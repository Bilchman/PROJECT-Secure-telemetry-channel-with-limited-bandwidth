using System;
using System.IO;
using System.Text;
using Core;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=======================================================");
Console.WriteLine("                  TelemetryLink CLI  ");
Console.WriteLine("=======================================================");

string csvPath = Path.Combine("data", "session.csv");
if (!File.Exists(csvPath))
{
    string altPath = Path.Combine("..", "..", "..", "data", "session.csv");
    if (File.Exists(altPath)) csvPath = altPath;
}

if (!File.Exists(csvPath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ПОМИЛКА] Файл даних {csvPath} не знайдено!");
    Console.ResetColor();
    return;
}

var frames = TelemetryReader.ReadCsv(csvPath);
Console.WriteLine($"[+] Прочитано вхідних кадрів: {frames.Count}");
Console.WriteLine("[*] Запуск симуляції на Аварійному каналі (15% втрат, B=3, бюджет 25.0 с)...");

// Фабрика для створення ідентичного стану каналу для кожного прогону
IChannel CreateEmergencyChannel() => new Channel(
    lossProbability: 0.15,
    burstLengthB: 3.0,
    delayMs: 100,
    bandwidthBps: 12000,
    transmissionBudgetS: 25.0, // обмеження часу сеансу
    seed: 42
);

// 1. Послідовний пакувальник (Sequential)
var runnerSeq = new SessionRunner(CreateEmergencyChannel(), new Huffman(), new SequentialPacker(), new MajorityClassifier(), new NoSecurity(), new PriorityScheduler());
var resSeq = runnerSeq.Run(frames);

// 2. Жадібний пакувальник (Greedy)
var runnerGreedy = new SessionRunner(CreateEmergencyChannel(), new Huffman(), new GreedyPacker(),  new MajorityClassifier(), new NoSecurity(), new PriorityScheduler());
var resGreedy = runnerGreedy.Run(frames);

// 3. Динамічне програмування (DP Knapsack)
var runnerDp = new SessionRunner(CreateEmergencyChannel(), new Huffman(), new KnapsackPacker(),  new MajorityClassifier(), new NoSecurity(), new PriorityScheduler());
var resDp = runnerDp.Run(frames);

var report = new StringBuilder();

void AppendDetailedReport(string packerName, SessionResult res)
{
    report.AppendLine("=======================================================");
    report.AppendLine($"       ЗВІТ СЕАНСУ: [{packerName.ToUpper()}] (АВАРІЙНИЙ КАНАЛ)");
    report.AppendLine("=======================================================");
    report.AppendLine($"Кількість переданих:    {res.TransmittedFrames} кадрів");
    report.AppendLine($"Кількість відновлених:  {res.RestoredFrames} кадрів");
    report.AppendLine($"Побайтовий збіг:        {(res.IsExactMatch ? "УСПІШНО (100%)" : "НЕ ЗБІГАЄТЬСЯ (через втрати в каналі)")}");
    report.AppendLine("-------------------------------------------------------");
    report.AppendLine($"B_ref (сирий обсяг):    {res.BRef} біт ({res.BRef / 8} байт)");
    report.AppendLine($"B_sent (трафік каналу): {res.BSent} біт ({res.BSent / 8} байт)");
    report.AppendLine($"Час роботи CPU (t):     {res.CpuTimeSec:F5} с");
    report.AppendLine("-------------------------------------------------------");
    report.AppendLine($"Ефективність (S):       {res.S:F4}");
    report.AppendLine($"Надійність критичних(R):{res.R:F4}");
    report.AppendLine($"Швидкодія (T):          {res.T:F4}");
    report.AppendLine("-------------------------------------------------------");
    report.AppendLine($"ПІДСУМКОВА МЕТРИКА Q:   {res.Q:F4}");
    report.AppendLine("=======================================================");
    report.AppendLine();
}

AppendDetailedReport("Sequential", resSeq);
AppendDetailedReport("Greedy", resGreedy);
AppendDetailedReport("DP-Knapsack", resDp);

// Підсумкова порівняльна таблиця (Таблиця 1 для звіту)
report.AppendLine("=======================================================================================");
report.AppendLine("                       ПОРІВНЯЛЬНА ТАБЛИЦЯ: АВАРІЙНИЙ КАНАЛ                            ");
report.AppendLine("=======================================================================================");
report.AppendLine(string.Format("{0,-15} | {1,-12} | {2,-14} | {3,-10} | {4,-10} | {5,-8}",
    "Алгоритм", "Відновлено", "Надійність (R)", "Трафік (B)", "Час CPU (с)", "Метрика Q"));
report.AppendLine("---------------------------------------------------------------------------------------");
report.AppendLine(string.Format("{0,-15} | {1,-12} | {2,-14:F4} | {3,-10} | {4,-10:F4} | {5,-8:F4}",
    "Sequential", $"{resSeq.RestoredFrames}/{resSeq.TransmittedFrames}", resSeq.R, resSeq.BSent, resSeq.CpuTimeSec, resSeq.Q));
report.AppendLine(string.Format("{0,-15} | {1,-12} | {2,-14:F4} | {3,-10} | {4,-10:F4} | {5,-8:F4}",
    "Greedy", $"{resGreedy.RestoredFrames}/{resGreedy.TransmittedFrames}", resGreedy.R, resGreedy.BSent, resGreedy.CpuTimeSec, resGreedy.Q));
report.AppendLine(string.Format("{0,-15} | {1,-12} | {2,-14:F4} | {3,-10} | {4,-10:F4} | {5,-8:F4}",
    "DP-Knapsack", $"{resDp.RestoredFrames}/{resDp.TransmittedFrames}", resDp.R, resDp.BSent, resDp.CpuTimeSec, resDp.Q));
report.AppendLine("=======================================================================================");

Console.WriteLine(report.ToString());

// Збереження результатів у каталог results/
try
{
    string resultsDir = "results";
    if (!Directory.Exists(resultsDir))
    {
        Directory.CreateDirectory(resultsDir);
    }

    string outputPath = Path.Combine(resultsDir, "emergency_comparison.txt");
    File.WriteAllText(outputPath, report.ToString());
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[OK] Результати збережено у: {Path.GetFullPath(outputPath)}");
    Console.ResetColor();
}
catch (Exception ex)
{
    Console.WriteLine($"[ПОПЕРЕДЖЕННЯ] Не вдалося зберегти файл у results/: {ex.Message}");
}




//
//
// new Channel(
//     lossProbability: 0.0,
//     burstLengthB: 1.0,
//     delayMs: 20,
//     bandwidthBps: 64000,
//     transmissionBudgetS: 1000.0,
//     seed: 42
// );
