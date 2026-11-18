using HidSharp;
using HwSuite.Core;
using HwSuite.Lighting.Devices;

namespace HwSuite.Hardware.Al80;

public sealed class Al80Exception : Exception { public Al80Exception(string m, Exception? inner = null) : base(m, inner) { } }

/// <summary>Raw transport (real HID or a test stand-in): writes one 64-byte report, reads one echo or returns null on timeout.</summary>
public interface IAl80Transport : IDisposable
{
    string Name { get; }
    void Write(byte[] report);
    byte[]? Read(int timeoutMs);
    /// <summary>USB bcdDevice (firmware revision): 0x0125 stock on the owner's AL80, 0x0122 YUNZII's SignalRGB build, 0x0119 the 10-min build. Null if unknown.</summary>
    int? Revision => null;
}

/// <summary>The AL80's raw-HID interface (usage page 0xFF60, usage 0x61) through HidSharp.</summary>
public sealed class Al80HidTransport : IAl80Transport
{
    readonly HidStream _stream;
    readonly int _outLen;
    readonly byte[] _in;
    public string Name { get; }
    public int? Revision { get; }

    Al80HidTransport(HidStream s, int outLen, int inLen, string name, int? revision) { _stream = s; _outLen = outLen; _in = new byte[Math.Max(65, inLen)]; Name = name; Revision = revision; }

    static int? RevisionOf(HidInfo info) { try { return info.Device.ReleaseNumberBcd; } catch { return null; } }

    public static HidInfo? Find() =>
        HidEnumerator.Find(Al80Protocol.Vid, Al80Protocol.Pid).FirstOrDefault(i => i.HasUsage(Al80Protocol.RawUsagePage, Al80Protocol.RawUsage));

    public static Al80HidTransport Open()
    {
        RunMode.Assert("open the AL80 keyboard");
        var info = Find() ?? throw new Al80Exception("AL80 not found on USB (it must be wired; the screen interface is 0xFF60/0x61)");
        if (!info.Device.TryOpen(out var stream)) throw new Al80Exception("could not open the AL80 screen interface; VIA or the YUNZII web app may hold it");
        stream.WriteTimeout = 500;
        var rev = RevisionOf(info);
        return new Al80HidTransport(stream, Math.Max(65, info.MaxOutput), info.MaxInput, $"{info.Product} ({info.Vid:X4}:{info.Pid:X4}{(rev is int r ? $" rev {r:X4}" : "")})", rev);
    }

    public void Write(byte[] report)
    {
        var buf = new byte[_outLen];          // [0] = report ID 0
        Array.Copy(report, 0, buf, 1, Math.Min(report.Length, _outLen - 1));
        _stream.Write(buf);
    }

    public byte[]? Read(int timeoutMs)
    {
        _stream.ReadTimeout = Math.Max(1, timeoutMs);
        try
        {
            int n = _stream.Read(_in, 0, _in.Length);
            if (n <= 1) return null;
            return _in.AsSpan(1, Math.Min(Al80Protocol.ReportSize, n - 1)).ToArray();   // drop the report ID
        }
        catch (TimeoutException) { return null; }
    }

    public void Dispose() { try { _stream.Dispose(); } catch { } }
}

/// <summary>
/// Sends screen streams with ACK gating and up to <see cref="Window"/> data reports in flight, and VIA requests.
/// One lock: screen and VIA traffic never interleave. Measured on 2026-11-12 (owner's AL80, wired):
/// one report at a time 3.45 s per full picture; 4 in flight 1.48 s; 8 or 32 in flight 1.30 s (the module's serial
/// link is the limit); 100 ms announce settle 1.10 s. Every variant arrived clean.
/// </summary>
public sealed class Al80Device : IDisposable
{
    readonly IAl80Transport _t;
    readonly object _lock = new();
    public int Window { get; set; } = 8;
    public string Name => _t.Name;
    public int Retries { get; private set; }

    public Al80Device(IAl80Transport t) { _t = t; }

    public static Al80Device OpenUsb() => new(Al80HidTransport.Open());

    /// <summary>Echoes that arrived while an earlier in-flight report was awaited (kept, so a later wait finds them).</summary>
    readonly List<byte[]> _early = new();

    void Drain() { _early.Clear(); while (_t.Read(1) != null) { } }

