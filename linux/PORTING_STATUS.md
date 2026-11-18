# VANTA on Fedora Linux — porting status

Living document: what is done, what is in progress, what still needs doing. Newest notes at the top of the log.
Target: **this PC, dual-boot Fedora** (B660M DS3H DDR4, i7-12700K, IT8689E Super I/O, RTX 3070 FE, XPG D60G RAM with
ENE RGB, IT5701 USB RGB, YUNZII AL80, Glorious Model O Wireless). Windows VANTA 1.20.0 stays exactly as it is.

**Where it stands (2026-11-13):** the engine (fans, lighting, AL80, failsafes) builds for Linux and passes 762 of 769
shared self-tests run from Windows (the 7 misses are sub-millisecond timer checks that can only pass on Linux itself).
An installable package exists: `linux\dist\vanta-linux-1.20.0-linux.0.tar.gz`. It has **not run on Fedora yet** and has
**no window yet** (the Avalonia UI is the next big piece). Nothing has touched real hardware from Linux.

## How the port is built
- Everything lives in `linux\`. The Windows source (`src\HwSuite`) is **not changed**: the Linux project links the
  platform-neutral files from it (effects, scenes, fan engine and safety, ownership rules, save format, device protocols,
  the self-tests), so both versions behave the same and read the same `config.json`.
- `src\Vanta.Linux\Platform\` = new Linux code. `src\Vanta.Linux\Ported\` = 19 Windows files copied and changed where the
  platform differs; each starts with `// PORTED from src\...` saying what changed.
- **Drift check:** `bash linux/check-drift.sh` says whether any of those 19 Windows originals changed since they were
  ported (hashes in `ported-sources.sha256`). After a Windows release, run it and re-port what it lists, then
  `check-drift.sh --record`.
- Build + package from Windows: `bash linux/build-dist.sh` (uses `tools\dotnet`; self-contained linux-x64, so Fedora
  needs no .NET). Quick build: `tools\dotnet\dotnet.exe build linux\src\Vanta.Linux`.
- Unit checks (from Windows or Linux, opens no device): `dotnet vanta.dll --selftest --no-hardware` in the build output.

### Architecture on Linux (decided)
| Piece | Windows | Linux | State |
|---|---|---|---|
| Motherboard fans + temps (IT8689E) | LibreHardwareMonitor + PawnIO | kernel hwmon sysfs `pwmN`/`pwmN_enable`/`fanN` (it87 driver) — `HwmonService`, `HwmonFanChannel` | ✅ code ❓ hw |
| CPU temperature | LHM (MSR) | `coretemp` "Package id 0"; package power from intel-rapl | ✅ code ❓ hw |
| GPU temp + fans | NVML `nvml.dll` | same NVML code, mapped to `libnvidia-ml.so.1` (`NativeResolver`) | ✅ code ❓ hw |
| RAM RGB (ENE on SMBus) | PawnIO SmbusI801 + global mutex | `/dev/i2c-N` I2C_SMBUS ioctls, same class/API; flock `/run/lock/vanta-smbus.lock`; never I2C_SLAVE_FORCE | ✅ code ❓ hw |
| IT5701 RGB, AL80, mouse | HidSharp | HidSharp (hidraw); engine runs as root so no udev rule needed | ✅ links unchanged ❓ hw |
| CPU load / RAM use / clocks | Win32 | `/proc/stat`, `/proc/meminfo`, cpufreq | ✅ |
| Memory modules (SMBIOS) | WMI | `/sys/firmware/dmi/entries/17-*/raw` | ✅ code ❓ hw |
| Images (AL80 screen, GIFs, colours-from-image) | WPF imaging | SkiaSharp (`RasterImage`) | ✅ (palette tests pass) |
| Precise frame timing | waitable timer | `clock_nanosleep` | ✅ code ❓ (7 timing tests need Linux) |
| Crash/hang failsafe | watchdog process | same watchdog + systemd `ExecStopPost=vanta --restore`. **Better than Windows:** pwm_enable is kernel state, so recovery returns headers to the BIOS curve instead of 100 % | ✅ code ❓ hw |
| Sleep/resume | SystemEvents | systemd sleep hook → `vanta --notify suspend/resume` → control socket | ✅ code ❓ |
| Run at boot, elevated | scheduled task, UAC | root systemd service `vantad` (group `vanta` may use the socket) | ✅ files ❓ |
| Lights-out hotkey | RegisterHotKey | parses to evdev codes; the evdev listener itself is not written | 🔨 |
| Typing Ripples key positions | WH_KEYBOARD_LL | evdev `/dev/input/event*` reader (positions only, same privacy rule) | ⬜ |
| UI | WPF (~6 000 lines) | Avalonia, as a user process talking to the root engine over `/run/vanta/control.sock` | ⬜ |

