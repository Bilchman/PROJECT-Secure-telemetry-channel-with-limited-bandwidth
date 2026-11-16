namespace Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public sealed record ChannelScenarioConfig(
    string Name,
    double LossProbability,
    double BurstLengthB,
    int DelayMinMs,
    int DelayMaxMs,
    int BandwidthBps,
    double TransmissionBudgetS,
    string RawJson
);

public sealed class TeamConfig
{
    public string TeamId { get; init; } = "T01";
    public int TeamSeed { get; init; } = 1001;
    public int FormatVersion { get; init; } = 1;
    public string Password { get; init; } = "algo2026";

    // Метрика Q
    public double Alpha { get; init; } = 0.5;
    public double Beta { get; init; } = 0.35;
    public double Gamma { get; init; } = 0.15;
    public double RefTimeSec { get; init; } = 20.0;

    // Пакування
    public int MaxPacketBytes { get; init; } = 512;
    public int MaxPayloadBytes { get; init; } = 437;
    public int CandidateWindowFrames { get; init; } = 64;

    // Пріоритети
    public IReadOnlyDictionary<FrameClass, int> FramePriorities { get; init; } =
        new Dictionary<FrameClass, int>
        {
            [FrameClass.Normal] = 1,
            [FrameClass.Transition] = 3,
            [FrameClass.Anomaly] = 5
        };
    public int CriticalBonus { get; init; } = 5;

    // Перцептрон
    public double F1Min { get; init; } = 0.58;
    public double BaselineMajorityF1 { get; init; } = 0.2925;
    public double BaselineRandomF1 { get; init; } = 0.3345;
    public int Initializations { get; init; } = 3;

    // Загальний ліміт часу
    public double MaxRunTimeSec { get; init; } = 120.0;

    // Канал
    public int ChannelSeed { get; init; } = 425243;
    public Dictionary<string, ChannelScenarioConfig> Scenarios { get; init; } = new();
    public Dictionary<string, double> CriticalLossThresholds { get; init; } = new();

    public static string FindRepoFile(params string[] relativeOptions)
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
                    if (File.Exists(candidate))
                        return candidate;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        throw new FileNotFoundException($"Не знайдено файл: {string.Join(" або ", relativeOptions)}");
    }

    public static TeamConfig Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var q = root.GetProperty("метрика_Q");
        var pack = root.GetProperty("пакування");
        var prio = root.GetProperty("пріоритети_кадрів");
        var ch = root.GetProperty("канал");
        var perc = root.GetProperty("перцептрон");
        var thresh = root.GetProperty("пороги_втрати_критичних_кадрів");

        var scenarios = new Dictionary<string, ChannelScenarioConfig>();
        foreach (var prop in ch.GetProperty("сценарії").EnumerateObject())
        {
            var s = prop.Value;
            double burstB = 1.0;
            if (s.TryGetProperty("втрати_пачками", out var burstObj) &&
                burstObj.TryGetProperty("середня_довжина_пачки", out var bVal))
            {
                burstB = bVal.GetDouble();
            }

            var delayObj = s.GetProperty("delay_ms");
            int delayMin, delayMax;
            if (delayObj.GetProperty("тип").GetString() == "стала")
            {
                delayMin = delayMax = delayObj.GetProperty("значення").GetInt32();
            }
            else
            {
                delayMin = delayObj.GetProperty("від").GetInt32();
                delayMax = delayObj.GetProperty("до").GetInt32();
            }

            scenarios[prop.Name] = new ChannelScenarioConfig(
                Name: prop.Name,
                LossProbability: s.GetProperty("loss_probability").GetDouble(),
                BurstLengthB: burstB,
                DelayMinMs: delayMin,
                DelayMaxMs: delayMax,
                BandwidthBps: s.GetProperty("bandwidth_bps").GetInt32(),
                TransmissionBudgetS: s.GetProperty("transmission_budget_s").GetDouble(),
                RawJson: s.GetRawText()
            );
        }

        var thresholds = new Dictionary<string, double>();
        foreach (var prop in thresh.EnumerateObject())
        {
            thresholds[prop.Name] = prop.Value.GetDouble();
        }

        return new TeamConfig
        {
            TeamId = root.GetProperty("команда").GetString() ?? "T01",
            TeamSeed = root.GetProperty("seed").GetInt32(),
            FormatVersion = root.GetProperty("версія_формату").GetInt32(),
            Password = root.GetProperty("пароль_сеансу").GetString() ?? "algo2026",
            Alpha = q.GetProperty("alpha").GetDouble(),
            Beta = q.GetProperty("beta").GetDouble(),
            Gamma = q.GetProperty("gamma").GetDouble(),
            RefTimeSec = q.GetProperty("t_ref_секунд").GetDouble(),
            MaxPacketBytes = pack.GetProperty("max_packet_bytes").GetInt32(),
            MaxPayloadBytes = pack.GetProperty("max_payload_bytes").GetInt32(),
            CandidateWindowFrames = pack.GetProperty("candidate_window_frames").GetInt32(),
            FramePriorities = new Dictionary<FrameClass, int>
            {
                [FrameClass.Normal] = prio.GetProperty("норма").GetInt32(),
                [FrameClass.Transition] = prio.GetProperty("перехідний_режим").GetInt32(),
                [FrameClass.Anomaly] = prio.GetProperty("аномалія").GetInt32()
            },
            CriticalBonus = prio.GetProperty("critical_бонус").GetInt32(),
            F1Min = perc.GetProperty("F1_min").GetDouble(),
            BaselineMajorityF1 = perc.GetProperty("базова_лінія_мажоритарний_клас").GetDouble(),
            BaselineRandomF1 = perc.GetProperty("базова_лінія_випадковий_вибір").GetDouble(),
            Initializations = perc.GetProperty("протокол_навчання").GetProperty("ініціалізацій").GetInt32(),
            MaxRunTimeSec = root.GetProperty("ліміт_часу_повного_прогону_секунд").GetDouble(),
            ChannelSeed = ch.GetProperty("seed_моделі_каналу").GetInt32(),
            Scenarios = scenarios,
            CriticalLossThresholds = thresholds
        };
    }

    public PythonChannel CreatePythonChannel(string scenarioName, string? channelPyPath = null)
    {
        if (!Scenarios.TryGetValue(scenarioName, out var sc))
            throw new KeyNotFoundException($"Сценарій '{scenarioName}' не знайдено в team_config.json.");

        string pyPath = channelPyPath ?? FindRepoFile("channel.py", Path.Combine("data", "channel.py"));
        return new PythonChannel(pyPath, sc.RawJson, ChannelSeed);
    }
}