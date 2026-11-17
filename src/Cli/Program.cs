using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Core;
using Core.Prediction;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=======================================================");
Console.WriteLine("                  TelemetryLink CLI  ");
Console.WriteLine("=======================================================");

static List<(double[] Features, int Label)> ExtractFeaturesChronologically(
    IReadOnlyList<(TelemetryFrame Frame, int Label)> rows)
{
    var extractor = new FeatureExtractor(FeatureExtractor.DefaultWindowSize);
    var history = new List<TelemetryFrame>();
    var dataset = new List<(double[] Features, int Label)>(rows.Count);

    foreach (var (frame, label) in rows)
    {
        dataset.Add((extractor.ExtractFeatures(history, frame), label));
        history.Add(frame);
        if (history.Count > FeatureExtractor.DefaultWindowSize)
        {
            history.RemoveAt(0);
        }
    }

    return dataset;
}

var totalRunSw = Stopwatch.StartNew();

// 1. Завантаження конфігурації та даних
string configPath = TeamConfig.FindRepoFile("team_config.json", Path.Combine("data", "team_config.json"));
string trainPath = TeamConfig.FindRepoFile(Path.Combine("data", "train.csv"), "train.csv");
string validPath = TeamConfig.FindRepoFile(Path.Combine("data", "valid.csv"), "valid.csv");
string sessionPath = TeamConfig.FindRepoFile(Path.Combine("data", "session.csv"), "session.csv");
string channelPyPath = TeamConfig.FindRepoFile("channel.py", Path.Combine("data", "channel.py"));

var config = TeamConfig.Load(configPath);
Console.WriteLine($"[+] Команда: {config.TeamId} | seed={config.TeamSeed} | t_ref={config.RefTimeSec}с | F1_min={config.F1Min:F2} | channel_seed={config.ChannelSeed}");

string resultsDir = "results";
Directory.CreateDirectory(resultsDir);

// 2. Навчання перцептрона за протоколом (80/20, 3 ініціалізації)
var trainRaw = TelemetryReader.ReadLabeledCsv(trainPath);
var validRaw = TelemetryReader.ReadLabeledCsv(validPath);
var sessionLabeled = TelemetryReader.ReadLabeledCsv(sessionPath);
Console.WriteLine($"[+] Завантажено кадрів -> train: {trainRaw.Count}, valid: {validRaw.Count}, session: {sessionLabeled.Count}");

var trainDataset = ExtractFeaturesChronologically(trainRaw);
var validDataset = ExtractFeaturesChronologically(validRaw);
var sessionEvalDataset = ExtractFeaturesChronologically(sessionLabeled);

var (trainSplit, innerValSplit) = Trainer.SplitData(trainDataset, trainRatio: 0.8, seed: config.TeamSeed);
var trainer = new Trainer(FeatureExtractor.FeatureCount, hiddenSize: 16);

var initSeeds = new int[config.Initializations];
for (int i = 0; i < config.Initializations; i++)
{
    initSeeds[i] = config.TeamSeed + i * 100;
}

var (bestModel, innerMetrics) = trainer.TrainThreeSeeds(trainSplit, innerValSplit, initSeeds);
var validMetrics = Trainer.Evaluate(bestModel, validDataset, innerMetrics.BestSeed);
var sessionMetrics = Trainer.Evaluate(bestModel, sessionEvalDataset, innerMetrics.BestSeed);
Trainer.SaveArtifacts(resultsDir, validMetrics);

var report = new StringBuilder();
report.AppendLine("==================================================================================================");
report.AppendLine("                           РЕЗУЛЬТАТИ ПЕРЦЕПТРОНА (VALID.CSV / SESSION.CSV)");
report.AppendLine("==================================================================================================");
report.AppendLine($"Найкращий seed ініціалізації: {validMetrics.BestSeed}");
report.AppendLine($"Базові лінії з конфіга:       majority={config.BaselineMajorityF1:F4}, random={config.BaselineRandomF1:F4}");
report.AppendLine($"Inner Val Macro-F1 (80/20):   {innerMetrics.MacroF1:F4}");
report.AppendLine($"Open Valid Macro-F1:          {validMetrics.MacroF1:F4}  (поріг F1_min = {config.F1Min:F2}) -> {(validMetrics.MacroF1 >= config.F1Min ? "ПРОЙДЕНО" : "НЕ ПРОЙДЕНО")}");
report.AppendLine($"Session Macro-F1 (діагн.):    {sessionMetrics.MacroF1:F4}");
report.AppendLine($"  Valid F1 по класах: Normal(0)={validMetrics.F1PerClass[0]:F4}, Anomaly(1)={validMetrics.F1PerClass[1]:F4}, Transition(2)={validMetrics.F1PerClass[2]:F4}");
report.AppendLine("Матриця помилок на valid.csv (рядки = факт, стовпці = прогноз):");
for (int r = 0; r < 3; r++)
{
    report.AppendLine($"  Клас {r}: {validMetrics.ConfusionMatrix[r][0],5} {validMetrics.ConfusionMatrix[r][1],5} {validMetrics.ConfusionMatrix[r][2],5}");
}
report.AppendLine();

