using System.Text;
using HwSuite.Core;
using HwSuite.Hardware;

namespace HwSuite.Lighting.Devices;

public sealed class EneDimm
{
    public byte Address { get; set; }
    public string Version { get; set; } = "";
    public int LedCount { get; set; }
    public ushort DirectReg { get; set; }
    public ushort EffectReg { get; set; }
    public bool Supported { get; set; }
    public int Slot { get; set; } = -1;
    internal byte[]? LastFrame;
    /// <summary>null = not yet verified this session; true = whole-module block writes read back correctly; false = use 3-byte blocks.</summary>
    public bool? WholeBlockOk { get; internal set; }
    public long ModuleWrites, ModuleSkips;

    public override string ToString() => $"0x{Address:X2} {Version} ({LedCount} LEDs){(Supported ? "" : " [unsupported]")}";
}

/// <summary>Result of a register write that is checked by reading it back.</summary>
public enum EneOutcome { Confirmed, Unconfirmed, Failed }

/// <summary>What the RAM sink needs from the ENE controllers (the real controller, or a fake in tests).</summary>
public interface IEneModules
{
    IReadOnlyList<EneDimm> Modules { get; }
    void EnterDirect(EneDimm d);
    int WriteFrame(EneDimm d, ReadOnlySpan<Rgb> leds);
    EneOutcome SetHardwareEffect(EneDimm d, byte mode, Rgb color, byte speed, out string detail);
    void SetStaticLook(EneDimm d, Rgb color);
}

/// <summary>
/// ENE SMBus DRAM RGB controller (XPG Spectrix D60G). Register map, access protocol and the remap flow were read
/// from OpenRGB's ENESMBusController sources. Safety additions over the reference implementation:
///  * the SMBus controller identity is checked first;
///  * nothing is ever written to an address that has not passed the ENE signature test (regs 0xA0..0xAF == 0..15);
///  * the 0x77 remap only runs when 0x77 itself passes that signature test;
///  * ENE flash (save command 0xAA) is never written.
/// </summary>
public sealed class EneDramController : IEneModules, IDisposable
{
    const ushort RegDeviceName = 0x1000, RegMicronCheck = 0x1030, RegConfigTable = 0x1C00;
    const ushort RegColorsDirect = 0x8000, RegColorsEffect = 0x8010, RegDirect = 0x8020, RegMode = 0x8021;
    const ushort RegSpeed = 0x8022, RegDirection = 0x8023, RegApply = 0x80A0, RegSlotIndex = 0x80F8, RegI2cAddress = 0x80F9;
    const ushort RegColorsDirectV2 = 0x8100, RegColorsEffectV2 = 0x8160;
    const byte ApplyValue = 0x01, ModeStatic = 1, SpeedNormal = 2, DirectionForward = 0, ConfigLedCount = 0x02;
    const int MaxBlock = 3;
    const byte DefaultAddress = 0x77;
    // OpenRGB ene_ram_addresses: 0x70..0x77 plus the alternates 0x4F, 0x66, 0x67, 0x39..0x3D. The 0x77 default is only a remap source.
    static readonly byte[] ScanAddresses = { 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D };

    readonly SmbusI801 _bus;
    public List<EneDimm> Dimms { get; } = new();
    IReadOnlyList<EneDimm> IEneModules.Modules => Dimms;
    public List<string> Journal { get; } = new();

    public EneDramController(SmbusI801 bus) { _bus = bus; }

    void Note(string s) { Journal.Add(s); Log.Info("[ENE DRAM] " + s); }

    // ---- ENE register access (OpenRGB ENESMBusInterface_i2c_smbus) ------------------------------------------

