namespace Core;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

public sealed class PythonChannel : IChannel, IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private bool _disposed;

    private const string PythonRunnerScript =
        "import sys, json, base64\n" +
        "sys.path.insert(0, sys.argv[1])\n" +
        "from channel import Channel\n" +
        "scenario = json.loads(base64.b64decode(sys.argv[2]).decode('utf-8'))\n" +
        "seed = int(sys.argv[3])\n" +
        "ch = Channel(scenario, seed)\n" +
        "for line in sys.stdin:\n" +
        "    cmd = line.strip()\n" +
        "    if not cmd:\n" +
        "        continue\n" +
        "    if cmd == 'STATS':\n" +
        "        print(json.dumps(ch.stats(), ensure_ascii=False), flush=True)\n" +
        "    elif cmd == 'QUIT':\n" +
        "        break\n" +
        "    else:\n" +
        "        n = int(cmd)\n" +
        "        prev_ob = ch.over_budget\n" +
        "        ok, arr = ch.send(b'\\x00' * n)\n" +
        "        ob = 1 if ch.over_budget > prev_ob else 0\n" +
        "        arr_s = '-1' if arr is None else repr(arr)\n" +
        "        print(f\"{1 if ok else 0},{arr_s},{ob}\", flush=True)\n";

    public PythonChannel(string channelPyPath, string scenarioRawJson, int seed)
    {
        if (!File.Exists(channelPyPath))
            throw new FileNotFoundException($"Не знайдено еталонний файл каналу: {channelPyPath}");

        string channelDir = Path.GetDirectoryName(Path.GetFullPath(channelPyPath))!;
        string scenarioB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(scenarioRawJson));

        _process = StartPythonProcess(channelDir, scenarioB64, seed);
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
    }

    private static Process StartPythonProcess(string channelDir, string scenarioB64, int seed)
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["python", "python3"]
            : ["python3", "python"];

        Exception? lastEx = null;
        foreach (var exe in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment["PYTHONUTF8"] = "1";

                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(PythonRunnerScript);
                psi.ArgumentList.Add(channelDir);
                psi.ArgumentList.Add(scenarioB64);
                psi.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));

                var proc = Process.Start(psi);
                if (proc != null)
                    return proc;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw new InvalidOperationException("Не вдалося запустити Python для channel.py. Перевірте PATH.", lastEx);
    }

    public ChannelResult Send(ReadOnlySpan<byte> packetBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stdin.WriteLine(packetBytes.Length);
        _stdin.Flush();

        string? response = _stdout.ReadLine();
        if (string.IsNullOrEmpty(response))
        {
            string err = _process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"channel.py не відповів: {err}");
        }

        var parts = response.Split(',');
        bool delivered = parts[0] == "1";
        double arrival = double.Parse(parts[1], CultureInfo.InvariantCulture);
        bool droppedByBudget = parts[2] == "1";

        return new ChannelResult(delivered, arrival, droppedByBudget);
    }

    public Dictionary<string, object> GetStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stdin.WriteLine("STATS");
        _stdin.Flush();

        string? jsonLine = _stdout.ReadLine();
        if (string.IsNullOrEmpty(jsonLine))
            return new Dictionary<string, object>();

        using var doc = JsonDocument.Parse(jsonLine);
        var stats = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            stats[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Number when prop.Value.TryGetInt32(out int i) => i,
                JsonValueKind.Number => prop.Value.GetDouble(),
                _ => prop.Value.ToString()
            };
        }
        return stats;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _stdin.WriteLine("QUIT");
                _stdin.Flush();
                _process.WaitForExit(500);
            }
        }
        catch { /* ignore */ }
        finally
        {
            _stdin.Dispose();
            _stdout.Dispose();
            if (!_process.HasExited)
            {
                try { _process.Kill(); } catch { /* ignore */ }
            }
            _process.Dispose();
        }
    }
}