    static bool Echoes(byte[] e, byte[] r) => e.Length >= 7 && e[0] == r[0] && e[1] == r[1] && e[2] == r[2];

    byte[]? NextEcho(byte[] r, int timeoutMs)
    {
        int i = _early.FindIndex(e => Echoes(e, r));
        if (i >= 0) { var e = _early[i]; _early.RemoveAt(i); return e; }
        var got = _t.Read(timeoutMs);
        if (got != null && !Echoes(got, r) && got[0] is >= 0x40 and <= 0x42 && _early.Count < 64) _early.Add(got);
        return got;
    }

    /// <summary>Wait for the ACK of <paramref name="r"/>; resend on busy or silence. Data reports carry their offset, so a resend is harmless.</summary>
    void AwaitAck(byte[] r, bool alreadySent)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (!alreadySent || attempt > 0) { if (attempt > 0) Retries++; _t.Write(r); }
            var deadline = Environment.TickCount64 + 500;
            while (Environment.TickCount64 < deadline)
            {
                var e = NextEcho(r, 50);
                if (e == null || !Echoes(e, r)) continue;
                if (e[6] == Al80Protocol.AckOk) return;
                if (e[6] == Al80Protocol.AckBusy) { Thread.Sleep(2); break; }
            }
        }
        throw new Al80Exception("the keyboard stopped acknowledging screen reports");
    }

    /// <summary>
    /// Optional hook run between screen packets when a caller passes allowBetween. NOT used by VANTA: on 2026-11-12 the
    /// first live picture sent with VIA lighting writes interleaved ended with the keyboard dropping off USB and the screen
    /// left blank until a power cut. Lighting is written only between whole screen transfers.
    /// </summary>
    public Action<Al80Device>? Between { get; set; }
    bool _inBetween;

    void RunBetween()
    {
        if (Between == null || _inBetween) return;
        _inBetween = true;
        try { Between(this); } finally { _inBetween = false; }
    }

    public void Send(IReadOnlyList<Al80Step> steps, Action<int, int>? progress = null, CancellationToken cancel = default, bool allowBetween = false)
    {
        lock (_lock)
        {
            try
            {
                Drain();
                var inflight = new Queue<byte[]>();
                int sinceBetween = 0;
                for (int i = 0; i < steps.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var s = steps[i];
                    if (s.Report[0] is not (Al80Protocol.OpAnnounce or Al80Protocol.OpData or Al80Protocol.OpFinish))
                        throw new Al80Exception($"refused to send opcode 0x{s.Report[0]:X2}");
                    bool pipelined = s.Report[0] == Al80Protocol.OpData && s.Pause == 0 && !IsCommand(s.Report) && Window > 1;
                    if (!pipelined)
                    {
                        while (inflight.Count > 0) AwaitAck(inflight.Dequeue(), true);
                        _t.Write(s.Report);
                        AwaitAck(s.Report, true);
                    }
                    else
                    {
                        _t.Write(s.Report);
                        inflight.Enqueue(s.Report);
                        if (inflight.Count >= Window) AwaitAck(inflight.Dequeue(), true);
                        if (allowBetween && Between != null && ++sinceBetween >= 32)
                        {
                            sinceBetween = 0;
                            while (inflight.Count > 0) AwaitAck(inflight.Dequeue(), true);
                            RunBetween();
                        }
                    }
                    if (s.Pause > 0)
                    {
                        while (inflight.Count > 0) AwaitAck(inflight.Dequeue(), true);
                        // short module settles (status packets, picture setup) let lighting writes through; GIF flash pauses do not
                        if (allowBetween && Between != null && s.Pause <= 0.35) SleepWithBetween(s.Pause);
                        else PreciseSleep(s.Pause);
                    }
                    if (progress != null && (i % 64 == 0 || i == steps.Count - 1)) progress(i + 1, steps.Count);
                }
                while (inflight.Count > 0) AwaitAck(inflight.Dequeue(), true);
            }
            catch (Al80Exception) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new Al80Exception("lost the keyboard: " + ex.Message, ex); }
        }
    }

    /// <summary>Setup/header packets (A5 5A ...) are sent one at a time; only raw pixel blocks are pipelined.</summary>
    static bool IsCommand(byte[] r) => r[7] == 0xA5 && r[8] == 0x5A && r[3] <= 10;

    void SleepWithBetween(double seconds)
    {
        var end = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(seconds * System.Diagnostics.Stopwatch.Frequency);
        RunBetween();
        while (true)
        {
            double left = (end - System.Diagnostics.Stopwatch.GetTimestamp()) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (left <= 0) return;
            PreciseSleep(Math.Min(left, 0.012));
            RunBetween();
        }
    }

    static void PreciseSleep(double seconds)
    {
        var end = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(seconds * System.Diagnostics.Stopwatch.Frequency);
        int ms = (int)(seconds * 1000) - 2;
        if (ms > 0) Thread.Sleep(ms);
        while (System.Diagnostics.Stopwatch.GetTimestamp() < end) Thread.SpinWait(50);
    }

    // ------------------------------------------------------------------ SignalRGB (rev 0x0122 only)

    public int? Revision => _t.Revision;
    /// <summary>The firmware is YUNZII's SignalRGB build (by USB revision), so 0x21-0x28 reach the SignalRGB handler.</summary>
    public bool SrgbFirmware => Al80Protocol.IsSrgbRevision(_t.Revision);
    /// <summary>0x25 was sent and 0x26 not yet: the keys show streamed colours, and VIA lighting writes are refused.</summary>
    public bool SrgbActive { get; private set; }
    public long SrgbPackets { get; private set; }

    void RequireSrgb(byte cmd)
    {
        if (!SrgbFirmware) throw new InvalidOperationException($"refused SignalRGB command 0x{cmd:X2}: the keyboard reports rev {(_t.Revision is int r ? r.ToString("X4") : "unknown")}, not one verified to have the SignalRGB handler (on rev 0119 and unknown builds 0x21-0x28 reach the vendor handler)");
    }

    /// <summary>A SignalRGB query (0x21, 0x22, 0x23, 0x27, 0x28): the 64-byte reply, or null when none came.</summary>
    public byte[]? SrgbQuery(byte cmd, int timeoutMs = 300)
    {
        if (cmd is not (Al80Protocol.SrgbQmkVersion or Al80Protocol.SrgbProtocol or Al80Protocol.SrgbUniqueId or Al80Protocol.SrgbTotalLeds or Al80Protocol.SrgbFirmwareType))
            throw new ArgumentException($"0x{cmd:X2} is not a SignalRGB query");
        RequireSrgb(cmd);
        lock (_lock)
        {
            try
            {
                Drain();
                _t.Write(Al80Protocol.Srgb(cmd));
                var deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    var e = _t.Read(50);
                    if (e != null && e[0] == cmd) return e;
                }
                return null;
            }
            catch (Exception ex) when (ex is not Al80Exception) { throw new Al80Exception("lost the keyboard: " + ex.Message, ex); }
        }
    }

    /// <summary>0x25 (enable: streamed colours from now on) or 0x26 (disable: the keyboard's stored effect back). No reply; no EEPROM write.</summary>
    public void SrgbMode(bool enable)
    {
        byte cmd = enable ? Al80Protocol.SrgbEnable : Al80Protocol.SrgbDisable;
        RequireSrgb(cmd);
        lock (_lock)
        {
            try { _t.Write(Al80Protocol.Srgb(cmd)); SrgbActive = enable; }
            catch (Exception ex) { throw new Al80Exception("lost the keyboard: " + ex.Message, ex); }
        }
    }

    /// <summary>
    /// Stream packets for one frame (built by <see cref="Al80Protocol.SrgbFrame"/>), written back to back under the lock so
    /// they never fall inside a screen transfer. Success has no reply; an error reply (byte 1 = 0xFD/0xFE) is reported.
    /// </summary>
    public string? SrgbWrite(IReadOnlyList<byte[]> packets)
    {
        if (packets.Count == 0) return null;
        RequireSrgb(Al80Protocol.SrgbStream);
        lock (_lock)
        {
            try
            {
                foreach (var p in packets)
                {
                    if (p[0] != Al80Protocol.SrgbStream || p[2] is 0 or > Al80Protocol.SrgbMaxPerPacket) throw new Al80Exception($"refused malformed stream packet {p[0]:X2} {p[1]:X2} {p[2]:X2}");
                    _t.Write(p);
                    SrgbPackets++;
                }
                // replies only on error; collect any without waiting
                string? err = null;
                while (_t.Read(0) is { } e)
                    if (e[1] is Al80Protocol.SrgbErrBounds or Al80Protocol.SrgbErrCount) err = e[1] == Al80Protocol.SrgbErrBounds ? "LED index out of range" : "too many LEDs in one packet";
                return err;
            }
            catch (Al80Exception) { throw; }
            catch (Exception ex) { throw new Al80Exception("lost the keyboard: " + ex.Message, ex); }
        }
    }

    /// <summary>One VIA request (built by <see cref="Al80Protocol.Via"/>); returns the echoed response or throws.</summary>
    public byte[] Via(byte[] request)
    {
        if (!Al80Protocol.ViaAllowed.Contains(request[0])) throw new Al80Exception($"refused VIA command 0x{request[0]:X2}");
        // the plan's rule: no VIA lighting writes while SignalRGB mode is on (not an Al80Exception: the link is fine)
        // 1.19: except volatile 0x07 on channel 2 (the separate 3-LED rgblight): Ghidra shows those touch rgblight only
        if (SrgbActive && (request[0] == 0x09 || (request[0] == 0x07 && request[1] != Al80Protocol.ChannelSide)))
            throw new InvalidOperationException("VIA lighting writes to the keys (and Save) are refused while VANTA streams per-key colours (SignalRGB mode)");
        lock (_lock)
        {
            try
            {
                Drain();
                _t.Write(request);
                var deadline = Environment.TickCount64 + 600;
                while (Environment.TickCount64 < deadline)
                {
                    var e = _t.Read(100);
                    if (e == null || e[0] != request[0]) continue;
                    // value reads echo their selector bytes too
                    if (request[0] is 0x02 or 0x08 or 0x07 or 0x04 or 0x05 && (e[1] != request[1] || (request[0] is 0x07 or 0x08 && e[2] != request[2]))) continue;
                    if (e[0] == 0xFF) throw new Al80Exception("the keyboard did not understand the VIA request");
                    return e;
                }
            }
            catch (Al80Exception) { throw; }
            catch (Exception ex) { throw new Al80Exception("lost the keyboard: " + ex.Message, ex); }
        }
        throw new Al80Exception($"no VIA reply to 0x{request[0]:X2}");
    }

    public void Dispose() => _t.Dispose();
}