var frames = TelemetryReader.ReadCsv(sessionPath);
var predictor = new PerceptronClassifier(bestModel);
var security = new SecurityService();
string[] scenarioOrder = ["спокійний", "робочий", "аварійний"];

// ==================================================================================================
// 3. ТАБЛИЦЯ 1 (ДП проти жадібності) ТА ТАБЛИЦЯ 4 (Поведінка за втрат у 3 сценаріях)
// ==================================================================================================
Console.WriteLine("[*] Запуск прогонів по трьох сценаріях каналу...");

var table1Csv = new List<string> { "scenario,algorithm,delivered_value,packer_time_ms,greedy_error_percent,Q,R" };
var table4Csv = new List<string> { "scenario,restored_frames,total_frames,B_sent,S,R,T,Q,critical_loss_percent,threshold_percent,status" };

report.AppendLine("==================================================================================================");
report.AppendLine("              ПОРІВНЯННЯ ПАКУВАЛЬНИКІВ НА ЕТАЛОННОМУ КАНАЛІ (channel.py + AES-CTR + HMAC)");
report.AppendLine("==================================================================================================");

foreach (var scenarioName in scenarioOrder)
{
    var sc = config.Scenarios[scenarioName];
    double maxCritLoss = config.CriticalLossThresholds[scenarioName];

    report.AppendLine($"\n--- Сценарій: {scenarioName.ToUpper()} (loss={sc.LossProbability:P0}, B={sc.BurstLengthB}, delay={sc.DelayMinMs}..{sc.DelayMaxMs} мс, bw={sc.BandwidthBps} bps, budget={sc.TransmissionBudgetS} с, поріг втрати кр. <= {maxCritLoss:P0}) ---");
    report.AppendLine(string.Format("{0,-13} | {1,-11} | {2,-8} | {3,-10} | {4,-9} | {5,-8} | {6,-8} | {7,-8} | {8,-9} | {9}",
        "Пакувальник", "Відновлено", "R", "Втрата кр.", "B_sent", "S", "T", "Q", "Цінність", "Статус / Поза бюдж."));
    report.AppendLine(new string('-', 118));

    IPacker[] packers = [new SequentialPacker(), new GreedyPacker(), new KnapsackPacker()];
    var resultsByPacker = new Dictionary<string, SessionResult>();

    foreach (var packer in packers)
    {
        using var channel = config.CreatePythonChannel(scenarioName, channelPyPath);
        var runner = new SessionRunner(
            channel: channel,
            compressor: new Huffman(),
            packer: packer,
            predictor: predictor,
            security: security,
            scheduler: new PriorityScheduler(),
            config: config
        );

        var res = runner.Run(frames);
        resultsByPacker[packer.Name] = res;

        var stats = channel.GetStats();
        int overBudget = Convert.ToInt32(stats["не_вміщено_в_бюджет"]);

        double critLoss = 1.0 - res.R;
        string status = critLoss <= maxCritLoss + 1e-9 ? "OK" : "ПЕРЕВИЩЕНО";

        report.AppendLine(string.Format("{0,-13} | {1,-11} | {2,-8:F4} | {3,-10:P2} | {4,-9} | {5,-8:F4} | {6,-8:F4} | {7,-8:F4} | {8,-9} | {9} (over={10})",
            packer.Name, $"{res.RestoredFrames}/{res.TransmittedFrames}", res.R, critLoss, res.BSent, res.S, res.T, res.Q, res.DeliveredValue, status, overBudget));
    }

    var dp = resultsByPacker["dp-knapsack"];
    var gr = resultsByPacker["greedy"];
    double valErr = dp.DeliveredValue > 0 ? (double)(dp.DeliveredValue - gr.DeliveredValue) / dp.DeliveredValue * 100.0 : 0.0;

    table1Csv.Add(string.Format(CultureInfo.InvariantCulture, "{0},dp-knapsack,{1},{2:F3},0.00,{3:F4},{4:F4}",
        scenarioName, dp.DeliveredValue, dp.PackerTimeMs, dp.Q, dp.R));
    table1Csv.Add(string.Format(CultureInfo.InvariantCulture, "{0},greedy,{1},{2:F3},{3:F2},{4:F4},{5:F4}",
        scenarioName, gr.DeliveredValue, gr.PackerTimeMs, valErr, gr.Q, gr.R));

    double dpCritLossPct = (1.0 - dp.R) * 100.0;
    string dpStatus = (1.0 - dp.R) <= maxCritLoss + 1e-9 ? "OK" : "EXCEEDED";
    table4Csv.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4:F4},{5:F4},{6:F4},{7:F4},{8:F2},{9:F2},{10}",
        scenarioName, dp.RestoredFrames, dp.TransmittedFrames, dp.BSent, dp.S, dp.R, dp.T, dp.Q, dpCritLossPct, maxCritLoss * 100.0, dpStatus));
}

