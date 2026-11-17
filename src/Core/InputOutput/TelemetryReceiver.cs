namespace Core;

using System;
using System.Collections.Generic;

public sealed class TelemetryReceiver
{
    private readonly ISecurity _security;
    private readonly ICompressor _compressor;

    public TelemetryReceiver(ISecurity? security = null, ICompressor? compressor = null)
    {
        _security = security ?? new SecurityService();
        _compressor = compressor ?? new Huffman();
    }

    public ReceiveResult Receive(ReadOnlySpan<byte> rawPacket, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (rawPacket.Length < 75)
            return new ReceiveResult(RejectionCode.E_SHORT, Array.Empty<TelemetryFrame>());

        var (headerCode, header) = PacketHeader.TryRead(rawPacket);
        if (headerCode != RejectionCode.None || header == null)
            return new ReceiveResult(headerCode, Array.Empty<TelemetryFrame>());

        long expectedLength = 43L + header.CiphertextLen + 32L;
        if (rawPacket.Length != expectedLength)
            return new ReceiveResult(RejectionCode.E_LENGTH, Array.Empty<TelemetryFrame>());

        var (kEnc, kMac) = _security.DeriveKeys(password, header.Salt);
        var cryptoResult = _security.Unprotect(rawPacket, kEnc, kMac);
        if (!cryptoResult.IsSuccess || cryptoResult.PlaintextPayload == null)
            return new ReceiveResult(cryptoResult.Code, Array.Empty<TelemetryFrame>());

        var (payloadCode, payload) = PacketPayload.TryRead(cryptoResult.PlaintextPayload);
        if (payloadCode != RejectionCode.None || payload == null)
            return new ReceiveResult(payloadCode, Array.Empty<TelemetryFrame>());

        byte[] decodedBytes;
        if ((payload.Flags & 0x08) != 0)
        {
            decodedBytes = payload.Bitstream;
        }
        else
        {
            try
            {
                decodedBytes = _compressor.Decompress(payload.Bitstream, payload.Table, (int)payload.SymbolCount);
            }
            catch (InvalidOperationException)
            {
                return new ReceiveResult(RejectionCode.E_CODE, Array.Empty<TelemetryFrame>());
            }
        }

        if (decodedBytes.Length != payload.FrameCount * TelemetryFrame.SizeInBytes)
            return new ReceiveResult(RejectionCode.E_PAYLOAD, Array.Empty<TelemetryFrame>());

        var frames = new List<TelemetryFrame>(payload.FrameCount);
        try
        {
            for (int i = 0; i < payload.FrameCount; i++)
            {
                frames.Add(TelemetryFrame.ReadFrom(
                    decodedBytes.AsSpan(i * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes)));
            }
        }
        catch (ArgumentException)
        {
            return new ReceiveResult(RejectionCode.E_PAYLOAD, Array.Empty<TelemetryFrame>());
        }

        return new ReceiveResult(RejectionCode.None, frames);
    }
}