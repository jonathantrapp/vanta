using System.Reflection;
using System.Runtime.InteropServices;
using HwSuite.Core;

namespace HwSuite.Hardware;

/// <summary>
/// Thin wrapper over PawnIOLib.dll. Signatures taken from C:\Program Files\PawnIO\PawnIOLib.h and the
/// DLL export table (pawnio_version / open / load / execute / close). Only signed PawnIO modules
/// (the .bin blobs embedded in LibreHardwareMonitorLib) are ever loaded.
/// </summary>
public static class PawnIoNative
{
    const string Lib = "PawnIOLib";

    static PawnIoNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(PawnIoNative).Assembly, (name, asm, path) =>
        {
            if (!string.Equals(name, Lib, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            foreach (var candidate in Candidates())
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h)) return h;
            }
            return IntPtr.Zero;
        });
    }

    static IEnumerable<string> Candidates()
    {
        yield return @"C:\Program Files\PawnIO\PawnIOLib.dll";
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf, "PawnIO", "PawnIOLib.dll");
    }

    public static bool IsInstalled()
    {
        try { return Candidates().Any(File.Exists); } catch { return false; }
    }

    [DllImport(Lib, EntryPoint = "pawnio_version")] public static extern int Version(out uint version);
    [DllImport(Lib, EntryPoint = "pawnio_open")] public static extern int Open(out IntPtr handle);
    [DllImport(Lib, EntryPoint = "pawnio_load")] public static extern int Load(IntPtr handle, byte[] blob, UIntPtr size);
    [DllImport(Lib, EntryPoint = "pawnio_execute")]
    public static extern int Execute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returnSize);
    [DllImport(Lib, EntryPoint = "pawnio_close")] public static extern int Close(IntPtr handle);
}

public sealed class PawnModule : IDisposable
{
    IntPtr _handle;
    public string Name { get; }

    PawnModule(IntPtr handle, string name) { _handle = handle; Name = name; }

    public static string? PawnIoVersionString()
    {
        try
        {
            if (PawnIoNative.Version(out var v) < 0) return null;
            return $"{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
        }
        catch { return null; }
    }

    /// <summary>Load one of the signed module blobs embedded in LibreHardwareMonitorLib (e.g. "SmbusI801").</summary>
    public static PawnModule? LoadEmbedded(string moduleName, out string error)
    {
        error = "";
        try
        {
            var asm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            var res = $"LibreHardwareMonitor.Resources.PawnIo.{moduleName}.bin";
            using var s = asm.GetManifestResourceStream(res);
            if (s == null) { error = $"embedded module {res} not found"; return null; }
            var blob = new byte[s.Length];
            int read = 0;
            while (read < blob.Length) { int n = s.Read(blob, read, blob.Length - read); if (n <= 0) break; read += n; }

            int hr = PawnIoNative.Open(out var h);
            if (hr < 0) { error = $"pawnio_open failed 0x{hr:X8}"; return null; }
            hr = PawnIoNative.Load(h, blob, (UIntPtr)blob.Length);
            if (hr < 0) { PawnIoNative.Close(h); error = $"pawnio_load({moduleName}) failed 0x{hr:X8}"; return null; }
            return new PawnModule(h, moduleName);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = "PawnIOLib.dll unavailable: " + ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Runs a module ioctl. Returns the HRESULT (negative = failure).</summary>
    public int Execute(string function, ulong[] input, ulong[] output, out int returned)
    {
        returned = 0;
        if (_handle == IntPtr.Zero) return unchecked((int)0x80004005);
        int hr = PawnIoNative.Execute(_handle, function, input, (UIntPtr)input.Length, output, (UIntPtr)output.Length, out var ret);
        returned = (int)ret;
        return hr;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            try { PawnIoNative.Close(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
    }
}
