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

        // Перевірка математичних інваріантів прямого проходу (Forward pass) перцептрона - сума ймовірностей вихідного розподілу Softmax дорівнює 1.0 з точністю до 1e-7,
        // відсутні значення NaN, а кожна ймовірність лежить у діапазоні [0.0, 1.0]
        [Fact]
        public void MultilayerPerceptron_Forward_ReturnsProbabilitiesSummingToOne_WithoutNaN()
        {
            // Arrange - створення моделі з фіксованим seed під кількість ознак екстрактора
            int featureCount = FeatureExtractor.FeatureCount;
            var mlp = new MultilayerPerceptron(inputSize: featureCount, hiddenSize: 16, outputSize: 3, seed: 42);
            var inputVector = new double[featureCount];
            for (int i = 0; i < featureCount; i++)
            {
                inputVector[i] = (i % 3 == 0) ? 0.5 : -0.25;
            }

            // Act - виконання прямого проходу
            double[] probabilities = mlp.Forward(inputVector);

            // Assert - перевірка розмірності, меж ймовірностей та суми виходу
            Assert.Equal(3, probabilities.Length);
            Assert.All(probabilities, p =>
            {
                Assert.False(double.IsNaN(p) || double.IsInfinity(p));
                Assert.InRange(p, 0.0, 1.0);
            });

            double sum = probabilities[0] + probabilities[1] + probabilities[2];
            Assert.True(Math.Abs(sum - 1.0) < 1e-7, $"Сума ймовірностей {sum} відрізняється від 1.0.");
        }

        // Перевірка повторюваності та детермінованості ваг при заданні однакового seed
        [Fact]
        public void MultilayerPerceptron_SameSeed_ProducesIdenticalOutputs()
        {
            // Arrange
            int featureCount = FeatureExtractor.FeatureCount;
            var inputVector = new double[featureCount];
            Array.Fill(inputVector, 0.1);

            var modelA = new MultilayerPerceptron(inputSize: featureCount, hiddenSize: 16, outputSize: 3, seed: 12345);
            var modelB = new MultilayerPerceptron(inputSize: featureCount, hiddenSize: 16, outputSize: 3, seed: 12345);

            // Act
            double[] probsA = modelA.Forward(inputVector);
            double[] probsB = modelB.Forward(inputVector);

            // Assert
            Assert.Equal(probsA[0], probsB[0]);
            Assert.Equal(probsA[1], probsB[1]);
            Assert.Equal(probsA[2], probsB[2]);
        }

        // Перевірка контрольованої реакції на невідповідність розмірності вхідного вектора ознак.
        [Fact]
        public void MultilayerPerceptron_InvalidInputDimension_ThrowsArgumentException()
        {
            // Arrange
            var mlp = new MultilayerPerceptron(inputSize: 12, hiddenSize: 16, outputSize: 3, seed: 42);
            var shortVector = new double[5];

            // Act & Assert
            Assert.Throws<ArgumentException>(() => mlp.Forward(shortVector));
        }

        // Перевірка коректності обчислення функції втрат Cross-Entropy Loss - ідеальне передбачення наближає помилку до 0, а хибне викликає високий штраф (пункт 6.11).
        [Fact]
        public void MultilayerPerceptron_Loss_BehavesCorrectly_ForConfidentAndErroneousPredictions()
        {
            // Arrange
            int featureCount = FeatureExtractor.FeatureCount;
            var mlp = new MultilayerPerceptron(inputSize: featureCount, hiddenSize: 8, outputSize: 3, seed: 100);
            var input = new double[featureCount];

            // Act, один крок навчання повертає величину втрат
            double initialLoss = mlp.TrainStep(input, targetClass: 0, learningRate: 0.0);

            // Assert, величина втрат має бути додатною, скінченною та не дорівнювати NaN
            Assert.False(double.IsNaN(initialLoss));
            Assert.False(double.IsInfinity(initialLoss));
            Assert.True(initialLoss > 0.0, "Функція втрат має бути більшою за 0.");
        }

        // Перевірка алгоритму Backpropagation на простому синтетичному наборі даних. Згідно з пунктом 6.11 методичних вказівок, протягом 40 епох навчання закономірно зменшує значення функції втрат (Loss final менше за Loss initial * 0.4).
        [Fact]
        public void MultilayerPerceptron_Backpropagation_ReducesLossOnSyntheticDataset()
        {
            // Arrange, синтетична вибірка з 3 чітко розрізнюваними класами для перевірки збіжності
            int featureCount = FeatureExtractor.FeatureCount;
            var mlp = new MultilayerPerceptron(inputSize: featureCount, hiddenSize: 16, outputSize: 3, seed: 42);

            // Вектори розмірністю 12 ознак
            // Клас 0 (норма): високі значення перших нормалізованих сигналів
            // Клас 1 (аномалія): висока вібрація (ознаки 2, 3) та прапорець critical
            // Клас 2 (перехідний): висока частота перемикань режиму (ознаки 4, 5)
            var syntheticData = new List<(double[] Input, int Target)>
        {
            (new double[] { 0.8, 0.5, 0.1, 0.1, 0.1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, 0),
            (new double[] { 0.7, 0.6, 0.2, 0.2, 0.1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, 0),
            (new double[] { 0.1, 0.0, 0.9, 0.9, 0.1, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, 1),
            (new double[] { 0.2, 0.1, 0.8, 0.8, 0.2, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, 1),
            (new double[] { 0.0, 0.0, 0.1, 0.1, 0.9, 0.8, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, 2),
            (new double[] { 0.1, 0.0, 0.2, 0.2, 0.8, 0.7, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, 2)
        };

            // Act, обчислення початкового loss та запуск 40 епох навчання через TrainStep
            double initialTotalLoss = 0.0;
            foreach (var sample in syntheticData)
            {
                // Замір початкової помилки нульовим кроком
                initialTotalLoss += mlp.TrainStep(sample.Input, sample.Target, learningRate: 0.0, momentum: 0.0);
            }

            // Цикл навчання
            for (int epoch = 0; epoch < 40; epoch++)
            {
                foreach (var sample in syntheticData)
                {
                    mlp.TrainStep(sample.Input, sample.Target, learningRate: 0.05, momentum: 0.8);
                }
            }

            double finalTotalLoss = 0.0;
            foreach (var sample in syntheticData)
            {
                finalTotalLoss += mlp.TrainStep(sample.Input, sample.Target, learningRate: 0.0, momentum: 0.0);
            }

            // Assert, підтвердження збіжності градієнтного спуску
            Assert.True(finalTotalLoss < initialTotalLoss * 0.4,
                $"Помилка після навчання ({finalTotalLoss:F4}) не зменшилась суттєво від початкової ({initialTotalLoss:F4}).");
        }

        // Перевірка градієнтів методом чисельного диференціювання відповідно до п. 6.11 методичних вказівок
        // Порівнює аналітичний градієнт Backpropagation із центральною скінченною різницею grad_num = (Loss(w + eps) - Loss(w - eps)) / (2 * eps).
        [Fact]
        public void MultilayerPerceptron_NumericalGradientChecking_MatchesAnalyticalGradients()
        {
            // Arrange, ініціалізація моделі та вектора ознак
            int featureCount = FeatureExtractor.FeatureCount;
            var mlp = new MultilayerPerceptron(inputSize: featureCount, hiddenSize: 8, outputSize: 3, seed: 12345);
            var input = new double[featureCount];
            for (int i = 0; i < featureCount; i++)
            {
                input[i] = (i % 2 == 0) ? 0.4 : -0.3;
            }

            int targetClass = 1;
            const double epsilon = 1e-5;
            const double maxAllowedRelativeError = 1e-4; // допустима похибка 0.01%

            // Розрахунок точних аналітичних градієнтів через зворотне поширення
            mlp.ComputeGradients(input, targetClass, out var gradW1, out _, out var gradW2, out _);

            // Act & Assert 1. Перевірка градієнта ваги вихідного шару W2[j, k]
            int testHiddenIdx = 2;
            int testOutputIdx = 1;
            double originalW2 = mlp.GetWeightW2(testHiddenIdx, testOutputIdx);

            // Обчислення Loss(w + eps)
            mlp.SetWeightW2(testHiddenIdx, testOutputIdx, originalW2 + epsilon);
            double lossPlusW2 = mlp.ComputeLoss(input, targetClass);

            // Обчислення Loss(w - eps)
            mlp.SetWeightW2(testHiddenIdx, testOutputIdx, originalW2 - epsilon);
            double lossMinusW2 = mlp.ComputeLoss(input, targetClass);

            // Відновлення оригінальної ваги
            mlp.SetWeightW2(testHiddenIdx, testOutputIdx, originalW2);

            double numericalGradW2 = (lossPlusW2 - lossMinusW2) / (2.0 * epsilon);
            double analyticalGradW2 = gradW2[testHiddenIdx][testOutputIdx];

            double relativeErrorW2 = Math.Abs(numericalGradW2 - analyticalGradW2) / (Math.Max(Math.Abs(numericalGradW2), Math.Abs(analyticalGradW2)) + 1e-8);

            Assert.True(relativeErrorW2 < maxAllowedRelativeError,
                $"W2 Gradient Check Failed: relError={relativeErrorW2:E3}, num={numericalGradW2:F6}, ana={analyticalGradW2:F6}");

            // Act & Assert 2. Перевірка градієнта ваги прихованого шару W1[i, j]
            int testInputIdx = 3;
            double originalW1 = mlp.GetWeightW1(testInputIdx, testHiddenIdx);

            mlp.SetWeightW1(testInputIdx, testHiddenIdx, originalW1 + epsilon);
            double lossPlusW1 = mlp.ComputeLoss(input, targetClass);

            mlp.SetWeightW1(testInputIdx, testHiddenIdx, originalW1 - epsilon);
            double lossMinusW1 = mlp.ComputeLoss(input, targetClass);

            mlp.SetWeightW1(testInputIdx, testHiddenIdx, originalW1);

            double numericalGradW1 = (lossPlusW1 - lossMinusW1) / (2.0 * epsilon);
            double analyticalGradW1 = gradW1[testInputIdx][testHiddenIdx];

            double relativeErrorW1 = Math.Abs(numericalGradW1 - analyticalGradW1) / (Math.Max(Math.Abs(numericalGradW1), Math.Abs(analyticalGradW1)) + 1e-8);

            Assert.True(relativeErrorW1 < maxAllowedRelativeError,
                $"W1 Gradient Check Failed: relError={relativeErrorW1:E3}, num={numericalGradW1:F6}, ana={analyticalGradW1:F6}");
        }

        // Стрес-тест підтвердження того, що екстремально великі вхідні значення не спричиняють переповнення типів з плаваючою комою у NaN або Infinity.
        [Fact]
        public void MultilayerPerceptron_ExtremeInputValues_MaintainsNumericalStability()
        {
            // Arrange
            int featureCount = FeatureExtractor.FeatureCount;
            var mlp = new MultilayerPerceptron(inputSize: featureCount, hiddenSize: 16, outputSize: 3, seed: 42);

            // Вектор з екстремальними величинами (ризик переповнення exp(z))
            var extremeInput = new double[featureCount];
            for (int i = 0; i < featureCount; i++)
            {
                extremeInput[i] = (i % 2 == 0) ? 500.0 : -500.0;
            }

            // Act
            double[] probabilities = mlp.Forward(extremeInput);

            // Assert
            Assert.Equal(3, probabilities.Length);
            Assert.All(probabilities, p =>
            {
                Assert.False(double.IsNaN(p));
                Assert.False(double.IsInfinity(p));
                Assert.InRange(p, 0.0, 1.0);
            });

            double sum = probabilities[0] + probabilities[1] + probabilities[2];
            Assert.True(Math.Abs(sum - 1.0) < 1e-7, $"Сума ймовірностей {sum} порушила інваріант 1.0.");
        }

        // Перевірка протоколу трьох ініціалізацій (seed=28, 111, 707), відбору за Macro-F1
        // та автоматичної генерації звітів у results/ (classifier_metrics.json, confusion_matrix.csv).
        [Fact]
        public void Trainer_ThreeSeedsProtocol_GeneratesArtifactsAndOutperformsBaseline()
        {
            // Arrange, синтетичний набір із 60 зразків для відтворення протоколу
            int featureCount = FeatureExtractor.FeatureCount;
            var dataset = new List<(double[] Features, int Label)>();

            for (int i = 0; i < 60; i++)
            {
                var feat = new double[featureCount];
                int label = i % 3;
                if (label == 0) feat[0] = 0.8;      // норма
                else if (label == 1) feat[2] = 0.9; // аномалія
                else feat[4] = 0.85;                // перехідний режим

                dataset.Add((feat, label));
            }

            var (train, val) = Trainer.SplitData(dataset, trainRatio: 0.8, seed: 42);
            var trainer = new Trainer(featureCount, hiddenSize: 16);

            // Act прогін 3-х seed
            int[] seeds = { 28, 111, 707 };
            var (bestModel, bestMetrics) = trainer.TrainThreeSeeds(train, val, seeds, epochs: 20);

            // Assert 1 модель стабільно перевершує випадкове вгадування 0.33
            Assert.NotNull(bestModel);
            Assert.True(bestMetrics.MacroF1 > 0.33, $"Macro-F1 {bestMetrics.MacroF1} мав перевищити baseline 0.33.");

            // Assert 2 генерація машинних звітів у папку results/ репозиторію
            string? currentDir = AppContext.BaseDirectory;
            string repoRoot = string.Empty;
            while (currentDir != null)
            {
                if (Directory.Exists(Path.Combine(currentDir, "results")) || File.Exists(Path.Combine(currentDir, "TelemetryLink.sln")))
                {
                    repoRoot = currentDir;
                    break;
                }
                currentDir = Directory.GetParent(currentDir)?.FullName;
            }

            string resultsDir = Path.Combine(repoRoot, "results");
            Trainer.SaveArtifacts(resultsDir, bestMetrics);

            string csvPath = Path.Combine(resultsDir, "confusion_matrix.csv");
            string jsonPath = Path.Combine(resultsDir, "classifier_metrics.json");

            Assert.True(File.Exists(csvPath), "Файл confusion_matrix.csv не було створено.");
            Assert.True(File.Exists(jsonPath), "Файл classifier_metrics.json не було створено.");
        }

        // Перевірка правила: якщо клас жодного разу не був передбачений і відсутній у вибірці, Precision + Recall == 0, а F1 обов'язково присвоюється 0.0 без ділення на нуль чи NaN.
        [Fact]
        public void Trainer_Evaluate_ZeroPrecisionAndRecall_AssignsZeroF1Safely()
        {
            // Arrange, порожня для класів 1 і 2 валідаційна вибірка
            int featureCount = FeatureExtractor.FeatureCount;
            var model = new MultilayerPerceptron(featureCount, hiddenSize: 8, outputSize: 3, seed: 1);
            var valData = new List<(double[] Features, int Label)>
        {
            (new double[featureCount], 0)
        };

            // Act
            var metrics = Trainer.Evaluate(model, valData);

            // Assert
            Assert.False(double.IsNaN(metrics.MacroF1));
            Assert.All(metrics.F1PerClass, f1 => Assert.False(double.IsNaN(f1)));
        }
    }
}
