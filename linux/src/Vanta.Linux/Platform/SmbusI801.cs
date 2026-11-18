using System.Runtime.InteropServices;
using HwSuite.Core;
using HwSuite.Platform;

namespace HwSuite.Hardware;

/// <summary>
/// LINUX replacement for src\HwSuite\Hardware\SmbusI801.cs (same class name and public surface, so EneDramController and
/// the RAM sink link unchanged). Intel PCH (i801) SMBus master through the kernel's i2c-i801 adapter and /dev/i2c-N
/// (module i2c-dev). Transfers use the I2C_SMBUS ioctl (linux/i2c-dev.h, linux/i2c.h); the protocol numbers are the
/// kernel's I2C_SMBUS_* sizes, which are the same values the Windows PawnIO module uses.
///
/// Mutual exclusion: the kernel serialises single transfers on the adapter; a burst of transfers (ENE register address
/// write + data) is held together with an exclusive flock on /run/lock/vanta-smbus.lock plus an in-process lock. Other
/// programs (OpenRGB) do not take that file lock, so they must not run at the same time.
/// I2C_SLAVE (not _FORCE) is used: if a kernel driver owns an address (ee1004/spd5118 on 0x50-0x57) we never touch it.
/// </summary>
public sealed class SmbusI801 : IDisposable
{
    public const int ProtoQuick = 0, ProtoByte = 1, ProtoByteData = 2, ProtoWordData = 3, ProtoBlockData = 5;

    const nuint I2C_SLAVE = 0x0703, I2C_SMBUS = 0x0720, I2C_FUNCS = 0x0705;
    const byte I2C_SMBUS_WRITE = 0, I2C_SMBUS_READ = 1;
    public const string LockFile = "/run/lock/vanta-smbus.lock";

    readonly int _fd;
    readonly object _gate = new();
    int _lockFd = -1;
    int _slave = -1;

    public string Identity { get; private set; } = "";
    public string DevicePath { get; private set; } = "";
    public uint PciVendor { get; private set; }
    public uint PciDevice { get; private set; }
    public bool? SpdWriteProtected { get; private set; }
    public long TransactionCount { get; private set; }
    public long MutexWaitTicks, IoctlTicks, LockCount;
    public long ErrorCount { get; private set; }
    /// <summary>Last result: 0 or the negative errno of the failed ioctl (the Windows build keeps an HRESULT here).</summary>
    public int LastHr { get; private set; }
    public Dictionary<int, int> ErrorCodes { get; } = new();

    SmbusI801(int fd) { _fd = fd; }

    /// <summary>The i801 adapter's /dev node, found by name in sysfs ("SMBus I801 adapter at efa0").</summary>
    public static (string dev, string sysAdapter, string name)? FindAdapter()
    {
        const string root = "/sys/class/i2c-dev";
        if (!Directory.Exists(root)) return null;
        foreach (var d in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
        {
            string name = "";
            try { name = File.ReadAllText(Path.Combine(d, "name")).Trim(); } catch { }
            if (name.StartsWith("SMBus I801", StringComparison.OrdinalIgnoreCase))
                return ("/dev/" + Path.GetFileName(d), Path.Combine(d, "device"), name);
        }
        return null;
    }

    public static SmbusI801? TryOpen(out string error)
    {
        if (RunMode.Refuse("open the SMBus")) { error = "hardware-free run"; return null; }
        if (!OperatingSystem.IsLinux()) { error = "not Linux"; return null; }
        var a = FindAdapter();
        if (a == null)
        {
            error = Directory.Exists("/sys/class/i2c-dev")
                ? "no 'SMBus I801' adapter in /sys/class/i2c-dev (is i2c-i801 loaded? on some boards it needs acpi_enforce_resources=lax)"
                : "i2c-dev is not loaded (modprobe i2c-dev; the install script adds it to /etc/modules-load.d)";
            return null;
        }
        int fd = LibC.open(a.Value.dev, LibC.O_RDWR | LibC.O_CLOEXEC, 0);
        if (fd < 0) { error = $"open {a.Value.dev}: {LibC.Errno()} (needs root or the i2c udev rule)"; return null; }
        var bus = new SmbusI801(fd) { DevicePath = a.Value.dev, Identity = a.Value.name };

        // the adapter's parent is the PCI function (00:1f.4); vendor/device and the host config register come from there
        try
        {
            // /sys/class/i2c-dev/i2c-N/device -> .../0000:00:1f.4/i2c-N (the adapter); its parent directory is the PCI function
            var adapter = Sysfs.RealPath(a.Value.sysAdapter);
            var pci = Path.GetDirectoryName(adapter)!;
            bus.PciVendor = Convert.ToUInt32(File.ReadAllText(Path.Combine(pci, "vendor")).Trim(), 16);
            bus.PciDevice = Convert.ToUInt32(File.ReadAllText(Path.Combine(pci, "device")).Trim(), 16);
            // HOSTC (config 0x40) bit 4 = SPD_WD: SPD write disable (same fact the PawnIO module reports on Windows)
            var cfg = File.ReadAllBytes(Path.Combine(pci, "config"));
            if (cfg.Length > 0x40) bus.SpdWriteProtected = (cfg[0x40] & 0x10) != 0;
        }
        catch { }
        error = "";
        return bus;
    }

    /// <summary>Hold the bus for a burst of transactions. Dispose to release.</summary>
    public IDisposable Lock(int timeoutMs = 3000)
    {
        Monitor.Enter(_gate);
        try
        {
            long w0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lockFd < 0)
            {
                _lockFd = LibC.open(LockFile, LibC.O_RDWR | LibC.O_CREAT | LibC.O_CLOEXEC, 0x1B6 /*0666*/);
                if (_lockFd < 0) _lockFd = LibC.open(Path.Combine(Path.GetTempPath(), "vanta-smbus.lock"), LibC.O_RDWR | LibC.O_CREAT | LibC.O_CLOEXEC, 0x1B6);
            }
            if (_lockFd >= 0)
            {
                var deadline = Environment.TickCount64 + timeoutMs;
                while (LibC.flock(_lockFd, LibC.LOCK_EX | LibC.LOCK_NB) != 0)
                {
                    if (Environment.TickCount64 > deadline) throw new TimeoutException("SMBus lock busy (another VANTA process owns the bus)");
                    Thread.Sleep(2);
                }
            }
            MutexWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - w0;
            LockCount++;
            return new Releaser(this);
        }
        catch { Monitor.Exit(_gate); throw; }
    }