File.WriteAllLines(Path.Combine(resultsDir, "table1_dp_vs_greedy.csv"), table1Csv);
File.WriteAllLines(Path.Combine(resultsDir, "table4_loss_behavior.csv"), table4Csv);

// ==================================================================================================
// 4. ТАБЛИЦЯ 2: Вплив кожного реалізованого блоку (по черзі вимикаємо на Аварійному сценарії)
// ==================================================================================================
Console.WriteLine("[*] Запуск аналізу впливу кожного блоку (Таблиця 2)...");

SessionResult RunEmergencyConfig(
    ICompressor comp, IPacker pack, IPredictor pred, ISecurity sec, IScheduler sched)
{
    using var ch = config.CreatePythonChannel("аварійний", channelPyPath);
    var r = new SessionRunner(ch, comp, pack, pred, sec, sched, config: config);
    return r.Run(frames);
}

var ablationVariants = new (string Name, string DisabledBlock, ICompressor Comp, IPacker Pack, IPredictor Pred, ISecurity Sec, IScheduler Sched)[]
{
    ("Повна система",         "Немає",      new Huffman(),       new KnapsackPacker(),   predictor,                security,         new PriorityScheduler()),
    ("Без перцептрона",       "Прогноз",    new Huffman(),       new KnapsackPacker(),   new MajorityClassifier(), security,         new PriorityScheduler()),
    ("Без стиснення",         "Гаффман",    new NoCompression(), new KnapsackPacker(),   predictor,                security,         new PriorityScheduler()),
    ("Без ДП-рюкзака",        "Пакування",  new Huffman(),       new SequentialPacker(), predictor,                security,         new PriorityScheduler()),
    ("Без планувальника",     "Черга",      new Huffman(),       new KnapsackPacker(),   predictor,                security,         new SequentialQueue()),
    ("Без шифрування",        "Захист",     new Huffman(),       new KnapsackPacker(),   predictor,                new NoSecurity(), new PriorityScheduler())
};

var table2Csv = new List<string> { "variant,disabled_block,S,R,T,Q,delta_Q" };
report.AppendLine();
report.AppendLine("==================================================================================================");
report.AppendLine("                     ТАБЛИЦЯ 2: ВПЛИВ КОЖНОГО БЛОКУ (АВАРІЙНИЙ СЦЕНАРІЙ)");
report.AppendLine("==================================================================================================");
report.AppendLine(string.Format("{0,-20} | {1,-24} | {2,-8} | {3,-8} | {4,-8} | {5,-8} | {6}",
    "Конфігурація", "Вимкнений блок", "S", "R", "T", "Q", "ΔQ від повної"));
report.AppendLine(new string('-', 98));

double fullQ = 0.0;
for (int i = 0; i < ablationVariants.Length; i++)
{
    var v = ablationVariants[i];
    var res = RunEmergencyConfig(v.Comp, v.Pack, v.Pred, v.Sec, v.Sched);
    if (i == 0) fullQ = res.Q;
    double deltaQ = res.Q - fullQ;

    table2Csv.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F4},{3:F4},{4:F4},{5:F4},{6:+0.0000;-0.0000;0.0000}",
        v.Name, v.DisabledBlock, res.S, res.R, res.T, res.Q, deltaQ));

    report.AppendLine(string.Format("{0,-20} | {1,-24} | {2,-8:F4} | {3,-8:F4} | {4,-8:F4} | {5,-8:F4} | {6:+0.0000;-0.0000;0.0000}",
        v.Name, v.DisabledBlock, res.S, res.R, res.T, res.Q, deltaQ));
}