### Safety rules carried over
- **Motherboard fan gate:** VANTA does not drive any motherboard header on Linux until `sudo vanta --fan-verify` has
  proven pwm N → tach N on Linux (writes `linux-fans-verified.json`). Imported Windows curves wait behind this gate.
  The verify run asks for "yes", stays ≥ 40 % duty, one header at a time, and restores each header's mode (also on Ctrl+C).
- GPU fans are not gated (same NVML semantics as Windows); the owner's GPU curve applies as soon as the engine runs.
- The SMBus is only used through the kernel adapter; addresses claimed by kernel drivers (SPD 0x50-0x57) are refused by
  the kernel. Don't run OpenRGB at the same time (it ignores VANTA's lock file).
- `--discover` is strictly read-only (no fan writes, no SMBus transactions, no HID reports).
- Hardware-free runs (`--selftest --no-hardware`) keep every Windows refusal boundary; the self-test caught one I had
  missed in the Linux service stub and it is fixed.

## Status
Legend: ✅ done · 🔨 in progress · ⬜ not started · ❓ needs a Fedora boot to verify

| Area | State | Notes |
|---|---|---|
| `linux\` project linking shared source | ✅ | ~85 % of non-UI files link unchanged |
| SMBus via i2c-dev | ✅ ❓ | |
| hwmon fans/temps + fan gate + `--fan-verify` | ✅ ❓ | ids match Windows (`lhm:/lpc/it8689e/0/control/N` = pwmN+1, checked against config.json) |
| NVML on Linux | ✅ ❓ | needs NVIDIA proprietary driver (RPM Fusion) |
| System stats, SMBIOS, imaging, timer, heartbeat, watchdog | ✅ ❓ | |
| Headless engine `vanta --daemon` + control socket | ✅ ❓ | commands: status, suspend, resume |
| `vanta --discover` (read-only report) | ✅ ❓ | first thing to run on Fedora |
| Self-tests from the Linux build | ✅ 762/769 | 7 timing checks need Linux; theme (phase 11) and key-hook (1.20) checks wait for the UI |
| systemd unit, sleep hook, modules-load, install/uninstall | ✅ ❓ | `install.sh --import-windows <HwSuite folder>` copies config |
| Package `build-dist.sh` → tar.gz | ✅ | 37 MB |
| Avalonia UI (dashboard, fans, lighting, system pages, tray) | ⬜ | biggest remaining piece |
| UI ↔ engine protocol (config edits, telemetry, preview frames) | ⬜ | extend the control socket |
| evdev reader (Typing Ripples, Lights-out hotkey) | ⬜ | |
| Window-placement / theme logic split out of WPF files | ⬜ | then phases 11 and 1.20 self-tests come back |
| First boot on Fedora: discovery, it87, fan verify, lighting check | ⬜ ❓ | needs you |

## Next steps (in order)
1. **Avalonia UI**: shell + theme tokens, then Dashboard, Fans, Lighting, System pages, tray; ported page by page from
   `src\HwSuite\Ui`. The UI runs as you (not root) and talks to `vantad`.
2. Extend the control socket into the UI protocol (get/put config, telemetry stream, lighting preview frames).
3. evdev key reader (Typing Ripples + Lights-out hotkey), privacy rule unchanged (positions only, memory only).
4. On Fedora with you: `vanta --discover` → install it87 if needed → `sudo vanta --fan-verify` → start the service →
   check lighting device by device.

## Things only you can do (when you get to them)
1. **On Fedora:**
   - Install the NVIDIA driver: enable RPM Fusion, then `sudo dnf install akmod-nvidia` (reboot once it has built).
   - Copy `vanta-linux-1.20.0-linux.0.tar.gz` over (e.g. from the Windows drive), `tar xzf` it, then run
     `./vanta --discover` (read-only, no sudo needed for most of it) and send me the output.
   - If discovery says the it87 chip is missing: the IT8689E driver (see risks). I'll give exact steps once I've seen the report.
2. **WSL (optional, lets me test Linux-only behaviour from Windows):** WSL2 can't start here: virtualization is off in
   the BIOS and the "Virtual Machine Platform" Windows feature is off. To enable: turn on Intel VT-x in the BIOS
   (Gigabyte: *Tweaker → Advanced CPU Settings → Intel Virtualization Technology*), then in an admin terminal
   `wsl --install --no-distribution`, reboot, then `wsl --install FedoraLinux-44`. WSL can't reach the fans or RGB, but
   it would let the 7 timing self-tests and the Linux UI run here.

## Known risks / open questions
- **IT8689E support:** I have not confirmed whether Fedora's in-kernel `it87` knows this chip. If not, the usual answer
  is the out-of-tree `frankcrawford/it87` driver (DKMS/akmod) plus `acpi_enforce_resources=lax` or the driver's
  `ignore_resource_conflict=1`. Motherboard fan control is blocked until this is settled and `--fan-verify` passes.
- The header map and DC minimums were measured on Windows (and changed with the 2026-10 fan swap); `--fan-verify`
  re-proves the map, but minimum duties are taken from the imported config as they are.
- SMBus on Linux: if the i801 adapter is claimed by ACPI the kernel may refuse it without `acpi_enforce_resources=lax`.
  ENE detection itself has not run on Linux yet (it does a read probe; first run with you present).
- HidSharp on Linux finds interfaces by parsing report descriptors; the AL80's raw-HID interface (0xFF60/0x61) must be
  found that way — `--discover` shows whether it is.
- Bahnschrift (AL80 screen font) is a Windows font; Linux uses the nearest condensed sans (Adwaita/Noto). The AL80
  system panel will look slightly different.
- Wayland: a root GUI is refused, which is why the engine (root) and the UI (user) are split.

## Drives on this PC (as Windows sees them)
- E: "465 GB HDD" (NTFS) holds all VANTA work (`.`, Linux port in `linux\`). Fedora shows it under
  Files → Other Locations → "465 GB HDD".
- F: "FEDORA" 8 GB FAT32 = the Fedora install USB stick. Not used by VANTA.
- Fedora is on its own drive with a Linux filesystem, so Windows doesn't show it (owner, 2026-11-14). Nothing of VANTA is installed there yet.

## Log
- **2026-11-14** — Linux UI ported to Avalonia (all pages, dialogs, tray, theme; fixture screenshots match the Windows
  look). Engine + UI run in one user process like Windows. Package rebuilt with `fedora-check.sh` (read-only first look).
- **2026-11-13 (session 1)** — Started. Surveyed the source; created `linux\` and this document. WSL unavailable
  (virtualization off). Wrote the Linux platform layer (SMBus, hwmon, NVML mapping, /proc stats, SMBIOS, SkiaSharp
  imaging, timer, heartbeat, watchdog, service stub), the headless engine with control socket, `--discover`,
  `--fan-verify`, `--selftest`, the systemd/install packaging and the drift check. Self-tests from the Linux build on
  Windows: 762 pass / 7 fail (timing, expected off Linux). Package built. Nothing run on Fedora or on hardware yet.
