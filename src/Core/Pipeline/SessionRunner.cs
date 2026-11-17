namespace Core;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;

public readonly record struct SessionResult(
    int TransmittedFrames,
    int RestoredFrames,
    bool IsExactMatch,
    long BRef,
    long BSent,
    double CpuTimeSec,
    double S,
    double R,
    double T,
    double Q,
    long DeliveredValue = 0,
    double PackerTimeMs = 0.0
);

public sealed class SessionRunner : IDisposable
{
    private readonly IPredictor _predictor;
    private readonly ICompressor _compressor;
    private readonly IPacker _packer;
    private readonly ISecurity _security;
    private readonly IChannel _channel;
    private readonly IScheduler _scheduler;
    private readonly SystemParameters? _parameters;
    private readonly TeamConfig _config;
    private readonly bool _ownsChannel;

    public SessionRunner(
        IChannel? channel = null,
        ICompressor? compressor = null,
        IPacker? packer = null,
        IPredictor? predictor = null,
        ISecurity? security = null,
        IScheduler? scheduler = null,
        SystemParameters? parameters = null,
        TeamConfig? config = null)
    {
        _config = config ?? LoadDefaultConfig();
        _compressor = compressor ?? new Huffman();
        _packer = packer ?? new KnapsackPacker();
        _predictor = predictor ?? new MajorityClassifier();
        _security = security ?? new NoSecurity();
        _scheduler = scheduler ?? new SequentialQueue();
        _parameters = parameters;

        if (channel != null)
        {
            _channel = channel;
            _ownsChannel = false;
        }
        else
        {
            _channel = _config.CreatePythonChannel("спокійний");
            _ownsChannel = true;
        }
    }

    private static TeamConfig LoadDefaultConfig()
    {
        try
        {
            string path = TeamConfig.FindRepoFile("team_config.json", Path.Combine("data", "team_config.json"));
            return TeamConfig.Load(path);
        }
        catch
        {
            return new TeamConfig();
        }
    }

    public SessionResult Run(List<TelemetryFrame> inputFrames)
    {
        var sw = Stopwatch.StartNew();

        if (inputFrames.Count == 0)
        {
            sw.Stop();
            return new SessionResult(0, 0, true, 0, 0, sw.Elapsed.TotalSeconds, 1.0, 1.0, 1.0, 1.0, 0, 0.0);
        }

        byte[] sessionSalt = SecurityService.GenerateSalt();
        var (kEnc, kMac) = _security.DeriveKeys(_config.Password, sessionSalt);

        // 1. Класифікація суцільного потоку один раз до пакування
        var classes = _predictor.Classify(inputFrames);

        // 2. Формування та пакування пакетів
        var (preparedPackets, packerTimeMs) = PackFrames(inputFrames, classes, sessionSalt, kEnc, kMac);

        // 3. Черга відправлення -> канал -> приймач
        var scheduledPackets = _scheduler.OrderPackets(preparedPackets);
        var (restoredFrames, sentBits, deliveredValue) = TransmitAndRestore(scheduledPackets, kEnc, kMac);

        sw.Stop();
        double cpuTime = sw.Elapsed.TotalSeconds;

        // 4. Підрахунок метрик
        int totalCritical = inputFrames.Count(f => f.IsCritical);
        int restoredCritical = restoredFrames.Count(f => f.IsCritical);
        long refBits = (long)inputFrames.Count * TelemetryFrame.SizeInBytes * 8;

        var sortedRestored = restoredFrames.OrderBy(f => f.TimestampMs).ToList();
        bool isExactMatch = inputFrames.Count == sortedRestored.Count && inputFrames.SequenceEqual(sortedRestored);

        double S = (refBits + sentBits) > 0 ? (double)refBits / (refBits + sentBits) : 1.0;
        double R = totalCritical > 0 ? (double)restoredCritical / totalCritical : 1.0;
        double T = _config.RefTimeSec / (_config.RefTimeSec + cpuTime);
        double Q = QualityMetric.Compute(refBits, sentBits, restoredCritical, totalCritical, _config.RefTimeSec, cpuTime, _config.Alpha, _config.Beta, _config.Gamma);

        return new SessionResult(
            inputFrames.Count,
            restoredFrames.Count,
            isExactMatch,
            refBits,
            sentBits,
            cpuTime,
            S,
            R,
            T,
            Q,
            deliveredValue,
            packerTimeMs
        );
    }