/// <summary>Test stand-in: acknowledges every report, assembles what a real module would store, and records VIA traffic.</summary>
public sealed class FakeAl80Transport : IAl80Transport
{
    public string Name => "simulated AL80";
    public readonly List<byte[]> Sent = new();
    readonly Queue<byte[]> _replies = new();
    public byte[]? Picture;
    public readonly Dictionary<int, List<byte[]>> Gifs = new();
    public readonly Dictionary<(byte ch, byte val), byte[]> Lighting = new();
    public int BusyEvery;          // reply "busy" to every Nth data report once
    public int MaxInFlight, _inFlight;
    (byte kind, int declared, byte[] buf)? _pending;
    int _gifMode, _bankBase, _received, _count;

    public List<byte[]> Snapshot() { lock (Sent) return Sent.ToList(); }

    /// <summary>bcdDevice the fake reports: 0x0119 = older build without the SignalRGB handler (default), 0x0122 / 0x0125 = with it.</summary>
    public int? Revision { get; set; } = 0x0119;
    /// <summary>SignalRGB state as the firmware keeps it (only used when Revision is the SignalRGB build).</summary>
    public readonly global::HwSuite.Lighting.Rgb[] SrgbLeds = new global::HwSuite.Lighting.Rgb[global::HwSuite.Lighting.Devices.Al80KeyMap.Leds];
    public bool SrgbOn;
    public int SrgbStreamPackets, SrgbErrors;
    /// <summary>SignalRGB IDs that reached a stock-revision fake (must stay 0: they mean something else there).</summary>
    public int SrgbOnStock;
    readonly byte[] _srgbPacket = new byte[64];

