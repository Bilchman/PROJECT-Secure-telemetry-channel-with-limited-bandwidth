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

public sealed class SessionRunner
{
    private readonly IPredictor _predictor;
    private readonly ICompressor _compressor;
    private readonly IPacker _packer;
    private readonly ISecurity _security;
    private readonly IChannel _channel;
    private readonly IScheduler _scheduler;

    public SessionRunner(
        IChannel channel,
        ICompressor compressor,
        IPacker packer,
        IPredictor predictor,
        ISecurity security,
        IScheduler scheduler)
    {
        _compressor = compressor;
        _packer = packer;
        _predictor = predictor;
        _security = security;
        _scheduler = scheduler;

        _channel = channel;
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

        // Сіль задається один раз на сеанс
        byte[] sessionSalt = new byte[16];
        var (kEnc, kMac) = _security.DeriveKeys("algo2026", sessionSalt);

        // Префікс nonce з 8 байтів сеансу
        byte[] sessionNoncePrefix = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        var restoredFrames = new List<TelemetryFrame>();
        long sentBits = 0;
        uint packetSeq = 0;

        var queue = inputFrames
            .Select((frame, index) => (GlobalIndex: index, Frame: frame))
            .ToList();

        // max_payload_bytes = 512 - 43 (Header) - 32 (HMAC) = 437
        const int maxPayloadBytes = 512 - 43 - 32;

        var preparedPackets = new List<Packet>();

        while (queue.Count > 0)
        {
            // 1. Вікно це перші 64 кадри з черги
            var window = queue.Take(64).ToList();

            var windowBytes = new byte[window.Count * 12];
            for (int j = 0; j < window.Count; j++)
            {
                window[j].Frame.WriteTo(windowBytes.AsSpan(j * 12, 12));
            }

            // 2. Єдина таблиця для вікна
            var table = _compressor.BuildTable(windowBytes);

            // 3. W = 8 * (max_payload_bytes - 14 - 2 * table_nsym)
            int capacityBits = 8 * (maxPayloadBytes - 14 - 2 * table.TableNSym);
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

            var windowOnlyFrames = window.Select(w => w.Frame).ToList();
            var classes = _predictor.Classify(windowOnlyFrames);

            // 4. Формування кандидатів за вагою та цінністю
            var candidates = new List<FrameCandidate>(window.Count);
            for (int j = 0; j < window.Count; j++)
            {
                int weightBits = 0;
                int offset = j * 12;
                for (int b = 0; b < 12; b++)
                {
                    weightBits += lengths[windowBytes[offset + b]];
                }

                int priority = classes[j] switch
                {
                    FrameClass.Anomaly => 30,
                    FrameClass.Transition => 20,
                    _ => 10
                };
                int value = priority + (window[j].Frame.IsCritical ? 50 : 0);

                candidates.Add(new FrameCandidate(j, weightBits, value));
            }

            // 5. Вибір кадрів пакувальником
            var selectedIndices = _packer.SelectFrames(candidates, capacityBits);
            if (selectedIndices.Count == 0)
            {
                break;
            }

            var chosen = selectedIndices.OrderBy(idx => idx).ToList();
            var selectedFrames = chosen.Select(idx => window[idx].Frame).ToList();
            var chosenGlobalIds = chosen.Select(idx => window[idx].GlobalIndex).ToHashSet();

            // 6. Необрані кадри вікна залишаються у черзі на наступну ітерацію
            queue.RemoveAll(item => chosenGlobalIds.Contains(item.GlobalIndex));

            // 7. Стиснення обраних кадрів таблицею
            var rawBytes = new byte[selectedFrames.Count * 12];
            for (int j = 0; j < selectedFrames.Count; j++)
            {
                selectedFrames[j].WriteTo(rawBytes.AsSpan(j * 12, 12));
            }

            var bitstream = _compressor.Compress(rawBytes, table);

            // 8. Payload
            byte flags = _compressor is NoCompression ? (byte)0x08 : (byte)0x00;
            var payload = new PacketPayload(
                FrameCount: (ushort)selectedFrames.Count,
                Flags: flags,
                Table: table,
                SymbolCount: (uint)(selectedFrames.Count * 12),
                Bitstream: bitstream
            );
            byte[] payloadBytes = payload.Serialize();

            // 9. Nonce за рекомендацією має складатися з 8 байтів сеансу та uint32_BE
            byte[] nonce = new byte[12];
            sessionNoncePrefix.CopyTo(nonce.AsSpan(0, 8));
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8, 4), packetSeq);

            var header = new PacketHeader(1, packetSeq, sessionSalt, nonce, (uint)payloadBytes.Length);
            packetSeq++;

            byte[] packetBytes = _security.Protect(header, payloadBytes, kEnc, kMac);

            int packetValue = candidates.Where(c => chosen.Contains(c.FrameIndex)).Sum(c => c.Value);

            // Додаємо пакет у список готових
            preparedPackets.Add(new Packet(
                Sequence: header.PacketSeq,
                Bytes: packetBytes,
                FrameIndexes: chosenGlobalIds.ToList(),
                Value: packetValue
            ));
        }

        // 10. Передача через канал
        var packetsToSend = _scheduler.OrderPackets(preparedPackets);

        foreach (var packet in packetsToSend)
        {
            sentBits += packet.Bytes.Length * 8;

            var channelResult = _channel.Send(packet.Bytes);
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

            for (int j = 0; j < decodedBytes.Length / 12; j++)
            {
                restoredFrames.Add(TelemetryFrame.ReadFrom(decodedBytes.AsSpan(j * 12, 12)));
            }
        }

        sw.Stop();
        double actualCpuTime = sw.Elapsed.TotalSeconds;

        int totalCritical = inputFrames.Count(f => f.IsCritical);
        int restoredCritical = restoredFrames.Count(f => f.IsCritical);
        long refBits = (long)inputFrames.Count * 12 * 8;

        // Впорядковуємо за TimestampMs для точного зіставлення
        var sortedRestored = restoredFrames.OrderBy(f => f.TimestampMs).ToList();
        bool isExactMatch = inputFrames.Count == sortedRestored.Count &&
                            inputFrames.SequenceEqual(sortedRestored);

        double S = (refBits + sentBits) > 0 ? (double)refBits / (refBits + sentBits) : 0.0;
        double R = totalCritical > 0 ? (double)restoredCritical / totalCritical : 1.0;
        double T = 1.0 / (1.0 + actualCpuTime);

        double Q = QualityMetric.Compute(
            refBits: refBits,
            sentBits: sentBits,
            restoredCritical: restoredCritical,
            totalCritical: totalCritical,
            refTimeSec: 1.0,
            actualTimeSec: actualCpuTime
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
}