    private (List<Packet> Packets, double PackerTimeMs) PackFrames(
        IReadOnlyList<TelemetryFrame> inputFrames,
        FrameClass[] classes,
        byte[] sessionSalt,
        byte[] kEnc,
        byte[] kMac)
    {
        int windowSize = _parameters?.WindowFrames ?? _config.CandidateWindowFrames;
        double compressionThreshold = _parameters?.CompressionThreshold ?? 1.0;
        int criticalBonus = (int)Math.Round(_config.CriticalBonus * (_parameters?.CriticalWeight ?? 1.0));

        int maxPayloadBytes = _config.MaxPacketBytes - PacketHeader.SizeInBytes - 32;
        int rawPayloadLimit = maxPayloadBytes - PacketPayload.HeaderSize;
        int rawCapacityBits = 8 * rawPayloadLimit;

        byte[] sessionNoncePrefix = RandomNumberGenerator.GetBytes(8);
        var queue = inputFrames.Select((frame, idx) => (GlobalIndex: idx, Frame: frame, Class: classes[idx])).ToList();
        var packets = new List<Packet>();
        var packerSw = new Stopwatch();
        uint packetSeq = 0;

        while (queue.Count > 0)
        {
            var window = queue.Take(windowSize).ToList();
            var windowBytes = SerializeFrames(window.Select(w => w.Frame).ToList());

            var table = _compressor.BuildTable(windowBytes);
            int huffmanCapacityBits = 8 * (rawPayloadLimit - 2 * table.TableNSym);

            bool forceRaw = _compressor is NoCompression || table.Entries.Count == 0 || huffmanCapacityBits <= 0;
            var candidates = BuildCandidates(window, windowBytes, table, forceRaw, criticalBonus);
            int capacityBits = forceRaw ? rawCapacityBits : huffmanCapacityBits;

            IReadOnlyList<int> selected = Array.Empty<int>();
            if (capacityBits > 0)
            {
                packerSw.Start();
                selected = _packer.SelectFrames(candidates, capacityBits);
                packerSw.Stop();
            }

            // Якщо таблиця зайняла майже весь пакет і жоден кадр не вліз - відмова від стиснення
            if (selected.Count == 0 && !forceRaw)
            {
                forceRaw = true;
                candidates = BuildCandidates(window, windowBytes, table, useRawWeights: true, criticalBonus);
                packerSw.Start();
                selected = _packer.SelectFrames(candidates, rawCapacityBits);
                packerSw.Stop();
            }

            if (selected.Count == 0)
                break;

            var selectedFrames = new List<TelemetryFrame>(selected.Count);
            var selectedGlobalIds = new List<int>(selected.Count);
            int packetValue = 0;

            foreach (int idx in selected.OrderBy(i => i))
            {
                selectedFrames.Add(window[idx].Frame);
                selectedGlobalIds.Add(window[idx].GlobalIndex);
                packetValue += candidates[idx].Value;
            }

            var removeSet = selectedGlobalIds.ToHashSet();
            queue.RemoveAll(item => removeSet.Contains(item.GlobalIndex));

            byte[] rawBytes = SerializeFrames(selectedFrames);
            byte[]? compressed = forceRaw ? null : _compressor.Compress(rawBytes, table);

            bool refuseCompression = forceRaw ||
                (compressed!.Length + 2 * table.TableNSym >= rawBytes.Length * compressionThreshold &&
                 rawBytes.Length <= rawPayloadLimit);

            var payload = new PacketPayload(
                FrameCount: (ushort)selectedFrames.Count,
                Flags: refuseCompression ? (byte)0x08 : (byte)0x00,
                Table: refuseCompression ? new CodeTable { Entries = Array.Empty<(byte, byte)>() } : table,
                SymbolCount: (uint)rawBytes.Length,
                Bitstream: refuseCompression ? rawBytes : compressed!
            );
            byte[] payloadBytes = payload.Serialize();

            byte[] nonce = CipherService.CreateNonce(sessionNoncePrefix, packetSeq);

            var header = new PacketHeader(1, packetSeq++, sessionSalt, nonce, (uint)payloadBytes.Length);
            byte[] packetBytes = _security.Protect(header, payloadBytes, kEnc, kMac);

            packets.Add(new Packet(header.PacketSeq, packetBytes, selectedGlobalIds, packetValue));
        }

        return (packets, packerSw.Elapsed.TotalMilliseconds);
    }

