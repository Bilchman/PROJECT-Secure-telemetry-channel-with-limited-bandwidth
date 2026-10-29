namespace Core;

public sealed class SessionRunner
{
    private readonly IPredictor _predictor = new MajorityClassifier();
    private readonly ICompressor _compressor = new NoCompression();
    private readonly IPacker _packer = new SequentialPacker();
    private readonly IScheduler _scheduler = new SequentialQueue();
    private readonly ISecurity _security = new NoSecurity();

    public double Run(List<TelemetryFrame> inputFrames)
    {
        var restoredFrames = new List<TelemetryFrame>();
        int totalCritical = inputFrames.Count(f => f.IsCritical);
        int batchSize = 32;

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

            // 3. Формування Payload (розділ 3 FORMAT_SPEC.md)
            var payload = new PacketPayload(
                FrameCount: (ushort)batch.Count,
                Flags: 0x08, // прапорець NO_COMPRESSION для поточної заглушки
                Table: table,
                SymbolCount: (uint)(batch.Count * 12),
                Bitstream: bitstream
            );
            byte[] payloadBytes = payload.Serialize();

            // 4. Заголовок і шифрування
            var header = new PacketHeader(1, (uint)(i / batchSize), new byte[16], new byte[12], (uint)payloadBytes.Length);
            byte[] packetBytes = _security.Protect(header, payloadBytes, kEnc, kMac);

            // 5. Приймач: перевірка цілісності та дешифрування
            var cryptoResult = _security.Unprotect(packetBytes, kEnc, kMac);
            if (!cryptoResult.IsSuccess || cryptoResult.PlaintextPayload == null) 
                continue;

            // 6. Розбір корисного навантаження (Payload) за специфікацією
            var (code, parsedPayload) = PacketPayload.TryRead(cryptoResult.PlaintextPayload);
            if (code != RejectionCode.None || parsedPayload == null) 
                continue;

            // 7. Декодування та відновлення 12-байтних кадрів
            byte[] decodedBytes = _compressor.Decompress(
                parsedPayload.Bitstream, 
                parsedPayload.Table, 
                (int)parsedPayload.SymbolCount);

            for (int j = 0; j < decodedBytes.Length / 12; j++)
            {
                restoredFrames.Add(TelemetryFrame.ReadFrom(decodedBytes.AsSpan(j * 12, 12)));
            }
        }

        int restoredCritical = restoredFrames.Count(f => f.IsCritical);
        long refBits = inputFrames.Count * 12 * 8;
        long sentBits = restoredFrames.Count * 12 * 8;

        return QualityMetric.Compute(refBits, sentBits, restoredCritical, totalCritical, 1.0, 0.1);
    }
}