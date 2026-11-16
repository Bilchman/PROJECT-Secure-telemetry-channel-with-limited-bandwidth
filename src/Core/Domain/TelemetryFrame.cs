namespace Core;

using System.Buffers.Binary;

public readonly record struct TelemetryFrame(
    uint  TimestampMs,
    short AltitudeDm,
    short TempC10,
    short Vibration,
    byte  Mode,
    byte  Critical)
{
    public const int SizeInBytes = 12;
    public bool IsCritical => Critical == 1;
    public bool IsValid => Mode <= 7 && Critical <= 1;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SizeInBytes)
            throw new ArgumentException($"Цільовий буфер закороткий: очікується щонайменше {SizeInBytes} байтів.");

        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(0, 4), TimestampMs);
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(4, 2), AltitudeDm);
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(6, 2), TempC10);
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(8, 2), Vibration);
        destination[10] = Mode;
        destination[11] = Critical;
    }

    public static TelemetryFrame ReadFrom(ReadOnlySpan<byte> source)
    {
        //Перевірка фіксованого розміру для запобігання IndexOutOfRangeException
        if (source.Length != SizeInBytes)
            throw new ArgumentException($"Розмір кадру має бути рівно {SizeInBytes} байтів, отримано {source.Length}.");

        var frame = new TelemetryFrame(
            BinaryPrimitives.ReadUInt32BigEndian(source.Slice(0, 4)),
            BinaryPrimitives.ReadInt16BigEndian(source.Slice(4, 2)),
            BinaryPrimitives.ReadInt16BigEndian(source.Slice(6, 2)),
            BinaryPrimitives.ReadInt16BigEndian(source.Slice(8, 2)),
            source[10],
            source[11]
        );

        if (!frame.IsValid)
            throw new ArgumentException($"Некоректні дані кадру: mode={frame.Mode}, critical={frame.Critical}");

        return frame;
    }
}