    static ushort Swap(ushort reg) => (ushort)(((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF));

    bool SelectRegister(byte addr, ushort reg) => _bus.WriteWordData(addr, 0x00, Swap(reg));

    bool RegRead(byte addr, ushort reg, out byte value)
    {
        value = 0;
        return SelectRegister(addr, reg) && _bus.ReadByteData(addr, 0x81, out value);
    }

    bool RegWrite(byte addr, ushort reg, byte value) =>
        SelectRegister(addr, reg) && _bus.WriteByteData(addr, 0x01, value);

    bool RegWriteBlock(byte addr, ushort reg, ReadOnlySpan<byte> data)
    {
        if (!SelectRegister(addr, reg)) return false;
        if (_bus.WriteBlockData(addr, 0x03, data)) return true;
        foreach (var b in data) // reference implementation falls back to single-byte writes
            if (!_bus.WriteByteData(addr, 0x01, b)) return false;
        return true;
    }

    // ---- detection ---------------------------------------------------------------------------------------------

    /// <summary>Read-only signature test. Returns true when the device at <paramref name="addr"/> behaves like an ENE controller.</summary>
    bool LooksLikeEne(byte addr)
    {
        if (!_bus.ReadByte(addr, out _) && !_bus.ReadByteData(addr, 0x00, out _)) return false;
        for (int i = 0xA0; i < 0xB0; i++)
        {
            if (!_bus.ReadByteData(addr, (byte)i, out var v) || v != i - 0xA0) return false;
        }
        var sb = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            if (!RegRead(addr, (ushort)(RegMicronCheck + i), out var c)) return false;
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString() != "Micron";
    }

    /// <summary>
    /// Finds ENE controllers. If controllers still sit at the default 0x77 (fresh boot), they are moved to free
    /// addresses 0x70..0x76 using the slot-index / address registers, exactly like the reference implementation.
    /// </summary>
    public void Detect(bool allowRemap)
    {
        Dimms.Clear();
        using var busLock = _bus.Lock();

        if (allowRemap && LooksLikeEne(DefaultAddress))
        {
            Note("ENE controller answering at default 0x77 - remapping slots to 0x70..0x76");
            for (int slot = 0; slot < 8; slot++)
            {
                if (!_bus.ReadByte(DefaultAddress, out _)) { Note($"slot {slot}: nothing left at 0x77, remap complete"); break; }
                if (!LooksLikeEne(DefaultAddress)) { Note($"slot {slot}: 0x77 no longer looks like ENE, stopping remap"); break; }

                byte target = 0;
                foreach (var a in ScanAddresses)
                {
                    if (a == DefaultAddress) continue;
                    if (!_bus.ReadByte(a, out _)) { target = a; break; } // NACK = free
                }
                if (target == 0) { Note("no free address left for remap"); break; }

                Note($"slot {slot} -> 0x{target:X2}");
                bool ok = RegWrite(DefaultAddress, RegSlotIndex, (byte)slot) &&
                          RegWrite(DefaultAddress, RegI2cAddress, (byte)(target << 1));
                if (!ok) { Note($"remap write failed for slot {slot}"); break; }
                Thread.Sleep(5);
            }
        }

        foreach (var addr in ScanAddresses)
        {
            if (!_bus.ReadByte(addr, out _)) continue;
            if (!LooksLikeEne(addr)) { Note($"0x{addr:X2} answers but is not an ENE controller - left alone"); continue; }
            var d = new EneDimm { Address = addr };
            ReadIdentity(d);
            Dimms.Add(d);
            Note($"found {d}");
        }
        if (Dimms.Count == 0) Note("no ENE DRAM controllers found (scanned 0x70-0x77, 0x4F, 0x66, 0x67, 0x39-0x3D)");
    }

    void ReadIdentity(EneDimm d)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 15; i++)
        {
            if (!RegRead(d.Address, (ushort)(RegDeviceName + i), out var c) || c == 0) break;
            sb.Append((char)c);
        }
        d.Version = sb.ToString();
        RegRead(d.Address, (ushort)(RegConfigTable + ConfigLedCount), out var leds);
        d.LedCount = leds;

