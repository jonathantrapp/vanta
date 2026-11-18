using System.Net.Sockets;
using Avalonia;
using System.Runtime.InteropServices;
using System.Text;
using HidSharp;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Platform;

namespace HwSuite;

/// <summary>
/// LINUX entry point (the Windows Program.cs is WPF + Windows CLI and is not linked).
///   vanta --daemon            the engine: fans, lighting, AL80 (run by vantad.service as root)
///   vanta --discover          read-only hardware report (no writes of any kind)
///   vanta --fan-verify        step test that opens the motherboard fan gate (owner present; writes fans)
///   vanta --selftest --no-hardware   the shared unit checks (no device opened)
///   vanta --restore           hand every claimed fan back to automatic (also the service's ExecStopPost)
///   vanta --watchdog PID      failsafe companion process (spawned by the daemon)
///   vanta --notify suspend|resume|status   talk to a running daemon (systemd sleep hook)
///   vanta --version
/// </summary>
public static class Program
{
    public const string InstanceMutexName = @"Global\HwSuite.SingleInstance";
    public static string ControlSocketPath => Path.Combine(Heartbeat.RunDir, "control.sock");

    static string? OptionValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int Main(string[] args)
    {
        // hardware-free runs get an isolated data folder before anything touches AppPaths (same rule as Windows)
        if (RunMode.IsHardwareFreeCommand(args))
        {
            RunMode.EnterHardwareFree(OptionValue(args, "--test-data") ?? RunMode.DefaultIsolatedFolder());
            ConfigStore.ReadOnly = true;
        }
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        Log.RotateIfLarge();
        switch (mode)
        {
            case "--selftest":
            {
                Log.ConsoleEnabled = true;
                NativeResolver.Install();
                if (args.Contains("--no-hardware"))
                {
                    int rc = Cli.SelfTest.Run(false, hardware: false);
                    Console.WriteLine($"isolated test data and log: {AppPaths.Data}");
                    return rc;
                }
                using var single = new Mutex(false, InstanceMutexName);
                bool hw;
                try { hw = single.WaitOne(0); } catch (AbandonedMutexException) { hw = true; }
                return Cli.SelfTest.Run(false, hardware: hw);
            }
            case "--version":
                Console.WriteLine($"VANTA for Linux {typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion}");
                return 0;
            case "--discover":
                Log.ConsoleEnabled = false;
                return Discover.Run();
            case "--fan-verify":
            {
                using var single = new Mutex(false, InstanceMutexName);
                bool own;
                try { own = single.WaitOne(0); } catch (AbandonedMutexException) { own = true; }
                if (!own) { Console.WriteLine("the VANTA engine is running: sudo systemctl stop vantad first"); return 3; }
                return FanVerify.Run(args.Contains("--yes"));
            }
            case "--restore":
            {
                Log.ConsoleEnabled = true;
                var o = FailsafeWatchdog.RestoreAll();
                Console.WriteLine(o.Text);
                return o.ExitCode;
            }
            case "--watchdog":
                return args.Length > 1 && int.TryParse(args[1], out var pid) ? FailsafeWatchdog.RunWatchdog(pid) : 2;
            case "--notify":
                return Notify(args.Length > 1 ? args[1] : "status");
            case "--ui-shot":
            {
                // fixture screenshots: simulated states only, nothing opened (hardware-free run)
                Log.ConsoleEnabled = true;
                string dir = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(AppPaths.Data, "shots");
                var themes = OptionValue(args, "--theme") is string th ? Ui.Themes.All.Where(t => t.Id.Equals(th, StringComparison.OrdinalIgnoreCase)) : Ui.Themes.All;
                var sizes = OptionValue(args, "--size") is string sz ? App.AllSizes.Where(s => s.Name.StartsWith(sz)) : new[] { App.AllSizes[1] };
                var fixtures = OptionValue(args, "--fixture") is string fx ? Ui.UiFixture.All().Where(f => f.Id.Contains(fx)) : Ui.UiFixture.All();
                App.ThemeId = Ui.Themes.DefaultId;
                App.Run = (app, desktop) =>
                {
                    int f = app.RunFixtures(dir, themes.ToList(), sizes.ToList(), fixtures.ToList());
                    Console.WriteLine($"{(f == 0 ? "all fixtures rendered" : f + " fixture(s) failed")}; images in {dir}");
                    desktop.Shutdown(f == 0 ? 0 : 1);
                    return Task.FromResult(f == 0 ? 0 : 1);
                };
                return RunAvalonia(args);
            }
            case "--daemon":
                Log.ConsoleEnabled = args.Contains("--console");
                return RunDaemon();
            case "":
            case "--minimized":
            {
                // the app: engine + window, as the logged-in user (like Windows VANTA). A second launch shows the first.
                if (Notify("show", quiet: true) == 0) return 0;
                App.StartMinimized = mode == "--minimized";
                Log.ConsoleEnabled = args.Contains("--console");
                NativeResolver.Install();
                using var single = new Mutex(false, InstanceMutexName);
                bool owned;
                try { owned = single.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) { Console.Error.WriteLine("vanta: the engine is already running (headless service or a hardware test); stop it first"); return 3; }
                return RunAvalonia(args);
            }
            default:
                Console.WriteLine("usage: vanta [--minimized] | --daemon | --discover | --fan-verify | --selftest --no-hardware | --restore | --watchdog PID | --notify suspend|resume|status | --version");
                return mode is "--help" or "-h" ? 0 : 2;
        }
    }

