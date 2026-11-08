namespace Core;

public static class TelemetryReader
{
    public static List<TelemetryFrame> ReadCsv(string filePath)
    {
        var frames = new List<TelemetryFrame>();
        var lines = File.ReadAllLines(filePath);

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var parts = line.Split(',');
            frames.Add(new TelemetryFrame(
                TimestampMs: uint.Parse(parts[0]),
                AltitudeDm: short.Parse(parts[1]),
                TempC10: short.Parse(parts[2]),
                Vibration: short.Parse(parts[3]),
                Mode: byte.Parse(parts[4]),
                Critical: byte.Parse(parts[5])
            ));
        }

        return frames;
    }

    // Зчитує бінарний потік телеметрії (12 байтів на кадр).
    // Порожній потік повертає порожній список без помилок (Вимога C1.2).
    public static List<TelemetryFrame> ReadBinary(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var frames = new List<TelemetryFrame>();
        byte[] buffer = new byte[TelemetryFrame.SizeInBytes];

        while (true)
        {
            int bytesRead = 0;
            while (bytesRead < TelemetryFrame.SizeInBytes)
            {
                int read = stream.Read(buffer, bytesRead, TelemetryFrame.SizeInBytes - bytesRead);
                if (read == 0) break;
                bytesRead += read;
            }

            if (bytesRead == 0) break; // Нормальний кінець потоку

            if (bytesRead < TelemetryFrame.SizeInBytes)
                throw new ArgumentException($"Потік обірвано: отримано неповний кадр ({bytesRead} байтів замість 12).");

            frames.Add(TelemetryFrame.ReadFrom(buffer));
        }

        return frames;
    }

    public static List<TelemetryFrame> ReadBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return new List<TelemetryFrame>();

        if (bytes.Length % TelemetryFrame.SizeInBytes != 0)
            throw new ArgumentException($"Довжина даних ({bytes.Length} Б) не кратна 12 байтам.");

        int count = bytes.Length / TelemetryFrame.SizeInBytes;
        var frames = new List<TelemetryFrame>(count);

        for (int i = 0; i < count; i++)
        {
            frames.Add(TelemetryFrame.ReadFrom(bytes.Slice(i * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes)));
        }

        return frames;
    }
}