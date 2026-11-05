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

var runner = new SessionRunner();
var res = runner.Run(frames);

var report = new StringBuilder();
report.AppendLine("=======================================================");
report.AppendLine("               ЗВІТ ПРОБІГУ СЕАНСУ ТЕЛЕМЕТРІЇ          ");
report.AppendLine("=======================================================");
report.AppendLine($"Сценарій каналу:        Спокійний (0% втрат, 20 мс, 64 кбіт/с)");
report.AppendLine($"Кількість переданих:    {res.TransmittedFrames} кадрів");
report.AppendLine($"Кількість відновлених:  {res.RestoredFrames} кадрів");
report.AppendLine($"Побайтовий збіг:        {(res.IsExactMatch ? "УСПІШНО (100% точність)" : "НЕ ЗБІГАЄТЬСЯ")}");
report.AppendLine("-------------------------------------------------------");
report.AppendLine($"B_ref (сирий обсяг):    {res.BRef} біт ({res.BRef / 8} байт)");
report.AppendLine($"B_sent (трафік каналу): {res.BSent} біт ({res.BSent / 8} байт)");
report.AppendLine($"Час роботи CPU (t):     {res.CpuTimeSec:F5} с");
report.AppendLine("-------------------------------------------------------");
report.AppendLine($"Ефективність (S):       {res.S:F4}");
report.AppendLine($"Надійність (R):         {res.R:F4}");
report.AppendLine($"Швидкодія (T):          {res.T:F4}");
report.AppendLine("-------------------------------------------------------");
report.AppendLine($"ПІДСУМКОВА МЕТРИКА Q:   {res.Q:F4}");
report.AppendLine("=======================================================");

Console.WriteLine(report.ToString());

// Збереження результатів у каталог results/
try
{
    string resultsDir = "results";
    if (!Directory.Exists(resultsDir))
    {
        Directory.CreateDirectory(resultsDir);
    }

    string outputPath = Path.Combine(resultsDir, "baseline_run_1.txt");
    File.WriteAllText(outputPath, report.ToString());
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[OK] Результати збережено у: {Path.GetFullPath(outputPath)}");
    Console.ResetColor();
}
catch (Exception ex)
{
    Console.WriteLine($"[ПОПЕРЕДЖЕННЯ] Не вдалося зберегти файл у results/: {ex.Message}");
}