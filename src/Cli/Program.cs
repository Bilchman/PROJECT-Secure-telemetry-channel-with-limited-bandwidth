using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

// Завантаження team_config.json та шляхів до еталонних файлів
string configPath = TeamConfig.FindRepoFile("team_config.json", Path.Combine("data", "team_config.json"));
string trainPath = TeamConfig.FindRepoFile(Path.Combine("data", "train.csv"), "train.csv");
string validPath = TeamConfig.FindRepoFile(Path.Combine("data", "valid.csv"), "valid.csv");
string sessionPath = TeamConfig.FindRepoFile(Path.Combine("data", "session.csv"), "session.csv");
string channelPyPath = TeamConfig.FindRepoFile("channel.py", Path.Combine("data", "channel.py"));

var config = TeamConfig.Load(configPath);
Console.WriteLine($"[+] Команда: {config.TeamId} | seed={config.TeamSeed} | t_ref={config.RefTimeSec}с | F1_min={config.F1Min:F2} | channel_seed={config.ChannelSeed}");

// Читання розмічених даних і навчання перцептрона за протоколом (80/20, 3 ініціалізації)
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

string resultsDir = "results";
Directory.CreateDirectory(resultsDir);
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

string[] scenarioOrder = ["спокійний", "робочий", "аварійний"];

report.AppendLine("==================================================================================================");
report.AppendLine("              ПОРІВНЯННЯ ПАКУВАЛЬНИКІВ НА ЕТАЛОННОМУ КАНАЛІ (channel.py)");
report.AppendLine("==================================================================================================");

foreach (var scenarioName in scenarioOrder)
{
    var sc = config.Scenarios[scenarioName];
    double maxCritLoss = config.CriticalLossThresholds[scenarioName];

    report.AppendLine($"\n--- Сценарій: {scenarioName.ToUpper()} (loss={sc.LossProbability:P0}, B={sc.BurstLengthB}, delay={sc.DelayMinMs}..{sc.DelayMaxMs} мс, bw={sc.BandwidthBps} bps, budget={sc.TransmissionBudgetS} с, поріг втрати кр. <= {maxCritLoss:P0}) ---");
    report.AppendLine(string.Format("{0,-13} | {1,-11} | {2,-8} | {3,-10} | {4,-9} | {5,-8} | {6,-8} | {7,-8} | {8}",
        "Пакувальник", "Відновлено", "R", "Втрата кр.", "B_sent", "S", "T", "Q", "Статус / Поза бюдж."));
    report.AppendLine(new string('-', 108));

    IPacker[] packers = [new SequentialPacker(), new GreedyPacker(), new KnapsackPacker()];
    foreach (var packer in packers)
    {
        using var channel = config.CreatePythonChannel(scenarioName, channelPyPath);
        var runner = new SessionRunner(
            channel: channel,
            compressor: new Huffman(),
            packer: packer,
            predictor: predictor,
            security: new NoSecurity(),
            scheduler: new PriorityScheduler(),
            config: config
        );

        var res = runner.Run(frames);
        var stats = channel.GetStats();
        int overBudget = Convert.ToInt32(stats["не_вміщено_в_бюджет"]);

        double critLoss = 1.0 - res.R;
        string status = critLoss <= maxCritLoss + 1e-9 ? "OK" : "ПЕРЕВИЩЕНО";

        report.AppendLine(string.Format("{0,-13} | {1,-11} | {2,-8:F4} | {3,-10:P2} | {4,-9} | {5,-8:F4} | {6,-8:F4} | {7,-8:F4} | {8} (over={9})",
            packer.Name, $"{res.RestoredFrames}/{res.TransmittedFrames}", res.R, critLoss, res.BSent, res.S, res.T, res.Q, status, overBudget));
    }
}

// Генетичний алгоритм проти Випадкового пошуку
double EvaluateEmergencyQ(SystemParameters p)
{
    using var ch = config.CreatePythonChannel("аварійний", channelPyPath);
    var runner = new SessionRunner(
        channel: ch,
        compressor: new Huffman(),
        packer: new KnapsackPacker(),
        predictor: predictor,
        security: new NoSecurity(),
        scheduler: new PriorityScheduler(),
        config: config,
        parameters: p
    );
    return runner.Run(frames).Q;
}

const int popSize = 10;
const int budget = 80;

Console.WriteLine($"[*] Запуск Генетичного алгоритму (pop={popSize}, budget={budget})...");
var ga = new GeneticOptimizer(populationSize: popSize, seed: config.TeamSeed);
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
report.AppendLine("           ОПТИМІЗАЦІЯ: ГЕНЕТИЧНИЙ АЛГОРИТМ ПРОТИ ВИПАДКОВОГО ПОШУКУ (АВАРІЙНИЙ СЦЕНАРІЙ)");
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
    Console.WriteLine($"[OK] Звіт збережено у: {Path.GetFullPath(outputPath)}");
    Console.ResetColor();
}
catch (Exception ex)
{
    Console.WriteLine($"[ПОПЕРЕДЖЕННЯ] Не вдалося зберегти файл у results/: {ex.Message}");
}