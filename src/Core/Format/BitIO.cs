namespace Core;

public sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private byte _currentByte;
    private int _bitCount;

    public void WriteBits(uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            int bit = (int)((value >> i) & 1);
            _currentByte = (byte)((_currentByte << 1) | bit);
            _bitCount++;

            if (_bitCount == 8)
            {
                _bytes.Add(_currentByte);
                _currentByte = 0;
                _bitCount = 0;
            }
        }
    }

    public byte[] ToArray()
    {
        if (_bitCount == 0) return _bytes.ToArray();

        byte padded = (byte)(_currentByte << (8 - _bitCount));
        var result = new byte[_bytes.Count + 1];
        _bytes.CopyTo(result, 0);
        result[^1] = padded;
        return result;
    }
}

public ref struct BitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _byteIndex = 0;
    private int _bitIndex = 0;

    public bool TryReadBit(out int bit)
    {
        if (_byteIndex >= _data.Length)
        {
            bit = 0;
            return false;
        }

        bit = (_data[_byteIndex] >> (7 - _bitIndex)) & 1;
        _bitIndex++;

        if (_bitIndex == 8)
        {
            _bitIndex = 0;
            _byteIndex++;
        }

        return true;
    }
}