# RTX 3070 FE zero-RPM investigation (2026-11-04)

Card: RTX 3070 Founders Edition, driver 617.42, 2 fans. Everything below was read through NVML
(`nvml_fanlog.py`, read-only, 0.5 s, raw data in `fanlog-20261104.csv`). Writes were made only in the
owner-attended tests at the end (`handback_test.py`, elevated, 30% minimum, always ending on VBIOS auto).

## Measurements

| # | Time | Situation | Result |
|---|---|---|---|
| 1 | 09:24-09:27 | Afterburner + Wallpaper Engine (hidden behind windows), VANTA closed. 34-35 C, P8, ~22 W | fans 30%, ~1000 RPM, policy 0 |
| 2 | 09:27:29 / 09:27:39 | Afterburner exited, then Wallpaper Engine closed | **0 RPM at 09:27:46, 34 C, ~19 W** |
| 3 | 09:28-09:31 | Wallpaper Engine visible (rendering, P0, 65-123 W), no Afterburner | 0 RPM up to 59 C; **started at 60-61 C (09:31:03)** |
| 4 | 09:32:50-09:36:41 | Wallpaper Engine closed, GPU cools 52 -> 34 C | 30% all the way down; **stopped at 09:36:41 after ~30 s at 34 C** |
| 5 | 09:37:33-09:40 | Afterburner started again while stopped | stayed 0 RPM (35-37 C) |
| 6 | 09:41:45-09:45:44 | VANTA started (fan control off since 09:14) | stayed 0 RPM (36-39 C) |
| 7 | 09:48:15 | handback test, NOT elevated | every NVML write rc 4 (NO_PERMISSION), nothing changed |
| 8 | 09:50:12-09:50:27 | fans stopped (firmware "off"), 37-38 C: manual 30% for 14 s, then `SetDefaultFanSpeed_v2` | **stopped 1.2 s after hand-back, 37 C** |
| 9 | 09:52:30-09:53:16 | Wallpaper Engine visible again | **started at 53-55 C** right after a 113 W spike (not 60 this time) |
| 10 | 09:53:50 | fans started by firmware, 44 C: `SetDefaultFanSpeed_v2` only (rc 0) | **kept spinning**, still 1000 RPM at 37 C |
| 11 | 09:56:36-09:56:51 | fans started by firmware, 34-35 C: manual 30% for 15 s, then hand-back | **kept spinning**; stopped at 09:57:11 (~30 s at 34 C, same as #4) |

Other facts:
- `target%` always reads 30 (the NVML minimum), also while the fan is stopped. Only speed% and RPM show a stop.
- NVML exports (430, read from the PE export table) have nothing that reports the zero-fan state or its thresholds.
  `nvmlDeviceGetThermalSettings` exposes one sensor (GPU core). `nvmlDeviceGetCoolerInfo`: both fans are PWM coolers
  for GPU + memory + power supply, so the firmware may also use temperatures NVML cannot read.
- NVML temperature is whole degrees, so "34 C" can be 34.0-34.9.
- The `rea` MCP has no analysis backend configured here (Ghidra/IDA not installed), so `nvapi64.dll` was not examined.

## Cause (proven by the tests above)

The fan stop is entirely the card's firmware, with a very wide hysteresis:

- **Start:** around 53-61 C (not a single temperature; it started at 55 C during a load spike and at 60-61 C at steadier load).
- **Stop:** only once the GPU reads 34 C for about 30 s.
- This card idles at 34-37 C, right on the stop point. Any trip to ~55-60 C (a game, the wallpaper rendering visibly)
  leaves the fans at 30% / ~1000 RPM until the GPU gets back to 34 C, which with Wallpaper Engine running in the
  background (~22 W) it may not do at all.
- The firmware keeps its own on/off state while software has manual control. Handing back
  (`nvmlDeviceSetDefaultFanSpeed_v2`) returns to whatever state the firmware is in: "off" -> fans stop at once (#8, and the
  older "0 RPM at 44 C" note); "on" -> fans keep running (#10, #11). There is no NVML call that resets it.
- Afterburner, Wallpaper Engine and VANTA do not make the fans spin. Wallpaper Engine only matters because rendering it
  visibly heats the GPU past the start point and keeps the idle power (and temperature) higher.

## What VANTA can and cannot do

- NVML cannot set a GPU fan below 30% (`nvmlDeviceGetMinMaxFanSpeed` = 30..100), so a 0% curve point always runs at 30%.
- "Hand back to VBIOS below 50 C" (current config) is already the best NVML can do: below 50 C the firmware decides, and
  the fans are either off or at 30%, which is what VANTA's own minimum would be anyway. It cannot force them off.
- So **the firmware will not allow 0 RPM between ~35 C and its start point once it has started the fans**, and nothing in
  NVML can override that.

## Alternatives

1. **Lower the idle temperature by a degree or two** so the GPU actually reaches 34 C: let Wallpaper Engine pause while
   other windows cover it (its own setting), and/or a slightly higher case-fan minimum. (#2 and #4 stopped only at ~13-19 W.)
2. **Avoid the trip to 55-60 C on the desktop**: visible Wallpaper Engine reached 55-61 C in 45-90 s.
3. **NVAPI private cooler interface** (`NvAPI_GPU_ClientFanCoolers*`): undocumented, and not verified to allow 0% or to
   reset the firmware state on this card. Would need `nvapi64.dll` analysis (Ghidra for `rea`) and attended tests first.
   Against the project rule of preferring official interfaces.
4. **VBIOS change**: not recommended (FE cards are signed; risk of bricking).