    void Srgb(byte[] r)
    {
        if (!Al80Protocol.IsSrgbRevision(Revision)) { SrgbOnStock++; return; }
        void Reply() => _replies.Enqueue((byte[])_srgbPacket.Clone());
        switch (r[0])
        {
            case 0x21: _srgbPacket[0] = 0x21; _srgbPacket[1] = 0; _srgbPacket[2] = 0x1D; _srgbPacket[3] = 4; Reply(); break;
            case 0x22: _srgbPacket[0] = 0x22; _srgbPacket[1] = 1; _srgbPacket[2] = 0; _srgbPacket[3] = 6; Reply(); break;
            case 0x23: _srgbPacket[0] = 0x23; _srgbPacket[1] = 0; _srgbPacket[2] = 0; _srgbPacket[3] = 0; Reply(); break;
            case 0x27: _srgbPacket[0] = 0x27; _srgbPacket[1] = 0x54; Reply(); break;
            case 0x28: _srgbPacket[0] = 0x28; _srgbPacket[1] = 2; Reply(); break;
            case 0x25: SrgbOn = true; break;
            case 0x26: SrgbOn = false; break;
            case 0x24:
                int start = r[1], n = r[2];
                if (start + n > 0x54) { _srgbPacket[1] = 0xFD; SrgbErrors++; Reply(); break; }
                if (n > 9) { _srgbPacket[1] = 0xFE; SrgbErrors++; Reply(); break; }
                SrgbStreamPackets++;
                for (int i = 0; i < n; i++) SrgbLeds[start + i] = new global::HwSuite.Lighting.Rgb(r[3 + 3 * i], r[4 + 3 * i], r[5 + 3 * i]);
                break;
        }
    }

