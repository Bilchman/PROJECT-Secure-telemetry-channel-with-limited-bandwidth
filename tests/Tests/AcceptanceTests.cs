using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Core;

namespace Tests
{
    public class AcceptanceTests
    {
        [Fact]
        public void Test_Case1_Single_Frame_Passes_And_Restores_Byte_Exact()
        {
            var runner = new SessionRunner();

            // Створюємо 1 кадр
            var singleFrame = new TelemetryFrame(
                TimestampMs: 0,
                AltitudeDm: 1000,
                TempC10: 215,
                Vibration: -3,
                Mode: 1,
                Critical: 1
            );

            var frames = new List<TelemetryFrame> { singleFrame };
            var result = runner.Run(frames);

            Assert.Equal(1, result.TransmittedFrames);
            Assert.Equal(1, result.RestoredFrames);
            Assert.True(result.IsExactMatch, "Одиничний кадр повинен відновитися побайтово точно.");
            Assert.Equal(1.0, result.R);
            Assert.True(result.Q > 0, "Метрика Q повинна бути більшою за 0.");
        }
        
        [Fact]
        public void Test_Case2_Empty_Stream_Does_Not_Crash()
        {
            var runner = new SessionRunner();
            var emptyFrames = new List<TelemetryFrame>();

            // Не повинно викидати divide-by-zero або IndexOutOfRangeException
            var result = runner.Run(emptyFrames);

            Assert.Equal(0, result.TransmittedFrames);
            Assert.Equal(0, result.RestoredFrames);
            Assert.True(result.IsExactMatch);
            Assert.False(double.IsNaN(result.Q), "Метрика Q не повинна бути NaN.");
        }

        [Fact]
        public void Test_Case3_Calm_Scenario_Full_Session_All_Frames_Restored()
        {
            var runner = new SessionRunner();

            // Шукаємо файл session.csv
            string csvPath = Path.Combine("data", "session.csv");
            if (!File.Exists(csvPath))
            {
                csvPath = Path.Combine("..", "..", "..", "data", "session.csv");
            }

            List<TelemetryFrame> sessionFrames;
            if (File.Exists(csvPath))
            {
                sessionFrames = TelemetryReader.ReadCsv(csvPath);
            }
            else
            {
                // Якщо запуск у середовищі, де немає файлу session.csv,
                // синтезуємо сеанс із 100 різноманітних кадрів
                sessionFrames = new List<TelemetryFrame>();
                for (uint i = 0; i < 100; i++)
                {
                    sessionFrames.Add(new TelemetryFrame(
                        TimestampMs: i * 100,
                        AltitudeDm: (short)(500 + i),
                        TempC10: (short)(200 + (i % 20)),
                        Vibration: (short)(i % 5),
                        Mode: (byte)(i % 4),
                        Critical: (byte)(i % 10 == 0 ? 1 : 0)
                    ));
                }
            }

            Assert.NotEmpty(sessionFrames);

            var result = runner.Run(sessionFrames);

            Assert.Equal(sessionFrames.Count, result.TransmittedFrames);
            Assert.Equal(sessionFrames.Count, result.RestoredFrames);
            Assert.True(result.IsExactMatch, "У спокійному сценарії всі кадри мають відновитися зі 100% точністю.");
            Assert.Equal(1.0, result.R);
            Assert.True(result.Q > 0.0);
        }
    }
}