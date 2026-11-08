using Core.Prediction;
using System;
using System.Collections.Generic;
using System.Text;
using Core;

namespace Tests
{
    public sealed class PredictionTests
    {
        [Fact]
        public void ReadFrom_ReferenceSingleFrame_MatchesFormatSpecification()
        {
            // Контрольний приклад з Розділу 7 FORMAT_SPEC_1.md:
            // timestamp_ms = 0     -> 00 00 00 00
            // altitude_dm  = 1000  -> 03 E8
            // temp_c10     = 215   -> 00 D7
            // vibration    = -3    -> FF FD
            // mode         = 1     -> 01
            // critical     = 1     -> 01
            byte[] raw = [0x00, 0x00, 0x00, 0x00, 0x03, 0xE8, 0x00, 0xD7, 0xFF, 0xFD, 0x01, 0x01];

            var frame = TelemetryFrame.ReadFrom(raw);

            Assert.Equal(0u, frame.TimestampMs);
            Assert.Equal((short)1000, frame.AltitudeDm);
            Assert.Equal((short)215, frame.TempC10);
            Assert.Equal((short)-3, frame.Vibration);
            Assert.Equal((byte)1, frame.Mode);
            Assert.Equal((byte)1, frame.Critical);
            Assert.True(frame.IsCritical);
            Assert.True(frame.IsValid);
        }

        [Fact]
        public void WriteToAndReadFrom_NegativeValuesRoundTrip_PreservesValues()
        {
            // Перевірка інваріанта 9.6: frame == restored_frame для знакових від'ємних чисел
            var original = new TelemetryFrame(
                TimestampMs: 42000u,
                AltitudeDm: -500,
                TempC10: -250,
                Vibration: -3,
                Mode: 5,
                Critical: 0
            );

            Span<byte> buffer = stackalloc byte[TelemetryFrame.SizeInBytes];
            original.WriteTo(buffer);

            var restored = TelemetryFrame.ReadFrom(buffer);

            Assert.Equal(original, restored);
        }

        [Theory]
        [InlineData(8)]
        [InlineData(255)]
        public void ReadFrom_ModeOutOfRange_ThrowsArgumentException(byte invalidMode)
        {
            byte[] raw = [0, 0, 0, 1, 0, 10, 0, 20, 0, 0, invalidMode, 0];
            Assert.Throws<ArgumentException>(() => TelemetryFrame.ReadFrom(raw));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(255)]
        public void ReadFrom_CriticalOutOfRange_ThrowsArgumentException(byte invalidCritical)
        {
            byte[] raw = [0, 0, 0, 1, 0, 10, 0, 20, 0, 0, 0, invalidCritical];
            Assert.Throws<ArgumentException>(() => TelemetryFrame.ReadFrom(raw));
        }

        [Fact]
        public void ReadFrom_TruncatedBuffer_ThrowsArgumentExceptionWithoutIndexOutOfRangeException()
        {
            // Кадр коротший за 12 байтів має контрольовано відхилятися
            byte[] truncated = [0x00, 0x00, 0x00, 0x01, 0x03, 0xE8];
            Assert.Throws<ArgumentException>(() => TelemetryFrame.ReadFrom(truncated));
        }

        [Fact]
        public void ReadBinary_EmptyStream_ReturnsEmptyListWithoutException()
        {
            // Приймальний кейс C1 (№ 2): порожній потік не викликає аварійного завершення
            using var emptyStream = new MemoryStream([]);
            var frames = TelemetryReader.ReadBinary(emptyStream);

            Assert.NotNull(frames);
            Assert.Empty(frames);
        }

        [Fact]
        public void MajorityClassifier_ValidFramesWithAnyCritical_AlwaysReturnsNormalClass()
        {
            // Інваріант 9.22: critical не є міткою класу, повертається клас 0 (норма)
            var classifier = new MajorityClassifier();
            var frames = new List<TelemetryFrame>
        {
            new(TimestampMs: 10u, AltitudeDm: 100, TempC10: 200, Vibration: 0, Mode: 0, Critical: 0),
            new(TimestampMs: 20u, AltitudeDm: 100, TempC10: 200, Vibration: 5, Mode: 1, Critical: 1)
        };

            var classes = classifier.Classify(frames);

            Assert.Equal(2, classes.Length);
            Assert.Equal(FrameClass.Normal, classes[0]);
            Assert.Equal(FrameClass.Normal, classes[1]);
        }

        //Перевірка коректності вилучення ознак на реальному сеансі телеметрії
        [Fact]
        public void FeatureExtractor_ProcessSessionCsv_ProducesValidFeaturesWithoutNaN()
        {            
            string? currentDir = AppContext.BaseDirectory;
            string sessionPath = string.Empty;

            while (currentDir != null)
            {
                string candidate = Path.Combine(currentDir, "data", "session.csv");
                if (File.Exists(candidate))
                {
                    sessionPath = candidate;
                    break;
                }
                currentDir = Directory.GetParent(currentDir)?.FullName;
            }

            Assert.True(File.Exists(sessionPath), $"Файл session.csv не знайдено за шляхом: {sessionPath}");

            // Зчитування кадрів сеансу телеметрії (обхід заголовка CSV-таблиці)
            var frames = new List<TelemetryFrame>();
            using (var reader = new StreamReader(sessionPath))
            {
                reader.ReadLine(); // Пропуск заголовока
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    frames.Add(new TelemetryFrame(
                        uint.Parse(parts[0]),
                        short.Parse(parts[1]),
                        short.Parse(parts[2]),
                        short.Parse(parts[3]),
                        byte.Parse(parts[4]),
                        byte.Parse(parts[5])));
                }
            }

            Assert.NotEmpty(frames);

            // Ініціалізація генератора з рекомендованим розміром вікна k = 8
            var extractor = new FeatureExtractor(windowSize: 8);
            var history = new List<TelemetryFrame>();

            foreach (var frame in frames)
            {
                double[] features = extractor.ExtractFeatures(history, frame);

                Assert.Equal(FeatureExtractor.FeatureCount, features.Length);
                Assert.All(features, val => Assert.False(double.IsNaN(val) || double.IsInfinity(val)));

                history.Add(frame);
                if (history.Count > 8)
                {
                    history.RemoveAt(0);
                }
            }
        }
    }
}
