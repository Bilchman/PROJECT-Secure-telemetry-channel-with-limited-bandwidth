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
}