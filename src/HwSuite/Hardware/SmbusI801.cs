using HwSuite.Core;

namespace HwSuite.Hardware;

/// <summary>
/// Intel PCH (i801) SMBus master through the signed PawnIO "SmbusI801" module.
/// Protocol constants and the ioctl argument layout were read from the module source
/// (PawnIO.Modules/SmbusI801.p): ioctl_smbus_xfer in = [addr, read(1)/write(0), command, protocol, data...].
/// Every transaction is wrapped in the system-wide SMBus mutex so Gigabyte/other tools never collide with it.
/// </summary>
public sealed class SmbusI801 : IDisposable
{
    public const int ProtoQuick = 0, ProtoByte = 1, ProtoByteData = 2, ProtoWordData = 3, ProtoBlockData = 5;

    // Canonical name used by the PawnIO module docs, OpenRGB, LibreHardwareMonitor and HWiNFO.
    const string MutexCanonical = @"Global\Access_SMBUS.HTP.Method";
    // Spelling used by the vendor tools. Kernel object names are case-sensitive, so hold both.
    const string MutexProject = @"Global\Access_SMBus.HTP.Method";

    readonly PawnModule _module;
    readonly object _gate = new();
    readonly Mutex _mutexA;
    readonly Mutex _mutexB;

    public string Identity { get; private set; } = "";
    public uint PciVendor { get; private set; }
    public uint PciDevice { get; private set; }
    public bool? SpdWriteProtected { get; private set; }
    public long TransactionCount { get; private set; }
    /// <summary>Profiling: Stopwatch ticks spent waiting for the global SMBus mutexes, and inside the PawnIO transfer call.</summary>
    public long MutexWaitTicks, IoctlTicks, LockCount;
    public long ErrorCount { get; private set; }
    public int LastHr { get; private set; }
    public Dictionary<int, int> ErrorCodes { get; } = new();

    SmbusI801(PawnModule module)
    {
        _module = module;
        _mutexA = new Mutex(false, MutexCanonical);
        _mutexB = new Mutex(false, MutexProject);
    }

    public static SmbusI801? TryOpen(out string error)
    {
        if (RunMode.Refuse("open the SMBus")) { error = "hardware-free run"; return null; }
        var module = PawnModule.LoadEmbedded("SmbusI801", out error);
        if (module == null) return null;
        var bus = new SmbusI801(module);

        var id = new ulong[3];
        int hr = module.Execute("ioctl_identity", Array.Empty<ulong>(), id, out _);
        if (hr < 0)
        {
            error = $"ioctl_identity failed 0x{hr:X8} (no supported i801 SMBus controller found)";
            bus.Dispose();
            return null;
        }
        var chars = BitConverter.GetBytes((uint)(id[0] & 0xFFFFFFFF));
        bus.Identity = new string(chars.Select(b => (char)b).ToArray());
        bus.PciVendor = (uint)(id[2] & 0xFFFF);
        bus.PciDevice = (uint)((id[2] >> 16) & 0xFFFF);

        var wp = new ulong[1];
        if (module.Execute("ioctl_write_protection", Array.Empty<ulong>(), wp, out _) >= 0)
            bus.SpdWriteProtected = wp[0] != 0;
        return bus;
    }

    /// <summary>Acquire the global SMBus mutexes for a burst of transactions. Dispose to release.</summary>
    public IDisposable Lock(int timeoutMs = 3000)
    {
        Monitor.Enter(_gate);
        bool a = false, b = false;
        try
        {
            long w0 = System.Diagnostics.Stopwatch.GetTimestamp();
            a = Acquire(_mutexA, timeoutMs);
            if (!a) throw new TimeoutException("SMBus mutex busy (another tool owns the bus)");
            b = Acquire(_mutexB, timeoutMs);
            MutexWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - w0;
            LockCount++;
            if (!b) throw new TimeoutException("SMBus mutex (project name) busy");
            return new Releaser(this, a, b);
        }
        catch
        {
            if (b) _mutexB.ReleaseMutex();
            if (a) _mutexA.ReleaseMutex();
            Monitor.Exit(_gate);
            throw;
        }
    }

    static bool Acquire(Mutex m, int timeoutMs)
    {
        try { return m.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { return true; } // previous owner crashed; we now own it
    }

    sealed class Releaser : IDisposable
    {
        readonly SmbusI801 _bus; readonly bool _a, _b; bool _done;
        public Releaser(SmbusI801 bus, bool a, bool b) { _bus = bus; _a = a; _b = b; }
        public void Dispose()
        {
            if (_done) return; _done = true;
            try { if (_b) _bus._mutexB.ReleaseMutex(); } catch { }
            try { if (_a) _bus._mutexA.ReleaseMutex(); } catch { }
            Monitor.Exit(_bus._gate);
        }
    }

    bool Xfer(byte addr, bool read, byte command, int protocol, ulong[] data, ulong[] output, out int hr)
    {
        var input = new ulong[4 + data.Length];
        input[0] = addr; input[1] = read ? 1UL : 0UL; input[2] = command; input[3] = (ulong)protocol;
        Array.Copy(data, 0, input, 4, data.Length);
        long x0 = System.Diagnostics.Stopwatch.GetTimestamp();
        hr = _module.Execute("ioctl_smbus_xfer", input, output, out _);
        IoctlTicks += System.Diagnostics.Stopwatch.GetTimestamp() - x0;
        TransactionCount++;
        LastHr = hr;
        if (hr < 0) { ErrorCount++; ErrorCodes[hr] = ErrorCodes.GetValueOrDefault(hr) + 1; return false; }
        return true;
    }

    /// <summary>Receive-byte (no command). Used to test whether an address ACKs.</summary>
    public bool ReadByte(byte addr, out byte value)
    {
        var o = new ulong[1];
        bool ok = Xfer(addr, true, 0, ProtoByte, Array.Empty<ulong>(), o, out _);
        value = (byte)o[0];
        return ok;
    }

    public bool ReadByteData(byte addr, byte command, out byte value)
    {
        var o = new ulong[1];
        bool ok = Xfer(addr, true, command, ProtoByteData, Array.Empty<ulong>(), o, out _);
        value = (byte)o[0];
        return ok;
    }

    public bool WriteByteData(byte addr, byte command, byte value) =>
        Xfer(addr, false, command, ProtoByteData, new ulong[] { value }, Array.Empty<ulong>(), out _);

    public bool WriteWordData(byte addr, byte command, ushort value) =>
        Xfer(addr, false, command, ProtoWordData, new ulong[] { value }, Array.Empty<ulong>(), out _);

    /// <summary>SMBus block write (max 32 bytes). Packed as: first byte = length, then data, little-endian in 8-byte cells.</summary>
    public bool WriteBlockData(byte addr, byte command, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 32) return false;
        var packed = new byte[40];
        packed[0] = (byte)data.Length;
        data.CopyTo(packed.AsSpan(1));
        var cells = new ulong[5];
        for (int i = 0; i < 5; i++) cells[i] = BitConverter.ToUInt64(packed, i * 8);
        return Xfer(addr, false, command, ProtoBlockData, cells, Array.Empty<ulong>(), out _);
    }

    public void Dispose()
    {
        _module.Dispose();
        _mutexA.Dispose();
        _mutexB.Dispose();
    }
}