    private (List<TelemetryFrame> Restored, long SentBits, long DeliveredValue) TransmitAndRestore(
        IReadOnlyList<Packet> packets,
        byte[] kEnc,
        byte[] kMac)
    {
        var restored = new List<TelemetryFrame>();
        long sentBits = 0;
        long deliveredValue = 0;

        foreach (var packet in packets)
        {
            var channelResult = _channel.Send(packet.Bytes);
            if (!channelResult.DroppedByBudget)
                sentBits += packet.Bytes.Length * 8;

            if (!channelResult.Delivered)
                continue;

            var cryptoResult = _security.Unprotect(packet.Bytes, kEnc, kMac);
            if (!cryptoResult.IsSuccess || cryptoResult.PlaintextPayload == null)
                continue;

            var (code, payload) = PacketPayload.TryRead(cryptoResult.PlaintextPayload);
            if (code != RejectionCode.None || payload == null)
                continue;

            byte[] decodedBytes = (payload.Flags & 0x08) != 0
                ? payload.Bitstream
                : _compressor.Decompress(payload.Bitstream, payload.Table, (int)payload.SymbolCount);

            for (int j = 0; j < decodedBytes.Length / TelemetryFrame.SizeInBytes; j++)
            {
                restored.Add(TelemetryFrame.ReadFrom(decodedBytes.AsSpan(j * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes)));
            }

            deliveredValue += packet.Value;
        }

        return (restored, sentBits, deliveredValue);
    }

    private List<FrameCandidate> BuildCandidates(
        List<(int GlobalIndex, TelemetryFrame Frame, FrameClass Class)> window,
        byte[] windowBytes,
        CodeTable table,
        bool useRawWeights,
        int criticalBonus)
    {
        int[] lengths = new int[256];
        if (useRawWeights)
        {
            Array.Fill(lengths, 8);
        }
        else
        {
            foreach (var (sym, len) in table.Entries)
                lengths[sym] = len;
        }

        var candidates = new List<FrameCandidate>(window.Count);
        for (int j = 0; j < window.Count; j++)
        {
            int weightBits = 0;
            int offset = j * TelemetryFrame.SizeInBytes;
            for (int b = 0; b < TelemetryFrame.SizeInBytes; b++)
                weightBits += lengths[windowBytes[offset + b]];

            int value = PerceptronClassifier.CalculateFrameValue(
                window[j].Class,
                window[j].Frame.Critical,
                _config.FramePriorities,
                criticalBonus
            );

            candidates.Add(new FrameCandidate(j, weightBits, value));
        }

        return candidates;
    }

    private static byte[] SerializeFrames(IReadOnlyList<TelemetryFrame> frames)
    {
        var bytes = new byte[frames.Count * TelemetryFrame.SizeInBytes];
        for (int i = 0; i < frames.Count; i++)
            frames[i].WriteTo(bytes.AsSpan(i * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes));
        return bytes;
    }

    public void Dispose()
    {
        if (_ownsChannel && _channel is IDisposable disposable)
            disposable.Dispose();
    }
}