File.WriteAllLines(Path.Combine(resultsDir, "table2_ablation_blocks.csv"), table2Csv);

// ==================================================================================================
// 5. ТАБЛИЦЯ 3: Порядок операцій ("стиснути -> зашифрувати" vs "зашифрувати -> стиснути")
// ==================================================================================================
Console.WriteLine("[*] Запуск експерименту порядку криптооперацій (Таблиця 3)...");

var huffmanExp = new Huffman();
byte[] expSalt = SecurityService.GenerateSalt();
var (expKEnc, _) = KeyDerivation.DeriveKeys(config.Password, expSalt);
byte[] expNonce = CipherService.CreateNonce(RandomNumberGenerator.GetBytes(8), 1);

int[] expBatchSizes = [32, 64, 128, 256, 512, 1000, frames.Count];
var table3Csv = new List<string>
{
    "dataset,frame_count,raw_bytes,compress_then_encrypt_bytes,compress_then_encrypt_ratio,encrypt_then_compress_bytes,encrypt_then_compress_ratio,savings_percent"
};

report.AppendLine();
report.AppendLine("==================================================================================================");
report.AppendLine("           ТАБЛИЦЯ 3: ПОРЯДОК ОПЕРАЦІЙ (СТИСНУТИ -> ЗАШИФРУВАТИ VS НАВПАКИ)");
report.AppendLine("==================================================================================================");
report.AppendLine(string.Format("{0,-12} | {1,-7} | {2,-14} | {3,-10} | {4,-14} | {5,-10} | {6}",
    "Набір", "Сирі Б", "Стисн->Шифр Б", "Коеф. 1", "Шифр->Стисн Б", "Коеф. 2", "Виграш"));
report.AppendLine(new string('-', 95));

foreach (int count in expBatchSizes)
{
    if (count > frames.Count) continue;

    byte[] rawBytes = new byte[count * TelemetryFrame.SizeInBytes];
    for (int i = 0; i < count; i++)
        frames[i].WriteTo(rawBytes.AsSpan(i * TelemetryFrame.SizeInBytes, TelemetryFrame.SizeInBytes));

    var t1 = huffmanExp.BuildTable(rawBytes);
    var b1 = huffmanExp.Compress(rawBytes, t1);
    int compThenEnc = PacketPayload.HeaderSize + t1.Entries.Count * 2 + b1.Length;
    double ratio1 = (double)rawBytes.Length / compThenEnc;

    byte[] encryptedRaw = CipherService.Encrypt(rawBytes, expKEnc, expNonce);
    var t2 = huffmanExp.BuildTable(encryptedRaw);
    var b2 = huffmanExp.Compress(encryptedRaw, t2);
    int encThenComp = PacketPayload.HeaderSize + t2.Entries.Count * 2 + b2.Length;
    double ratio2 = (double)rawBytes.Length / encThenComp;

    double savings = (double)(encThenComp - compThenEnc) / encThenComp * 100.0;
    string name = count == frames.Count ? "Full_Session" : $"Batch_{count}";

    table3Csv.Add(string.Format(CultureInfo.InvariantCulture,
        "{0},{1},{2},{3},{4:F4},{5},{6:F4},{7:F2}%",
        name, count, rawBytes.Length, compThenEnc, ratio1, encThenComp, ratio2, savings));

    report.AppendLine(string.Format("{0,-12} | {1,-7} | {2,-14} | {3,-10:F4} | {4,-14} | {5,-10:F4} | {6:F2}%",
        name, rawBytes.Length, compThenEnc, ratio1, encThenComp, ratio2, savings));
}

File.WriteAllLines(Path.Combine(resultsDir, "table3_crypto_order.csv"), table3Csv);

// ==================================================================================================
// 6. ТАБЛИЦЯ 5: Збіжність ГА проти Випадкового пошуку
// ==================================================================================================
double EvaluateEmergencyQ(SystemParameters p)
{
    using var ch = config.CreatePythonChannel("аварійний", channelPyPath);
    var runner = new SessionRunner(
        channel: ch,
        compressor: new Huffman(),
        packer: new KnapsackPacker(),
        predictor: predictor,
        security: security,
        scheduler: new PriorityScheduler(),
        config: config,
        parameters: p
    );
    return runner.Run(frames).Q;
}

