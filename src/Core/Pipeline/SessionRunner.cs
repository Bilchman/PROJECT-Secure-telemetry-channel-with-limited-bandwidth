namespace Core;

using System;
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
    private readonly IPredictor _predictor = new MajorityClassifier();
    private readonly ICompressor _compressor;
    private readonly IPacker _packer = new SequentialPacker();
    private readonly IScheduler _scheduler = new SequentialQueue();
    private readonly ISecurity _security = new NoSecurity();
    private readonly IChannel _channel;

    public SessionRunner(IChannel? channel = null, ICompressor? compressor = null)
    {
        _compressor = compressor ?? new NoCompression();

        // Спокійний сценарій каналу
        _channel = channel ?? new Channel(
            lossProbability: 0.0,
            burstLengthB: 1.0,
            delayMs: 20,
            bandwidthBps: 64000,
            transmissionBudgetS: 1000.0,
            seed: 42
        );
    }

    public SessionResult Run(List<TelemetryFrame> inputFrames)
    {
        var sw = Stopwatch.StartNew();

        var restoredFrames = new List<TelemetryFrame>();
        int totalCritical = inputFrames.Count(f => f.IsCritical);
        int batchSize = 32;
        long sentBits = 0;

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

        var (kEnc, kMac) = _security.DeriveKeys("algo2026", new byte[16]);

        for (int i = 0; i < inputFrames.Count; i += batchSize)
        {
            var batch = inputFrames.Skip(i).Take(batchSize).ToList();

            // 1. Серіалізація 12-байтних кадрів
            var rawBytes = new byte[batch.Count * 12];
            for (int j = 0; j < batch.Count; j++)
            {
                batch[j].WriteTo(rawBytes.AsSpan(j * 12, 12));
            }

            // 2. Стиснення
            var table = _compressor.BuildTable(rawBytes);
            var bitstream = _compressor.Compress(rawBytes, table);

            // 3. Формування Payload
            byte flags = _compressor is NoCompression ? (byte)0x08 : (byte)0x00;
            var payload = new PacketPayload(
                FrameCount: (ushort)batch.Count,
                Flags: flags,
                Table: table,
                SymbolCount: (uint)(batch.Count * 12),
                Bitstream: bitstream
            );
            byte[] payloadBytes = payload.Serialize();

            // 4. Заголовок і шифрування
            var header = new PacketHeader(1, (uint)(i / batchSize), new byte[16], new byte[12], (uint)payloadBytes.Length);
            byte[] packetBytes = _security.Protect(header, payloadBytes, kEnc, kMac);

            // передача через канал
            sentBits += packetBytes.Length * 8;

            var channelResult = _channel.Send(packetBytes);
            if (!channelResult.Delivered) 
                continue;

            // приймач
            var cryptoResult = _security.Unprotect(packetBytes, kEnc, kMac);
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

        int restoredCritical = restoredFrames.Count(f => f.IsCritical);
        long refBits = (long)inputFrames.Count * 12 * 8;

        bool isExactMatch = inputFrames.Count == restoredFrames.Count &&
                            inputFrames.SequenceEqual(restoredFrames);

        // Обчислення компонент метрики якості
        double S = (refBits + sentBits) > 0 ? (double)refBits / (refBits + sentBits) : 0.0;
        double R = totalCritical > 0 ? (double)restoredCritical / totalCritical : 1.0;
        double T = 1.0 / (1.0 + actualCpuTime); // 1 c за замовчуванням має бути

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