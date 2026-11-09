namespace Core;

using System;


// Багатошаровий перцептрон для класифікації кадру телеметрії на три класи.
public sealed class MultilayerPerceptron
{
    private const double LeakySlope = 0.01;

    private readonly int _inputSize;
    private readonly int _hiddenSize;
    private readonly int _outputSize;

    private readonly double[][] _w1; // [input][hidden]
    private readonly double[] _b1;   // [hidden]
    private readonly double[][] _w2; // [hidden][output]
    private readonly double[] _b2;   // [output]

    // Буфери накопичення імпульсу для Momentum SGD
    private readonly double[][] _vW1;
    private readonly double[] _vB1;
    private readonly double[][] _vW2;
    private readonly double[] _vB2;

    public MultilayerPerceptron(int inputSize, int hiddenSize = 16, int outputSize = 3, int? seed = null)
    {
        if (inputSize <= 0) throw new ArgumentOutOfRangeException(nameof(inputSize));
        if (hiddenSize <= 0) throw new ArgumentOutOfRangeException(nameof(hiddenSize));
        if (outputSize <= 0) throw new ArgumentOutOfRangeException(nameof(outputSize));

        _inputSize = inputSize;
        _hiddenSize = hiddenSize;
        _outputSize = outputSize;

        _w1 = AllocateMatrix(inputSize, hiddenSize);
        _b1 = new double[hiddenSize];
        _w2 = AllocateMatrix(hiddenSize, outputSize);
        _b2 = new double[outputSize];

        // Виділення пам'яті під буфери імпульсу
        _vW1 = AllocateMatrix(inputSize, hiddenSize);
        _vB1 = new double[hiddenSize];
        _vW2 = AllocateMatrix(hiddenSize, outputSize);
        _vB2 = new double[outputSize];

        InitializeWeights(seed);
    }

    public int InputSize => _inputSize;
    public int HiddenSize => _hiddenSize;
    public int OutputSize => _outputSize;

    // Ініціалізація Ксав'єра (Glorot Uniform) для запобігання затуханню градієнтів.
    private void InitializeWeights(int? seed)
    {
        var random = seed.HasValue ? new Random(seed.Value) : new Random();

        double limit1 = Math.Sqrt(6.0 / (_inputSize + _hiddenSize));
        for (int i = 0; i < _inputSize; i++)
        {
            for (int j = 0; j < _hiddenSize; j++)
            {
                _w1[i][j] = (random.NextDouble() * 2.0 - 1.0) * limit1;
            }
        }

        double limit2 = Math.Sqrt(6.0 / (_hiddenSize + _outputSize));
        for (int j = 0; j < _hiddenSize; j++)
        {
            for (int k = 0; k < _outputSize; k++)
            {
                _w2[j][k] = (random.NextDouble() * 2.0 - 1.0) * limit2;
            }
        }
    }