const int popSize = 10;
const int budget = 80;

Console.WriteLine($"[*] Запуск Генетичного алгоритму (pop={popSize}, budget={budget})...");
var ga = new GeneticOptimizer(populationSize: popSize, tournamentSize: 2, mutationRate: 0.35, seed: config.TeamSeed);
var bestGa = ga.Optimize(EvaluateEmergencyQ, budget);
ga.SaveHistoryCsv(Path.Combine(resultsDir, "ga_evolution.csv"));

Console.WriteLine($"[*] Запуск Випадкового пошуку (budget={budget})...");
var rs = new RandomSearchOptimizer(seed: config.TeamSeed);
var bestRs = rs.Optimize(EvaluateEmergencyQ, budget);
rs.SaveHistoryCsv(Path.Combine(resultsDir, "random_search_history.csv"));

double gaGen0Best = ga.History[0].BestFitness;
double gaLastBest = ga.History[^1].BestFitness;
double rsFinalBest = rs.History[^1].BestFitness;

report.AppendLine();
report.AppendLine("==================================================================================================");
report.AppendLine("           ТАБЛИЦЯ 5: ЗБІЖНІСТЬ ГА ПРОТИ ВИПАДКОВОГО ПОШУКУ (АВАРІЙНИЙ СЦЕНАРІЙ)");
report.AppendLine("==================================================================================================");
report.AppendLine($"Бюджет обчислень пристосованості: {budget}");
report.AppendLine($"ГА (найкращі параметри): Window={bestGa.WindowFrames}, Threshold={bestGa.CompressionThreshold:F4}, CriticalWeight={bestGa.CriticalWeight:F4} -> Q={gaLastBest:F4}");
report.AppendLine($"RS (найкращі параметри): Window={bestRs.WindowFrames}, Threshold={bestRs.CompressionThreshold:F4}, CriticalWeight={bestRs.CriticalWeight:F4} -> Q={rsFinalBest:F4}");
report.AppendLine($"Перевірка елітизму (Кейс 16): Gen0={gaGen0Best:F4} -> LastGen={gaLastBest:F4} ({(gaLastBest >= gaGen0Best ? "ПРОЙДЕНО" : "ПОРУШЕНО")})");
report.AppendLine();
report.AppendLine(string.Format("{0,-10} | {1,-8} | {2,-12} | {3,-12} | {4,-12} | {5}",
    "Покоління", "Оцінок", "ГА Best Q", "ГА Avg Q", "RS Best Q", "Найкращі гени ГА (W / Thr / CritW)"));
report.AppendLine(new string('-', 95));

for (int g = 0; g < ga.History.Count; g++)
{
    var rec = ga.History[g];
    int evalsAtGen = g == 0 ? Math.Min(budget, popSize) : Math.Min(budget, popSize + g * (popSize - 1));
    double rsBestAtStep = rs.History[evalsAtGen - 1].BestFitness;

    report.AppendLine(string.Format("{0,-10} | {1,-8} | {2,-12:F4} | {3,-12:F4} | {4,-12:F4} | {5} / {6:F3} / {7:F3}",
        rec.Generation,
        evalsAtGen,
        rec.BestFitness,
        rec.AvgFitness,
        rsBestAtStep,
        rec.BestParameters.WindowFrames,
        rec.BestParameters.CompressionThreshold,
        rec.BestParameters.CriticalWeight));
}

totalRunSw.Stop();
report.AppendLine();
report.AppendLine($"Повний час виконання програми: {totalRunSw.Elapsed.TotalSeconds:F3} с (ліміт: {config.MaxRunTimeSec} с)");

Console.WriteLine(report.ToString());

try
{
    string outputPath = Path.Combine(resultsDir, "full_pipeline_report.txt");
    File.WriteAllText(outputPath, report.ToString());
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[OK] Усі 5 таблиць і звіт збережено в каталозі: {Path.GetFullPath(resultsDir)}");
    Console.ResetColor();
}
catch (Exception ex)
{
    Console.WriteLine($"[ПОПЕРЕДЖЕННЯ] Не вдалося зберегти звіт: {ex.Message}");
}