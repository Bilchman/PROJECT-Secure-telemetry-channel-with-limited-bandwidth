using Core;

Console.WriteLine("=== TelemetryLink CLI (Зріз 0) ===");

string csvPath = Path.Combine("data", "session.csv");

if (File.Exists(csvPath))
{
    var frames = TelemetryReader.ReadCsv(csvPath);
    Console.WriteLine($"Успішно прочитано {frames.Count} кадрів із {csvPath}");

    var runner = new SessionRunner();
    double Q = runner.Run(frames);

    Console.WriteLine("Успішний наскрізний прогін через заглушки!");
    Console.WriteLine($"Базова метрика Q = {Q:F4}");
}
else
{
    Console.WriteLine($"Файл {csvPath} не знайдено. Переконайтеся, що він у папці data/");
}