    public void Write(byte[] r)
    {
        lock (Sent) Sent.Add((byte[])r.Clone());
        if (r[0] is >= 0x21 and <= 0x28) { Srgb(r); return; }
        if (r[0] is 0x40 or 0x41 or 0x42)
        {
            int sum = 0;
            for (int i = 0; i < 64; i++) if (i is not (4 or 5)) sum += r[i];
            if ((sum & 0xFFFF) != (r[4] | r[5] << 8)) throw new InvalidOperationException("bad checksum");
            _count++;
            var echo = (byte[])r.Clone();
            if (BusyEvery > 0 && r[0] == 0x41 && _count % BusyEvery == 0) { echo[6] = Al80Protocol.AckBusy; _replies.Enqueue(echo); return; }
            Screen(r);
            echo[6] = Al80Protocol.AckOk;
            _replies.Enqueue(echo);
            MaxInFlight = Math.Max(MaxInFlight, _replies.Count);
            return;
        }
        var reply = (byte[])r.Clone();
        if (r[0] == 0x07) Lighting[(r[1], r[2])] = r.AsSpan(3, 2).ToArray();
        if (r[0] == 0x08 && Lighting.TryGetValue((r[1], r[2]), out var v)) v.CopyTo(reply, 3);
        if (r[0] == 0x11) reply[1] = 4;
        _replies.Enqueue(reply);
    }

    void Screen(byte[] r)
    {
        if (r[0] == 0x42) return;
        int off = r[1] | r[2] << 8, len = r[3];
        var payload = r.AsSpan(7, len);
        if (_pending is { } p)
        {
            if (p.kind == Al80Protocol.PkFrameLen) { if (off == 0 && _received > 0) _bankBase += Al80Protocol.Bank; off += _bankBase; }
            payload.CopyTo(p.buf.AsSpan(off));
            _received += len;
            if (_received < p.declared) return;
            _pending = null;
            if (p.kind == Al80Protocol.PkAddPic) Picture = p.buf;
            else if (p.kind == Al80Protocol.PkFrameLen) Gifs[_gifMode].Add(p.buf);
            return;
        }
        if (len < 7 || payload[0] != 0xA5 || payload[1] != 0x5A) return;
        byte kind = payload[2]; int declared = payload[3] << 8 | payload[4];
        var data = payload.Slice(7);
        if (kind is Al80Protocol.PkAddPic or Al80Protocol.PkFrameLen || (kind is Al80Protocol.PkTime or Al80Protocol.PkDate && data.Length == 0))
        { _pending = (kind, declared, new byte[declared]); _bankBase = 0; _received = 0; }
        else if (kind == Al80Protocol.PkEvent && data.Length >= 2 && data[0] == 2) _gifMode = data[1];
        else if (kind == Al80Protocol.PkGifNum && data.Length >= 2 && data[1] == 0) Gifs[data[0]] = new();
    }

    public byte[]? Read(int timeoutMs) => _replies.Count > 0 ? _replies.Dequeue() : null;
    public void Dispose() { }
}