    sealed class Releaser : IDisposable
    {
        readonly SmbusI801 _bus; bool _done;
        public Releaser(SmbusI801 bus) { _bus = bus; }
        public void Dispose()
        {
            if (_done) return; _done = true;
            try { if (_bus._lockFd >= 0) LibC.flock(_bus._lockFd, LibC.LOCK_UN); } catch { }
            Monitor.Exit(_bus._gate);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoctlData { public byte ReadWrite; public byte Command; public uint Size; public nint Data; }

    unsafe bool Xfer(byte addr, byte readWrite, byte command, int size, byte* data)
    {
        long x0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int rc = 0;
        lock (_gate)
        {
            if (_slave != addr)
            {
                rc = LibC.ioctl(_fd, I2C_SLAVE, addr);
                if (rc == 0) _slave = addr;
            }
            if (rc == 0)
            {
                var a = new IoctlData { ReadWrite = readWrite, Command = command, Size = (uint)size, Data = (nint)data };
                rc = LibC.ioctl(_fd, I2C_SMBUS, (nint)(&a));
            }
        }
        IoctlTicks += System.Diagnostics.Stopwatch.GetTimestamp() - x0;
        TransactionCount++;
        if (rc != 0)
        {
            int e = -LibC.LastErrno;
            LastHr = e; ErrorCount++; ErrorCodes[e] = ErrorCodes.GetValueOrDefault(e) + 1;
            return false;
        }
        LastHr = 0;
        return true;
    }

    /// <summary>Receive-byte (no command). Used to test whether an address ACKs.</summary>
    public unsafe bool ReadByte(byte addr, out byte value)
    {
        byte* d = stackalloc byte[34];
        bool ok = Xfer(addr, I2C_SMBUS_READ, 0, ProtoByte, d);
        value = d[0];
        return ok;
    }

    public unsafe bool ReadByteData(byte addr, byte command, out byte value)
    {
        byte* d = stackalloc byte[34];
        bool ok = Xfer(addr, I2C_SMBUS_READ, command, ProtoByteData, d);
        value = d[0];
        return ok;
    }

    public unsafe bool WriteByteData(byte addr, byte command, byte value)
    {
        byte* d = stackalloc byte[34];
        d[0] = value;
        return Xfer(addr, I2C_SMBUS_WRITE, command, ProtoByteData, d);
    }

    public unsafe bool WriteWordData(byte addr, byte command, ushort value)
    {
        byte* d = stackalloc byte[34];
        d[0] = (byte)value; d[1] = (byte)(value >> 8);
        return Xfer(addr, I2C_SMBUS_WRITE, command, ProtoWordData, d);
    }

    /// <summary>SMBus block write (max 32 bytes): union i2c_smbus_data.block[0] = length, then the data.</summary>
    public unsafe bool WriteBlockData(byte addr, byte command, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 32) return false;
        byte* d = stackalloc byte[34];
        d[0] = (byte)data.Length;
        for (int i = 0; i < data.Length; i++) d[1 + i] = data[i];
        return Xfer(addr, I2C_SMBUS_WRITE, command, ProtoBlockData, d);
    }

    public void Dispose()
    {
        try { if (_lockFd >= 0) LibC.close(_lockFd); } catch { }
        try { LibC.close(_fd); } catch { }
    }
}
