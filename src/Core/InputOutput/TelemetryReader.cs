namespace Core;

using System;
using System.Collections.Generic;
using System.IO;

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

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 6)
                throw new FormatException($"Недостатньо колонок у рядку {i + 1} файлу {filePath}.");

            var frame = new TelemetryFrame(
                TimestampMs: uint.Parse(parts[0]),
                AltitudeDm: short.Parse(parts[1]),
                TempC10: short.Parse(parts[2]),
                Vibration: short.Parse(parts[3]),
                Mode: byte.Parse(parts[4]),
                Critical: byte.Parse(parts[5])
            );

            if (!frame.IsValid)
                throw new ArgumentException($"Некоректні значення кадру в рядку {i + 1}: mode={frame.Mode}, critical={frame.Critical}");

            frames.Add(frame);
        }

        return frames;
    }

    // Зчитує train.csv, valid.csv або session.csv разом із 7-ю колонкою label (0, 1, 2)
    public static List<(TelemetryFrame Frame, int Label)> ReadLabeledCsv(string filePath)
    {
        var result = new List<(TelemetryFrame, int)>();
        var lines = File.ReadAllLines(filePath);

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 7)
                throw new FormatException($"Очікувалось 7 колонок (з label) у рядку {i + 1} файлу {filePath}.");

            var frame = new TelemetryFrame(
                TimestampMs: uint.Parse(parts[0]),
                AltitudeDm: short.Parse(parts[1]),
                TempC10: short.Parse(parts[2]),
                Vibration: short.Parse(parts[3]),
                Mode: byte.Parse(parts[4]),
                Critical: byte.Parse(parts[5])
            );

            if (!frame.IsValid)
                throw new ArgumentException($"Некоректні значення кадру в рядку {i + 1}: mode={frame.Mode}, critical={frame.Critical}");

            int label = int.Parse(parts[6]);
            if (label < 0 || label > 2)
                throw new ArgumentException($"Некоректна мітка класу в рядку {i + 1}: label={label}");

            result.Add((frame, label));
        }

        return result;
    }

    // Зчитує потік кадрів із бінарного файлу/потоку (12 байтів на кадр).
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

            if (bytesRead == 0) break;

            if (bytesRead < TelemetryFrame.SizeInBytes)
                throw new ArgumentException($"Потік обірвано: прочитано неповний кадр ({bytesRead} байтів замість 12).");

            frames.Add(TelemetryFrame.ReadFrom(buffer));
        }

        return frames;
    }

    public static List<TelemetryFrame> ReadBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return new List<TelemetryFrame>();

        if (bytes.Length % TelemetryFrame.SizeInBytes != 0)
            throw new ArgumentException($"Довжина буфера ({bytes.Length} Б) не кратна 12 байтам.");

        int count = bytes.Length / TelemetryFrame.SizeInBytes;
        var frames = new List<TelemetryFrame>(count);

        for (int i = 0; i < count; i++)
        {
            frames.Add(TelemetryFrame.ReadFrom(bytes.Slice(i * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes)));
        }

        return frames;
    }
}