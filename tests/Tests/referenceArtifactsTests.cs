namespace Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Core;
using Xunit;

public sealed class ReferenceArtifactsTests
{
    private static string FindReferenceDir()
    {
        string?[] roots = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
        foreach (var start in roots)
        {
            string? dir = start;
            while (dir != null)
            {
                string candidate1 = Path.Combine(dir, "reference");
                if (Directory.Exists(candidate1)) return candidate1;

                string candidate2 = Path.Combine(dir, "data", "reference");
                if (Directory.Exists(candidate2)) return candidate2;

                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        throw new DirectoryNotFoundException("Папку reference/ не знайдено в репозиторії.");
    }

    private static string? FindOptionalFile(params string[] relativeOptions)
    {
        string?[] roots = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
        foreach (var start in roots)
        {
            string? dir = start;
            while (dir != null)
            {
                foreach (var rel in relativeOptions)
                {
                    string candidate = Path.Combine(dir, rel);
                    if (File.Exists(candidate)) return candidate;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        return null;
    }

    [Fact]
    public void All_Reference_Packets_Are_Accepted_And_Match_Expected_Frames()
    {
        string refDir = FindReferenceDir();
        string[] jsonFiles = Directory.GetFiles(refDir, "ref_*.json");
        Assert.NotEmpty(jsonFiles);

        var receiver = new TelemetryReceiver();

        foreach (string jsonPath in jsonFiles)
        {
            string binPath = Path.ChangeExtension(jsonPath, ".bin");
            Assert.True(File.Exists(binPath), $"Не знайдено бінарний файл: {binPath}");

            byte[] packetBytes = File.ReadAllBytes(binPath);
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;

            int expectedLen = root.GetProperty("довжина_пакета").GetInt32();
            Assert.Equal(expectedLen, packetBytes.Length);

            string password = root.GetProperty("пароль").GetString()!;
            var expectedFrames = new List<TelemetryFrame>();
            foreach (var f in root.GetProperty("кадри").EnumerateArray())
            {
                expectedFrames.Add(new TelemetryFrame(
                    TimestampMs: f.GetProperty("timestamp_ms").GetUInt32(),
                    AltitudeDm: f.GetProperty("altitude_dm").GetInt16(),
                    TempC10: f.GetProperty("temp_c10").GetInt16(),
                    Vibration: f.GetProperty("vibration").GetInt16(),
                    Mode: f.GetProperty("mode").GetByte(),
                    Critical: f.GetProperty("critical").GetByte()
                ));
            }

            ReceiveResult result = receiver.Receive(packetBytes, password);

            Assert.True(result.IsAccepted, $"Файл {Path.GetFileName(binPath)} відхилено з кодом {result.Code}");
            Assert.Equal(RejectionCode.None, result.Code);
            Assert.Equal(expectedFrames, result.Frames);
        }
    }

    [Fact]
    public void All_Bad_Reference_Packets_Return_Expected_Rejection_Codes()
    {
        string refDir = FindReferenceDir();
        string failuresPath = Path.Combine(refDir, "expected_failures.json");
        Assert.True(File.Exists(failuresPath), $"Не знайдено {failuresPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(failuresPath));
        var receiver = new TelemetryReceiver();
        int checkedCount = 0;

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            string binPath = Path.Combine(refDir, entry.Name);
            Assert.True(File.Exists(binPath), $"Не знайдено пошкоджений пакет: {binPath}");

            string expectedCodeStr = entry.Value.GetProperty("очікуваний_код").GetString()!;
            RejectionCode expectedCode = Enum.Parse<RejectionCode>(expectedCodeStr);

            byte[] badPacket = File.ReadAllBytes(binPath);
            ReceiveResult result = receiver.Receive(badPacket, "algo2026");

            Assert.False(result.IsAccepted, $"Пакет {entry.Name} мав бути відхилений!");
            Assert.Equal(expectedCode, result.Code);
            Assert.Empty(result.Frames);
            checkedCount++;
        }

        Assert.True(checkedCount >= 5);
    }

    [Fact]
    public void Reference_Packet_With_Wrong_Password_Returns_EHmac_Without_Decoding()
    {
        string refDir = FindReferenceDir();
        string binPath = Path.Combine(refDir, "ref_04_session.bin");
        byte[] packet = File.ReadAllBytes(binPath);

        var receiver = new TelemetryReceiver();
        ReceiveResult result = receiver.Receive(packet, "wrong_password_2026");

        Assert.False(result.IsAccepted);
        Assert.Equal(RejectionCode.E_HMAC, result.Code);
        Assert.Empty(result.Frames);
    }

    [Fact]
    public void SessionBin_Matches_SessionCsv_Exactly()
    {
        string? binPath = FindOptionalFile(Path.Combine("data", "session.bin"), "session.bin");
        string? csvPath = FindOptionalFile(Path.Combine("data", "session.csv"), "session.csv");

        if (binPath == null || csvPath == null)
            return;

        using var fs = File.OpenRead(binPath);
        var binFrames = TelemetryReader.ReadBinary(fs);
        var csvFrames = TelemetryReader.ReadCsv(csvPath);

        Assert.Equal(csvFrames.Count, binFrames.Count);
        Assert.Equal(csvFrames, binFrames);
    }
}