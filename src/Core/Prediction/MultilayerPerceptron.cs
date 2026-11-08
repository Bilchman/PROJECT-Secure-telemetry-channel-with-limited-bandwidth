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

    // Прямий прохід, активація LeakyReLU на прихованому шарі та чисельно стабільний Softmax на виході
    public double[] Forward(double[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length != _inputSize)
        {
            throw new ArgumentException($"Очікувалось {_inputSize} ознак, отримано {input.Length}.");
        }

        //Прихований шар із лінійним зважуванням та LeakyReLU
        var hiddenAct = new double[_hiddenSize];
        for (int j = 0; j < _hiddenSize; j++)
        {
            double sum = _b1[j];
            for (int i = 0; i < _inputSize; i++)
            {
                sum += input[i] * _w1[i][j];
            }
            hiddenAct[j] = sum >= 0.0 ? sum : LeakySlope * sum;
        }

        //Вихідний шар для обчислення лінійних комбінацій та пошук maxNet
        var outputNet = new double[_outputSize];
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

        //Чисельно стабільний Softmax exp(z_k - maxNet)
        var probabilities = new double[_outputSize];
        double sumExp = 0.0;
        for (int k = 0; k < _outputSize; k++)
        {
            double exp = Math.Exp(outputNet[k] - maxNet);
            probabilities[k] = exp;
            sumExp += exp;
        }

        // Нормалізація ймовірностей (сума строго прямує до 1.0)
        for (int k = 0; k < _outputSize; k++)
        {
            probabilities[k] /= sumExp;
        }

        return probabilities;
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
}