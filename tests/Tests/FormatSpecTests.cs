namespace Tests;

using Core;
using Xunit;

public class FormatSpecTests
{
    [Fact]
    public void TelemetryFrame_Serializes_Exactly_To_12_Bytes_BigEndian()
    {
        // Приклад з розділу 7: timestamp=0, alt=1000, temp=215, vib=-3, mode=1, critical=1
        var frame = new TelemetryFrame(0, 1000, 215, -3, 1, 1);
        
        var buffer = new byte[12];
        frame.WriteTo(buffer);

        // Очікувані байти з розділу 7: 00 00 00 00 03 E8 00 D7 FF FD 01 01
        byte[] expected = [0x00, 0x00, 0x00, 0x00, 0x03, 0xE8, 0x00, 0xD7, 0xFF, 0xFD, 0x01, 0x01];
        Assert.Equal(expected, buffer);

        // Перевірка зворотного читання
        var restored = TelemetryFrame.ReadFrom(buffer);
        Assert.Equal(frame, restored);
    }

    [Fact]
    public void BitWriter_Packs_Bits_MSB_First_Matching_Spec_Example()
    {
        // Приклад бітового потоку з розділу 7: 31 біт -> 0C E6 DF 90
        var writer = new BitWriter();
        
        // Коди: 0, 0, 0, 0, 1100, 1110, 0, 1101, 101, 1111, 100, 100
        writer.WriteBits(0, 1);
        writer.WriteBits(0, 1);
        writer.WriteBits(0, 1);
        writer.WriteBits(0, 1);
        writer.WriteBits(0b1100, 4);
        writer.WriteBits(0b1110, 4);
        writer.WriteBits(0, 1);
        writer.WriteBits(0b1101, 4);
        writer.WriteBits(0b101, 3);
        writer.WriteBits(0b1111, 4);
        writer.WriteBits(0b100, 3);
        writer.WriteBits(0b100, 3);

        byte[] result = writer.ToArray();
        byte[] expected = [0x0C, 0xE6, 0xDF, 0x90];
        Assert.Equal(expected, result);
    }
}