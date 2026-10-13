using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;

// Exercise the real app in --no-discord mode. No test framework or Discord/game installation needed.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Complete startup zone and live appended zone", async () =>
    {
        using var log = new WatchedLog(ZoneLine("The Fabled Ruins of Guk"));
        await log.Expect("\"In The Fabled Ruins of Guk\"");
        log.Append(ZoneLine("Commonlands"));
        await log.Expect("zone: Commonlands");
    }),
    ("Unfinished startup line is completed without publishing a shortened zone", async () =>
    {
        using var log = new WatchedLog(Prefix() + "You have entered Ant");
        await log.Expect("\"Zone not known yet\"");
        log.Append("onica.\r\n");
        await log.Expect("zone: Antonica");
        log.Reject("\"In Ant\"");
    }),
    ("Startup fragment preserves UTF-8 bytes and the previous complete zone", async () =>
    {
        byte[] pending = Encoding.UTF8.GetBytes(ZoneLine("Forêt enchantée"));
        int split = Array.IndexOf(pending, (byte)0xc3) + 1;
        byte[] initial = Encoding.UTF8.GetBytes(ZoneLine("Antonica")).Concat(pending[..split]).ToArray();
        using var log = new WatchedLog(initial);
        await log.Expect("\"In Antonica\"");
        log.Append(pending[split..]);
        await log.Expect("zone: Forêt enchantée");
    }),
    ("Shorter replacement log updates the zone", async () =>
    {
        using var log = new WatchedLog(ZoneLine("Antonica") + new string('x', 4000) + "\n");
        await log.Expect("\"In Antonica\"");
        log.Replace(ZoneLine("Qeynos"));
        await log.Expect("\"In Qeynos\"");
    }),
    ("Truncation clears the old zone and retains an unfinished replacement line", async () =>
    {
        using var log = new WatchedLog(ZoneLine("Antonica") + new string('x', 4000) + "\n");
        await log.Expect("\"In Antonica\"");
        log.Replace(Prefix() + "You have entered Com");
        await log.Expect("\"Zone not known yet\"");
        log.Append("monlands.\r\n");
        await log.Expect("zone: Commonlands");
        log.Reject("\"In Com\"");
    }),
};

int failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} regression checks passed.");
return failed == 0 ? 0 : 1;

static string Prefix() => "(1140157349)[Fri Feb 17 06:22:29 2006] ";
static string ZoneLine(string zone) => Prefix() + $"You have entered {zone}.\r\n";

sealed class WatchedLog : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eq2-presence-test-" + Guid.NewGuid());
    private readonly string _log;
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _output = new();

    public WatchedLog(string initial) : this(Encoding.UTF8.GetBytes(initial)) { }

    public WatchedLog(byte[] initial)
    {
        string server = Path.Combine(_root, "Maj'Dul");
        Directory.CreateDirectory(server);
        _log = Path.Combine(server, "eq2log_Bob.txt");
        File.WriteAllBytes(_log, initial);

        string runner = Assembly.GetExecutingAssembly().Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(runner, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(runner, ".deps.json"),
            Assembly.Load("EQ2Presence").Location, "--no-discord", _root,
        })
            start.ArgumentList.Add(arg);

        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) _output.Enqueue(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _output.Enqueue(e.Data); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void Append(string text) => Append(Encoding.UTF8.GetBytes(text));
    public void Append(byte[] bytes)
    {
        using var stream = new FileStream(_log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }
    public void Replace(string text) => File.WriteAllBytes(_log, Encoding.UTF8.GetBytes(text));

    public async Task Expect(string text)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (_output.Any(line => line.Contains(text, StringComparison.Ordinal))) return;
            if (_process.HasExited) break;
            await Task.Delay(50);
        }
        throw new Exception($"Expected {text}. Output:\n{string.Join('\n', _output)}");
    }

    public void Reject(string text)
    {
        if (_output.Any(line => line.Contains(text, StringComparison.Ordinal)))
            throw new Exception($"Unexpected {text}. Output:\n{string.Join('\n', _output)}");
    }

    public void Dispose()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _process.WaitForExit();
        _process.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
