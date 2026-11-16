namespace Core;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

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
    double Q
);

public sealed class SessionRunner : IDisposable
{
    private readonly IPredictor _predictor;
    private readonly ICompressor _compressor;
    private readonly IPacker _packer;
    private readonly ISecurity _security;
    private readonly IChannel _channel;
    private readonly IScheduler _scheduler;
    private readonly TeamConfig _config;
    private readonly bool _ownsChannel;

    public SessionRunner(
        IChannel? channel = null,
        ICompressor? compressor = null,
        IPacker? packer = null,
        IPredictor? predictor = null,
        ISecurity? security = null,
        IScheduler? scheduler = null,
        TeamConfig? config = null)
    {
        _config = config ?? LoadDefaultConfig();
        _compressor = compressor ?? new Huffman();
        _packer = packer ?? new KnapsackPacker();
        _predictor = predictor ?? new MajorityClassifier();
        _security = security ?? new NoSecurity();
        _scheduler = scheduler ?? new SequentialQueue();

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
            return new SessionResult(
                TransmittedFrames: 0,
                RestoredFrames: 0,
                IsExactMatch: true,
                BRef: 0,
                BSent: 0,
                CpuTimeSec: sw.Elapsed.TotalSeconds,
                S: 1.0,
                R: 1.0,
                T: 1.0,
                Q: 1.0
            );
        }

        byte[] sessionSalt = new byte[16];
        var (kEnc, kMac) = _security.DeriveKeys(_config.Password, sessionSalt);

        byte[] sessionNoncePrefix = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        var restoredFrames = new List<TelemetryFrame>();
        long sentBits = 0;
        uint packetSeq = 0;

        // 1. Класифікуємо ВЕСЬ неперервний потік один раз до циклу пакування,
        // щоб зберегти точне ковзне вікно історії перцептрона
        var allClasses = _predictor.Classify(inputFrames);

        var queue = inputFrames
            .Select((frame, index) => (GlobalIndex: index, Frame: frame, Class: allClasses[index]))
            .ToList();

        int maxPayloadBytes = _config.MaxPacketBytes - PacketHeader.SizeInBytes - 32;

        var preparedPackets = new List<Packet>();