    // Прямий прохід для інференсу повертає розподіл трьох ймовірностей Softmax.
    public double[] Forward(double[] input)
    {
        ForwardInternal(input, out _, out _, out _, out double[] probabilities);
        return probabilities;
    }

   
    /// Внутрішній прямий прохід зі збереженням проміжних активацій для Backpropagation.
    private void ForwardInternal(
        double[] input,
        out double[] hiddenNet,
        out double[] hiddenAct,
        out double[] outputNet,
        out double[] probabilities)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length != _inputSize)
        {
            throw new ArgumentException($"Очікувалось {_inputSize} ознак, отримано {input.Length}.");
        }

        // Прихований шар із лінійним зважуванням та LeakyReLU
        hiddenNet = new double[_hiddenSize];
        hiddenAct = new double[_hiddenSize];
        for (int j = 0; j < _hiddenSize; j++)
        {
            double sum = _b1[j];
            for (int i = 0; i < _inputSize; i++)
            {
                sum += input[i] * _w1[i][j];
            }
            hiddenNet[j] = sum;
            hiddenAct[j] = sum >= 0.0 ? sum : LeakySlope * sum;
        }

        // Вихідний шар, обчислення лінійних сум та пошук maxNet
        outputNet = new double[_outputSize];
        double maxNet = double.NegativeInfinity;
        for (int k = 0; k < _outputSize; k++)
        {
            double sum = _b2[k];
            for (int j = 0; j < _hiddenSize; j++)
            {
                sum += hiddenAct[j] * _w2[j][k];
            }
            outputNet[k] = sum;
            if (sum > maxNet)
            {
                maxNet = sum;
            }
        }

        // Захищений від переповнення Softmax: exp(z_k - maxNet)
        probabilities = new double[_outputSize];
        double sumExp = 0.0;
        for (int k = 0; k < _outputSize; k++)
        {
            double exp = Math.Exp(outputNet[k] - maxNet);
            probabilities[k] = exp;
            sumExp += exp;
        }

        for (int k = 0; k < _outputSize; k++)
        {
            probabilities[k] /= sumExp;
        }
    }

    // Один крок навчання на окремому прикладі методом Backpropagation (пункт 4.11).
    // Обчислює Cross-Entropy Loss, аналітичні градієнти та оновлює ваги через Momentum SGD.
    public double TrainStep(double[] input, int targetClass, double learningRate = 0.05, double momentum = 0.9)
    {
        if (targetClass < 0 || targetClass >= _outputSize)
        {
            throw new ArgumentOutOfRangeException(nameof(targetClass), $"Цільовий клас має бути в межах 0..{_outputSize - 1}");
        }

        ComputeGradients(input, targetClass, out var gradW1, out var gradB1, out var gradW2, out var gradB2);
        double loss = ComputeLoss(input, targetClass);

        // Оновлення ваг W2 та зсувів B2 вихідного шару
        for (int j = 0; j < _hiddenSize; j++)
        {
            for (int k = 0; k < _outputSize; k++)
            {
                _vW2[j][k] = momentum * _vW2[j][k] - learningRate * gradW2[j][k];
                _w2[j][k] += _vW2[j][k];
            }
        }

        for (int k = 0; k < _outputSize; k++)
        {
            _vB2[k] = momentum * _vB2[k] - learningRate * gradB2[k];
            _b2[k] += _vB2[k];
        }

        // Оновлення ваг W1 та зсувів B1 прихованого шару
        for (int i = 0; i < _inputSize; i++)
        {
            for (int j = 0; j < _hiddenSize; j++)
            {
                _vW1[i][j] = momentum * _vW1[i][j] - learningRate * gradW1[i][j];
                _w1[i][j] += _vW1[i][j];
            }
        }

        for (int j = 0; j < _hiddenSize; j++)
        {
            _vB1[j] = momentum * _vB1[j] - learningRate * gradB1[j];
            _b1[j] += _vB1[j];
        }

        return loss;
    }


    private static double[][] AllocateMatrix(int rows, int cols)
    {
        var matrix = new double[rows][];
        for (int i = 0; i < rows; i++)
        {
            matrix[i] = new double[cols];
        }
        return matrix;
    }

    // Обчислення значення функції втрат Cross-Entropy без зміни ваг моделі.
    public double ComputeLoss(double[] input, int targetClass)
    {
        double[] probs = Forward(input);
        return -Math.Log(Math.Max(probs[targetClass], 1e-15));
    }

    // Аналітичний розрахунок градієнтів функції втрат методом Backpropagation (пункт 4.11).
    public void ComputeGradients(
        double[] input,
        int targetClass,
        out double[][] gradW1,
        out double[] gradB1,
        out double[][] gradW2,
        out double[] gradB2)
    {
        ForwardInternal(input, out double[] hiddenNet, out double[] hiddenAct, out _, out double[] probs);

        gradW2 = AllocateMatrix(_hiddenSize, _outputSize);
        gradB2 = new double[_outputSize];
        gradW1 = AllocateMatrix(_inputSize, _hiddenSize);
        gradB1 = new double[_hiddenSize];

        // Градієнт вихідного шару dL/dz_out = (probs[k] - target)
        var deltaOutput = new double[_outputSize];
        for (int k = 0; k < _outputSize; k++)
        {
            deltaOutput[k] = probs[k] - (k == targetClass ? 1.0 : 0.0);
            gradB2[k] = deltaOutput[k];
        }

        for (int j = 0; j < _hiddenSize; j++)
        {
            for (int k = 0; k < _outputSize; k++)
            {
                gradW2[j][k] = deltaOutput[k] * hiddenAct[j];
            }
        }

        // Градієнт прихованого шару з урахуванням похідної LeakyReLU
        var deltaHidden = new double[_hiddenSize];
        for (int j = 0; j < _hiddenSize; j++)
        {
            double sumErr = 0.0;
            for (int k = 0; k < _outputSize; k++)
            {
                sumErr += deltaOutput[k] * _w2[j][k];
            }
            double dActivation = hiddenNet[j] >= 0.0 ? 1.0 : LeakySlope;
            deltaHidden[j] = sumErr * dActivation;
            gradB1[j] = deltaHidden[j];
        }

        for (int i = 0; i < _inputSize; i++)
        {
            for (int j = 0; j < _hiddenSize; j++)
            {
                gradW1[i][j] = deltaHidden[j] * input[i];
            }
        }
    }

    // Методи доступу до ваг для тестування скінченних різниць
    public double GetWeightW1(int i, int j) => _w1[i][j];
    public void SetWeightW1(int i, int j, double value) => _w1[i][j] = value;
    public double GetWeightW2(int j, int k) => _w2[j][k];
    public void SetWeightW2(int j, int k, double value) => _w2[j][k] = value;
}