    static int RunDaemon()
    {
        using var single = new Mutex(false, InstanceMutexName);
        bool owned;
        try { owned = single.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) { Console.Error.WriteLine("vanta: another engine is already running"); return 3; }

        Log.Info($"VANTA for Linux starting (pid {Environment.ProcessId}, {(LibC.IsRoot ? "root" : "NOT root: fans and SMBus will be read-only or unavailable")}); data {AppPaths.Data}");
        NativeResolver.Install();
        var stop = new ManualResetEventSlim();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Set(); });
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Set(); });

        var services = new AppServices();
        try
        {
            services.InitializeHardware();
            services.InitializeLighting();
            foreach (var n in services.Notes) Log.Info("startup: " + n);
            FailsafeWatchdog.SpawnFor(Environment.ProcessId);
            services.Start(heartbeat: true);
            using var control = new ControlSocket(ControlSocketPath, services);
            control.Start();
            Log.Info("VANTA engine running");
            stop.Wait();
            Log.Info("VANTA engine stopping (signal)");
        }
        catch (Exception ex) { Log.Error("engine failed", ex); }
        finally
        {
            try { services.Dispose(); } catch (Exception ex) { Log.Error("shutdown", ex); }
            try { ConfigStore.Save(services.Config); } catch { }
        }
        return 0;
    }

    static int RunAvalonia(string[] args)
    {
        Avalonia.AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return App.ExitCode;
    }

    static int Notify(string cmd, bool quiet = false)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(ControlSocketPath));
            s.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = s.Receive(buf);
            Console.WriteLine(Encoding.UTF8.GetString(buf, 0, n).TrimEnd());
            return 0;
        }
        catch (Exception ex) { if (!quiet) Console.Error.WriteLine("vanta: engine not reachable at " + ControlSocketPath + ": " + ex.Message); return 1; }
    }
}

/// <summary>Maps the Windows library names used by shared [DllImport]s to their Linux files.</summary>
public static class NativeResolver
{
    static int _done;
    public static void Install()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NvmlGpu).Assembly, (name, asm, path) =>
            {
                if (name.StartsWith("nvml", StringComparison.OrdinalIgnoreCase))
                    foreach (var cand in new[] { "libnvidia-ml.so.1", "libnvidia-ml.so" })
                        if (NativeLibrary.TryLoad(cand, asm, path, out var h)) return h;
                return IntPtr.Zero;
            });
        }
        catch (InvalidOperationException) { /* already set */ }
    }
}

/// <summary>
/// Line-based Unix socket for the systemd sleep hook and (later) the UI. Commands: status, suspend, resume.
/// Root-owned socket in /run/vanta (mode 0660, group vanta set by the service's RuntimeDirectory settings).
/// </summary>
public sealed class ControlSocket : IDisposable
{
    readonly string _path;
    readonly AppServices _svc;
    Socket? _listen;
    volatile bool _stop;

    public ControlSocket(string path, AppServices svc) { _path = path; _svc = svc; }
    /// <summary>Set by the GUI: a second launch sends "show".</summary>
    public Action? ShowWindow { get; init; }

    public void Start()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        _listen = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listen.Bind(new UnixDomainSocketEndPoint(_path));
        _listen.Listen(4);
        try { File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite); } catch { }
        new Thread(Loop) { IsBackground = true, Name = "Control socket" }.Start();
    }

    void Loop()
    {
        while (!_stop)
        {
            Socket c;
            try { c = _listen!.Accept(); } catch { if (_stop) return; Thread.Sleep(100); continue; }
            using (c)
            {
                try
                {
                    var buf = new byte[512];
                    int n = c.Receive(buf);
                    var cmd = Encoding.UTF8.GetString(buf, 0, n).Trim().ToLowerInvariant();
                    c.Send(Encoding.UTF8.GetBytes(Handle(cmd) + "\n"));
                }
                catch (Exception ex) { Log.Warn("control socket: " + ex.Message); }
            }
        }
    }

    string Handle(string cmd)
    {
        switch (cmd)
        {
            case "show": if (ShowWindow == null) return "error no window (headless engine)"; ShowWindow(); return "ok shown";
            case "suspend": _svc.PowerEvent(true); return "ok suspended";
            case "resume": _svc.PowerEvent(false); return "ok resumed";
            case "status":
            {
                var s = _svc.Hub.Latest;
                var controlled = _svc.Hub.Engine.Channels.Count(ch => _svc.Hub.Engine.IsControlled(ch.Id));
                return $"ok cpu {s.CpuTempC?.ToString("0") ?? "-"}C gpu {s.GpuTempC?.ToString("0") ?? "-"}C; {_svc.Hub.Engine.Channels.Count} fan channel(s), {controlled} driven; " +
                       $"lighting {(_svc.Config.Rgb.Enabled ? "on" : "off")}, {_svc.Rgb.Sinks.Count(x => x.Available)}/{_svc.Rgb.Sinks.Count} device(s) ready";
            }
            default: return "error unknown command (status | show | suspend | resume)";
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _listen?.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }
}
