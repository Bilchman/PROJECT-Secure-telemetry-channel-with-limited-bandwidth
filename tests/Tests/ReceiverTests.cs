namespace Tests;

using System;
using Core;
using Xunit;

public sealed class ReceiverTests
{
    [Fact]
    public void Receive_ReferenceSingleFramePacket_RestoresExactFrame()
    {
        byte[] ref01Packet = Convert.FromHexString(
            "544C4D3101000100000000" +
            "000102030405060708090A0B0C0D0E0F" +
            "010203040506070800000000" +
            "00000020" +
            "8E51D20BA4B608DFA5B8A5748C5410989DB93D2BFD04DD1CEEB0C925DAE946AF" +
            "DD881BA276FB275C767958046D3EB9B75D4F7C95EFA3F8CAC0B9411E18461A8E"
        );

        var receiver = new TelemetryReceiver();
        var result = receiver.Receive(ref01Packet, "algo2026");

        Assert.True(result.IsAccepted);
        Assert.Equal(RejectionCode.None, result.Code);
        Assert.Single(result.Frames);

        var frame = result.Frames[0];
        Assert.Equal(0u, frame.TimestampMs);
        Assert.Equal((short)1000, frame.AltitudeDm);
        Assert.Equal((short)215, frame.TempC10);
        Assert.Equal((short)-3, frame.Vibration);
        Assert.Equal((byte)1, frame.Mode);
        Assert.Equal((byte)1, frame.Critical);
    }

    [Fact]
    public void Receive_EmptyFrameCountPacket_AcceptedWithZeroFrames()
    {
        var security = new SecurityService();
        byte[] salt = SecurityService.GenerateSalt();
        var (kEnc, kMac) = security.DeriveKeys("algo2026", salt);

        var payload = new PacketPayload(
            FrameCount: 0,
            Flags: 0x00,
            Table: new CodeTable { Entries = Array.Empty<(byte, byte)>() },
            SymbolCount: 0,
            Bitstream: Array.Empty<byte>()
        );
        byte[] payloadBytes = payload.Serialize();
        byte[] nonce = CipherService.CreateNonce([1, 2, 3, 4, 5, 6, 7, 8], 0);
        byte[] packet = security.Protect(new PacketHeader(1, 0, salt, nonce, (uint)payloadBytes.Length), payloadBytes, kEnc, kMac);

        var result = new TelemetryReceiver().Receive(packet, "algo2026");

        Assert.True(result.IsAccepted);
        Assert.Empty(result.Frames);
    }

    [Fact]
    public void Receive_NoCompressionAndClassTable_AcceptedAndDecoded()
    {
        var security = new SecurityService();
        byte[] salt = SecurityService.GenerateSalt();
        var (kEnc, kMac) = security.DeriveKeys("algo2026", salt);

        var expectedFrame = new TelemetryFrame(500, 1200, 210, -15, 2, 1);
        byte[] rawFrame = new byte[TelemetryFrame.SizeInBytes];
        expectedFrame.WriteTo(rawFrame);

        // NO_COMPRESSION + CLASS_TABLE
        var payload = new PacketPayload(
            FrameCount: 1,
            Flags: 0x0C,
            Table: new CodeTable { Entries = Array.Empty<(byte, byte)>() },
            SymbolCount: 12,
            Bitstream: rawFrame,
            ClassTable: [(byte)FrameClass.Anomaly]
        );
        byte[] payloadBytes = payload.Serialize();
        byte[] nonce = CipherService.CreateNonce([8, 7, 6, 5, 4, 3, 2, 1], 1);
        byte[] packet = security.Protect(new PacketHeader(1, 1, salt, nonce, (uint)payloadBytes.Length), payloadBytes, kEnc, kMac);

        var result = new TelemetryReceiver().Receive(packet, "algo2026");

        Assert.True(result.IsAccepted);
        Assert.Single(result.Frames);
        Assert.Equal(expectedFrame, result.Frames[0]);
    }

    [Fact]
    public void Receive_OversizedPacketOver512Bytes_AcceptedWithoutError()
    {
        // Імітація кейса пакет більший за max_packet_bytes = 512, напр. ~770+ байтів
        var security = new SecurityService();
        byte[] salt = SecurityService.GenerateSalt();
        var (kEnc, kMac) = security.DeriveKeys("algo2026", salt);

        const int frameCount = 64;
        byte[] raw = new byte[frameCount * TelemetryFrame.SizeInBytes];
        for (int i = 0; i < frameCount; i++)
        {
            new TelemetryFrame((uint)(i * 50), 1000, 200, (short)(i - 20), 1, 0)
                .WriteTo(raw.AsSpan(i * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes));
        }

        var huffman = new Huffman();
        var table = huffman.BuildTable(raw);
        var bitstream = huffman.Compress(raw, table);

        var payload = new PacketPayload(frameCount, 0x00, table, (uint)raw.Length, bitstream);
        byte[] payloadBytes = payload.Serialize();
        byte[] nonce = CipherService.CreateNonce([1, 1, 2, 2, 3, 3, 4, 4], 0);
        byte[] packet = security.Protect(new PacketHeader(1, 0, salt, nonce, (uint)payloadBytes.Length), payloadBytes, kEnc, kMac);

        Assert.True(packet.Length > 512);

        var result = new TelemetryReceiver().Receive(packet, "algo2026");
        Assert.True(result.IsAccepted);
        Assert.Equal(frameCount, result.Frames.Count);
    }

    [Fact]
    public void Receive_ReservedFlagOrReservedByteNonZero_ReturnsEPayload()
    {
        var security = new SecurityService();
        byte[] salt = SecurityService.GenerateSalt();
        var (kEnc, kMac) = security.DeriveKeys("algo2026", salt);

        var payload = new PacketPayload(0, 0x00, new CodeTable { Entries = Array.Empty<(byte, byte)>() }, 0, Array.Empty<byte>());
        byte[] badPayload = payload.Serialize();
        badPayload[3] = 0x01;

        byte[] nonce = CipherService.CreateNonce([1, 2, 3, 4, 5, 6, 7, 8], 0);
        byte[] packet = security.Protect(new PacketHeader(1, 0, salt, nonce, (uint)badPayload.Length), badPayload, kEnc, kMac);

        var result = new TelemetryReceiver().Receive(packet, "algo2026");
        Assert.False(result.IsAccepted);
        Assert.Equal(RejectionCode.E_PAYLOAD, result.Code);
    }

    [Fact]
    public void Receive_UnknownExtensionFlag_ReturnsEPayload()
    {
        var security = new SecurityService();
        byte[] salt = SecurityService.GenerateSalt();
        var (kEnc, kMac) = security.DeriveKeys("algo2026", salt);

        var payload = new PacketPayload(0, 0x01, new CodeTable { Entries = Array.Empty<(byte, byte)>() }, 0, Array.Empty<byte>());
        byte[] payloadBytes = payload.Serialize();

        byte[] nonce = CipherService.CreateNonce([1, 2, 3, 4, 5, 6, 7, 8], 0);
        byte[] packet = security.Protect(new PacketHeader(1, 0, salt, nonce, (uint)payloadBytes.Length), payloadBytes, kEnc, kMac);

        var result = new TelemetryReceiver().Receive(packet, "algo2026");
        Assert.False(result.IsAccepted);
        Assert.Equal(RejectionCode.E_PAYLOAD, result.Code);
    }
}