        switch (d.Version)
        {
            case "DIMM_LED-0102":
            case "DIMM_LED-0103":
                d.DirectReg = RegColorsDirect; d.EffectReg = RegColorsEffect; d.Supported = true; break;
            case "AUDA0-E6K5-0101":
                d.DirectReg = RegColorsDirectV2; d.EffectReg = RegColorsEffectV2; d.Supported = true; break;
            default:
                d.Supported = false; break; // unknown variant: never write
        }
        if (d.LedCount < 1 || d.LedCount > 30) d.Supported = false;
    }

    // ---- lighting ------------------------------------------------------------------------------------------------

    public void EnterDirect(EneDimm d)
    {
        if (!d.Supported) return;
        using var busLock = _bus.Lock();
        RegWrite(d.Address, RegDirect, 1);
        RegWrite(d.Address, RegApply, ApplyValue);
        d.LastFrame = null;
        if (d.WholeBlockOk == true) d.WholeBlockOk = null;   // re-confirm once per activation; a failure stays a failure
    }

    /// <summary>Smallest per-channel change worth sending; smaller ones are not visible on these LEDs.</summary>
    public const int PerceptualDelta = 2;

    /// <summary>Module byte order: ENE expects R, B, G per LED.</summary>
    internal static byte[] Pack(ReadOnlySpan<Rgb> leds)
    {
        var buf = new byte[leds.Length * 3];
        for (int i = 0; i < leds.Length; i++)
        {
            buf[i * 3 + 0] = leds[i].R;
            buf[i * 3 + 1] = leds[i].B;
            buf[i * 3 + 2] = leds[i].G;
        }
        return buf;
    }

    /// <summary>
    /// True when some channel moved by at least <see cref="PerceptualDelta"/> since the last frame actually written.
    /// Comparing with the last written frame (not the last offered one) means slow fades still go out once their drift adds up.
    /// </summary>
    internal static bool NeedsWrite(byte[]? lastWritten, byte[] next)
    {
        if (lastWritten == null || lastWritten.Length != next.Length) return true;
        for (int i = 0; i < next.Length; i++)
        {
            if (Math.Abs(next[i] - lastWritten[i]) >= PerceptualDelta) return true;
            // a channel going to 0 is an explicit "off" (Stealth, Lights out): never let the perceptual skip leave it dimly lit
            if (next[i] == 0 && lastWritten[i] != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Push one frame. A module whose LEDs all moved by less than <see cref="PerceptualDelta"/> since the last write is
    /// skipped (no bus traffic). Otherwise the whole module goes out as one register select plus one block write
    /// (8 transactions per frame for the four modules instead of 2 per LED); the probe and the first write after each
    /// activation are read back to confirm, and a module that fails falls back to 3-byte blocks per changed LED.
    /// Returns the number of LEDs written, or -1 if a bus write failed (the module is then fully re-sent next time).
    /// </summary>
    public int WriteFrame(EneDimm d, ReadOnlySpan<Rgb> leds)
    {
        if (!d.Supported) return 0;
        int n = Math.Min(d.LedCount, leds.Length);
        var buf = Pack(leds[..n]);
        if (!NeedsWrite(d.LastFrame, buf)) { d.ModuleSkips++; return 0; }

        using var busLock = _bus.Lock();
        if (d.WholeBlockOk != false && buf.Length <= 32)
        {
            if (!SelectRegister(d.Address, d.DirectReg) || !_bus.WriteBlockData(d.Address, 0x03, buf))
            {
                d.LastFrame = null;
                return -1;
            }
            if (d.WholeBlockOk == null)
            {
                // first whole-module write since activation: read it back once before relying on it
                bool same = true;
                for (int i = 0; i < buf.Length && same; i++)
                    same = RegRead(d.Address, (ushort)(d.DirectReg + i), out var v) && v == buf[i];
                d.WholeBlockOk = same;
                Note($"0x{d.Address:X2}: whole-module block write {(same ? "verified by read-back" : "did NOT read back correctly; using 3-byte blocks")}");
                if (!same) { d.LastFrame = null; return WriteFrame3(d, buf); }
            }
            d.LastFrame = buf;
            d.ModuleWrites++;
            return n;
        }
        return WriteFrame3(d, buf);
    }

    /// <summary>Previous method: one register select plus a 3-byte block per changed LED (caller holds the bus lock).</summary>
    int WriteFrame3(EneDimm d, byte[] buf)
    {
        int sent = 0, n = buf.Length / 3;
        for (int led = 0; led < n; led++)
        {
            bool changed = d.LastFrame == null || d.LastFrame.Length != buf.Length ||
                           buf[led * 3] != d.LastFrame[led * 3] ||
                           buf[led * 3 + 1] != d.LastFrame[led * 3 + 1] ||
                           buf[led * 3 + 2] != d.LastFrame[led * 3 + 2];
            if (!changed) continue;
            if (!RegWriteBlock(d.Address, (ushort)(d.DirectReg + 3 * led), buf.AsSpan(led * 3, 3)))
            {
                d.LastFrame = null;   // unknown what the module shows now: resend everything next time
                return -1;
            }
            sent++;
        }
        d.LastFrame = buf;
        d.ModuleWrites++;
        return sent;
    }

    /// <summary>ENE onboard effect modes used by HwSuite (OpenRGB ENESMBusController.h: ENE_MODE_*).</summary>
    public const byte ModeOff = 0, ModeBreathing = 2, ModeSpectrumCycleWave = 11;
    /// <summary>ENE speed register: 0 = fastest ... 4 = slowest (OpenRGB ENE_SPEED_*).</summary>
    public const byte SpeedFastest = 0, SpeedSlowest = 4;

    /// <summary>HwSuite speed 0..1 (slow..fast) to the ENE speed register (4 = slowest .. 0 = fastest).</summary>
    public static byte SpeedFor(double speed01) => (byte)Math.Clamp((int)Math.Round((1 - Math.Clamp(speed01, 0, 1)) * SpeedSlowest), SpeedFastest, SpeedSlowest);

    /// <summary>
    /// Run one of the controller's own effects: leave direct mode, write the effect colour to every LED, then mode, speed
    /// and direction, each followed by the volatile apply (0x01; the flash save value 0xAA is never used). The module then
    /// animates by itself with no further SMBus traffic. The mode, speed and direct registers are read back to confirm.
    /// </summary>
    public EneOutcome SetHardwareEffect(EneDimm d, byte mode, Rgb color, byte speed, out string detail)
    {
        detail = "";
        if (!d.Supported) { detail = "unsupported module"; return EneOutcome.Failed; }
        using var busLock = _bus.Lock();
        bool ok = RegWrite(d.Address, RegDirect, 0) && RegWrite(d.Address, RegApply, ApplyValue);
        var buf = new byte[d.LedCount * 3];
        for (int i = 0; i < d.LedCount; i++) { buf[i * 3] = color.R; buf[i * 3 + 1] = color.B; buf[i * 3 + 2] = color.G; }
        for (int sent = 0; ok && sent < buf.Length; )
        {
            int chunk = Math.Min(MaxBlock, buf.Length - sent);
            ok = RegWriteBlock(d.Address, (ushort)(d.EffectReg + sent), buf.AsSpan(sent, chunk));
            sent += chunk;
        }
        ok = ok && RegWrite(d.Address, RegApply, ApplyValue)
                && RegWrite(d.Address, RegMode, mode)
                && RegWrite(d.Address, RegSpeed, speed)
                && RegWrite(d.Address, RegDirection, DirectionForward)
                && RegWrite(d.Address, RegApply, ApplyValue);
        d.LastFrame = null;
        if (!ok) { detail = "SMBus write failed"; return EneOutcome.Failed; }
        bool readBack = RegRead(d.Address, RegDirect, out var dir) & RegRead(d.Address, RegMode, out var m) & RegRead(d.Address, RegSpeed, out var sp);
        if (!readBack) { detail = "written, but the read-back failed (unconfirmed)"; return EneOutcome.Unconfirmed; }
        bool match = dir == 0 && m == mode && sp == speed;
        detail = match ? $"mode {mode} speed {speed} confirmed by read-back" : $"read back direct {dir} mode {m} speed {sp} (wrote 0/{mode}/{speed})";
        return match ? EneOutcome.Confirmed : EneOutcome.Failed;
    }

    /// <summary>Read-only check: the module's direct-colour registers (R, B, G per LED) and its direct/mode registers.</summary>
    public (byte[] direct, byte directFlag, byte mode)? ReadBack(EneDimm d)
    {
        if (!d.Supported) return null;
        using var busLock = _bus.Lock();
        var buf = new byte[d.LedCount * 3];
        for (int i = 0; i < buf.Length; i++) if (!RegRead(d.Address, (ushort)(d.DirectReg + i), out buf[i])) return null;
        if (!RegRead(d.Address, RegDirect, out var df) || !RegRead(d.Address, RegMode, out var m)) return null;
        return (buf, df, m);
    }

    /// <summary>Leave direct mode and show a steady colour using the controller's own static effect (volatile, not saved to flash).</summary>
    public void SetStaticLook(EneDimm d, Rgb color)
    {
        if (!d.Supported) return;
        using var busLock = _bus.Lock();
        RegWrite(d.Address, RegDirect, 0);
        RegWrite(d.Address, RegApply, ApplyValue);
        var buf = new byte[d.LedCount * 3];
        for (int i = 0; i < d.LedCount; i++) { buf[i * 3] = color.R; buf[i * 3 + 1] = color.B; buf[i * 3 + 2] = color.G; }
        int sent = 0;
        while (sent < buf.Length)
        {
            int chunk = Math.Min(MaxBlock, buf.Length - sent);
            if (!RegWriteBlock(d.Address, (ushort)(d.EffectReg + sent), buf.AsSpan(sent, chunk))) break;
            sent += chunk;
        }
        RegWrite(d.Address, RegApply, ApplyValue);
        RegWrite(d.Address, RegMode, ModeStatic);
        RegWrite(d.Address, RegSpeed, SpeedNormal);
        RegWrite(d.Address, RegDirection, DirectionForward);
        RegWrite(d.Address, RegApply, ApplyValue);
        d.LastFrame = null;
    }

    /// <summary>
    /// Probe (Phase 8): enter direct mode, write a dim test pattern to the direct colour registers in blocks of
    /// <paramref name="chunk"/> bytes (one register select per block, never past 3 x LED count), then read every byte
    /// back. <paramref name="reselect"/> = false selects only once and relies on the register pointer advancing after a block.
    /// Returns whether every byte read back as written.
    /// </summary>
    public bool ProbeBlockWrite(EneDimm d, int chunk, bool reselect, byte seed, out string detail)
    {
        detail = "";
        if (!d.Supported) { detail = "unsupported module"; return false; }
        int n = d.LedCount * 3;
        var pattern = new byte[n];
        for (int i = 0; i < n; i++) pattern[i] = (byte)((seed + i * 5) & 0x3F);   // dim: every value <= 63
        using var busLock = _bus.Lock();
        RegWrite(d.Address, RegDirect, 1);
        RegWrite(d.Address, RegApply, ApplyValue);
        int writes = 0;
        if (!reselect && !SelectRegister(d.Address, d.DirectReg)) { detail = "select failed"; return false; }
        for (int off = 0; off < n; off += chunk)
        {
            int c = Math.Min(chunk, n - off);
            if (reselect && !SelectRegister(d.Address, (ushort)(d.DirectReg + off))) { detail = $"select failed at +{off}"; return false; }
            if (!_bus.WriteBlockData(d.Address, 0x03, pattern.AsSpan(off, c))) { detail = $"block write of {c} bytes failed at +{off}"; return false; }
            writes++;
        }
        int bad = 0; var first = "";
        for (int i = 0; i < n; i++)
        {
            if (!RegRead(d.Address, (ushort)(d.DirectReg + i), out var v)) { detail = $"read-back failed at +{i}"; return false; }
            if (v != pattern[i]) { if (bad++ == 0) first = $"+{i}: wrote 0x{pattern[i]:X2}, read 0x{v:X2}"; }
        }
        d.LastFrame = null;
        detail = bad == 0 ? $"{writes} block write(s), all {n} bytes read back correctly" : $"{bad} of {n} bytes differ (first {first})";
        return bad == 0;
    }

    public void Dispose() { /* bus is owned by the caller */ }
}