        while (queue.Count > 0)
        {
            // 2. Вікно кандидатів (candidate_window_frames = 64)
            var window = queue.Take(_config.CandidateWindowFrames).ToList();

            var windowBytes = new byte[window.Count * TelemetryFrame.SizeInBytes];
            for (int j = 0; j < window.Count; j++)
            {
                window[j].Frame.WriteTo(windowBytes.AsSpan(j * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes));
            }

            // 3. Єдина таблиця для вікна
            var table = _compressor.BuildTable(windowBytes);

            // 4. W = 8 * (max_payload_bytes - 14 - 2 * table_nsym)
            int capacityBits = 8 * (maxPayloadBytes - PacketPayload.HeaderSize - 2 * table.TableNSym);
            if (capacityBits <= 0)
            {
                break;
            }

            int[] lengths = new int[256];
            if (_compressor is NoCompression || table.Entries.Count == 0)
            {
                Array.Fill(lengths, 8);
            }
            else
            {
                foreach (var (sym, len) in table.Entries)
                {
                    lengths[sym] = len;
                }
            }

            // 5. Формування кандидатів за вагою w_i та цінністю v_i з team_config.json
            var candidates = new List<FrameCandidate>(window.Count);
            for (int j = 0; j < window.Count; j++)
            {
                int weightBits = 0;
                int offset = j * TelemetryFrame.SizeInBytes;
                for (int b = 0; b < TelemetryFrame.SizeInBytes; b++)
                {
                    weightBits += lengths[windowBytes[offset + b]];
                }

                int value = PerceptronClassifier.CalculateFrameValue(
                    window[j].Class,
                    window[j].Frame.Critical,
                    _config.FramePriorities,
                    _config.CriticalBonus
                );

                candidates.Add(new FrameCandidate(j, weightBits, value));
            }

            // 6. Вибір кадрів пакувальником
            var selectedIndices = _packer.SelectFrames(candidates, capacityBits);
            if (selectedIndices.Count == 0)
            {
                break;
            }

            var chosen = selectedIndices.OrderBy(idx => idx).ToList();
            var chosenSet = chosen.ToHashSet();
            var selectedFrames = chosen.Select(idx => window[idx].Frame).ToList();
            var chosenGlobalIds = chosen.Select(idx => window[idx].GlobalIndex).ToHashSet();

            // Необрані кадри вікна залишаються в черзі на наступну ітерацію
            queue.RemoveAll(item => chosenGlobalIds.Contains(item.GlobalIndex));

            // 7. Стиснення обраних кадрів ТІЄЮ САМОЮ таблицею
            var rawBytes = new byte[selectedFrames.Count * TelemetryFrame.SizeInBytes];
            for (int j = 0; j < selectedFrames.Count; j++)
            {
                selectedFrames[j].WriteTo(rawBytes.AsSpan(j * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes));
            }

            var bitstream = _compressor.Compress(rawBytes, table);

            // 8. Payload (CLASS_TABLE = 0 під час задачі пакування)
            byte flags = _compressor is NoCompression ? (byte)0x08 : (byte)0x00;
            var payload = new PacketPayload(
                FrameCount: (ushort)selectedFrames.Count,
                Flags: flags,
                Table: table,
                SymbolCount: (uint)(selectedFrames.Count * TelemetryFrame.SizeInBytes),
                Bitstream: bitstream
            );
            byte[] payloadBytes = payload.Serialize();

            // 9. Nonce = 8 байтів сеансу || uint32_BE(packet_seq)
            byte[] nonce = new byte[12];
            sessionNoncePrefix.CopyTo(nonce.AsSpan(0, 8));
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8, 4), packetSeq);

            var header = new PacketHeader(1, packetSeq, sessionSalt, nonce, (uint)payloadBytes.Length);
            packetSeq++;

            byte[] packetBytes = _security.Protect(header, payloadBytes, kEnc, kMac);

            int packetValue = candidates.Where(c => chosenSet.Contains(c.FrameIndex)).Sum(c => c.Value);

            preparedPackets.Add(new Packet(
                Sequence: header.PacketSeq,
                Bytes: packetBytes,
                FrameIndexes: chosenGlobalIds.ToList(),
                Value: packetValue
            ));
        }

        // 10. Планування черги і передача через еталонний channel.py
        var packetsToSend = _scheduler.OrderPackets(preparedPackets);

        foreach (var packet in packetsToSend)
        {
            var channelResult = _channel.Send(packet.Bytes);

            // Рахуємо лише біти, що реально потрапили в канал (не відкинуті бюджетом)
            if (!channelResult.DroppedByBudget)
            {
                sentBits += packet.Bytes.Length * 8;
            }

            if (!channelResult.Delivered)
                continue;

            var cryptoResult = _security.Unprotect(packet.Bytes, kEnc, kMac);
            if (!cryptoResult.IsSuccess || cryptoResult.PlaintextPayload == null)
                continue;

            var (code, parsedPayload) = PacketPayload.TryRead(cryptoResult.PlaintextPayload);
            if (code != RejectionCode.None || parsedPayload == null)
                continue;

            byte[] decodedBytes = _compressor.Decompress(
                parsedPayload.Bitstream,
                parsedPayload.Table,
                (int)parsedPayload.SymbolCount);

            for (int j = 0; j < decodedBytes.Length / TelemetryFrame.SizeInBytes; j++)
            {
                restoredFrames.Add(TelemetryFrame.ReadFrom(decodedBytes.AsSpan(j * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes)));
            }
        }

        sw.Stop();
        double actualCpuTime = sw.Elapsed.TotalSeconds;

        int totalCritical = inputFrames.Count(f => f.IsCritical);
        int restoredCritical = restoredFrames.Count(f => f.IsCritical);
        long refBits = (long)inputFrames.Count * TelemetryFrame.SizeInBytes * 8;

        var sortedRestored = restoredFrames.OrderBy(f => f.TimestampMs).ToList();
        bool isExactMatch = inputFrames.Count == sortedRestored.Count &&
                            inputFrames.SequenceEqual(sortedRestored);

        double S = (refBits + sentBits) > 0 ? (double)refBits / (refBits + sentBits) : 1.0;
        double R = totalCritical > 0 ? (double)restoredCritical / totalCritical : 1.0;
        double T = _config.RefTimeSec / (_config.RefTimeSec + actualCpuTime);

        double Q = QualityMetric.Compute(
            refBits: refBits,
            sentBits: sentBits,
            restoredCritical: restoredCritical,
            totalCritical: totalCritical,
            refTimeSec: _config.RefTimeSec,
            actualTimeSec: actualCpuTime,
            alpha: _config.Alpha,
            beta: _config.Beta,
            gamma: _config.Gamma
        );

        return new SessionResult(
            TransmittedFrames: inputFrames.Count,
            RestoredFrames: restoredFrames.Count,
            IsExactMatch: isExactMatch,
            BRef: refBits,
            BSent: sentBits,
            CpuTimeSec: actualCpuTime,
            S: S,
            R: R,
            T: T,
            Q: Q
        );
    }

    public void Dispose()
    {
        if (_ownsChannel && _channel is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}