namespace HwSuite.Core;

/// <summary>Thrown when a hardware-free run reaches a boundary that would open hardware, run recovery or operate services.</summary>
public sealed class HardwareFreeViolation : InvalidOperationException
{
    public HardwareFreeViolation(string what) : base($"hardware-free run: refused to {what}") { }
}

/// <summary>
/// Process-wide run mode, decided once in Program.Main before anything else (in particular before <see cref="AppPaths"/>
/// is first touched). Hardware-free runs (--selftest --no-hardware, --ui-fixtures, --ui-shot) use an isolated data
/// folder and log, and every boundary that would open a device, start recovery, spawn the watchdog or operate a Windows
/// service refuses (and is counted) instead. Normal app runs are unaffected: <see cref="HardwareFree"/> stays false.
/// </summary>
public static class RunMode
{
    static int _refusals;
    static readonly List<string> _refused = new();

    /// <summary>Set once at start; never cleared.</summary>
    public static bool HardwareFree { get; private set; }
    /// <summary>Isolated data folder of a hardware-free run (null for normal runs).</summary>
    public static string? IsolatedData { get; private set; }

    /// <summary>Boundary refusals so far (a fault-free hardware-free run expects only the ones its tests provoke on purpose).</summary>
    public static int Refusals => Volatile.Read(ref _refusals);
    public static List<string> Refused { get { lock (_refused) return _refused.ToList(); } }

    /// <summary>Enter hardware-free mode with an isolated data folder. Must run before AppPaths is used.</summary>
    public static void EnterHardwareFree(string isolatedData)
    {
        IsolatedData = isolatedData;
        HardwareFree = true;
    }

    /// <summary>True (and counted) when the current run must not do <paramref name="what"/>.</summary>
    public static bool Refuse(string what)
    {
        if (!HardwareFree) return false;
        Interlocked.Increment(ref _refusals);
        lock (_refused) { if (_refused.Count < 64) _refused.Add(what); }
        try { Log.Warn("hardware-free run: refused to " + what); } catch { }
        return true;
    }

    /// <summary>Throws <see cref="HardwareFreeViolation"/> in a hardware-free run.</summary>
    public static void Assert(string what)
    {
        if (Refuse(what)) throw new HardwareFreeViolation(what);
    }

    /// <summary>The option values that make a run hardware-free.</summary>
    public static bool IsHardwareFreeCommand(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        return mode is "--ui-fixtures" or "--ui-shot" or "--ui-idle-bench" or "--scene-bench" or "--scene-probe" || (mode == "--selftest" && args.Contains("--no-hardware"));
    }

    /// <summary>
    /// Default isolated folder: %TEMP%\VANTA-test\&lt;time&gt;-&lt;pid&gt;. Older run folders beyond the newest 12 are removed
    /// (bounded; only folders under VANTA-test that this naming produced).
    /// </summary>
    public static string DefaultIsolatedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "VANTA-test");
        try
        {
            if (Directory.Exists(root))
                foreach (var old in new DirectoryInfo(root).GetDirectories("run-*").OrderByDescending(d => d.CreationTimeUtc).Skip(12))
                    try { old.Delete(true); } catch { }
        }
        catch { }
        return Path.Combine(root, $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
    }
}
