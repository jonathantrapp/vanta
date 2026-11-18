# Model O Wireless on-mouse tests: results (2026-10-23)

All writes were the verified report-0x00 sequence via `--mouse-probe` (logs: `t*-*.txt` / `.json` here). Videos: `.\video\test 2 started recording a tad late.mp4` and `test 3.mp4`.
Analysis: brightness of the side strip per real frame (the phone records at a variable frame rate, so each frame's own timestamp was used, not index / average fps). The dark dip of each breathing cycle is sharp and was timed with sub-frame interpolation.

## 1. Stored or temporary: NOT saved immediately (owner's observation; see also test 6)

- Probe set green at about 20:1x. Unplugging the cable: still green (it's battery powered, as expected).
- Mouse switched off and on: it came back **blue**, VANTA's Downpour companion `#1A3565` (in `mouse-lighting.json`), which VANTA had sent long before.
  Re-plugging the receiver: still blue.
- So the mouse doesn't commit a write to its stored profile straight away, but an older write by the same command *was* kept.
  Something saves later: perhaps a delay, a sleep/idle save or a reconnect. When, and how often, is still unknown.
  Practical reading: a burst of writes is not a burst of flash saves, but we can't yet say re-sync writes are never saved.

## 2. Speed byte to period: period = 0.512 s × speed byte (measured)

| Speed byte | Period (s) | Cycle-to-cycle spread | Per step |
|---|---|---|---|
| 1 | 0.509 | ±0.018 (short cycles, few frames each) | 0.509 |
| 2 | 1.024 | ±0.002 | 0.512 |
| 5 | 2.560 | ±0.002 | 0.512 |
| 11 | 5.631 | ±0.001 | 0.5119 |
| 20 | 10.242 | ±0.005 | 0.5121 |

Test 3 measured speed 11 independently at 5.632 s. The mouse's timer is steady (millisecond-level jitter), and its 512 ms step matches real time
to about 0.04 %, roughly ≤25 ms of drift per minute. Effect 5 (single-colour breathing) was measured; effect 3 (multi-colour) is assumed to use the same timer (not filmed).

## 3. Phase reset: YES when the setting changes; an identical re-send confirmed in test 5

- Test 2, all four speed-changing writes: the first dark dip of the new speed comes a fixed time after the write: **latency L + 0.505 × new period**.
  The fit across the four writes is linear to a few ms (slopes 0.504, 0.506, 0.504). So every write restarts the cycle at its brightest point, and the dip comes half a cycle later.
- Test 3, identical setting re-sent 2.017 s after the first write: the dips line up with the *second* write (predicted 17.70 s, seen 17.32 s).
  If it hadn't reset they'd line up with the first (15.68 s, not seen). The video clock is only known to about ±1 s and the camera moved for the first 8 s, so this one isn't conclusive.
- The absolute latency L (write → light change) is somewhere between about 0.2 and 1.2 s. The phone's file time only has 1 s resolution, so it can't be pinned down from these videos.

## 4. Multi-colour breathing (effect 3): works (owner: "did colors and stuff")

2 colours and 7 colours on the receiver, and 7 on the cable: all writes accepted, and the owner saw colours cycling. Not filmed and not watched closely, so the colour order wasn't confirmed.

## What this means for sync

- Breathing in time with VANTA **is realistic**. VANTA's breathing period can snap to a multiple of 0.512 s while the mouse is in the scene, so the periods match exactly.
  One write at the scene start lines up the phase, plus a re-sync write every few minutes to cancel the ≤25 ms/min drift. Expected error: a few tens of ms right after each line-up, at most about 0.15 s before the next one.
- Still impossible: Rainfall, Ripples, Downpour, waves and anything per-LED. There's no live path.

## 5. Test 5 (2026-10-23 21:16, `video\test5.mp4`, 120 fps, mouse + case in shot)

The case fans flashed white at each mouse write (marker posted 0.0–0.8 ms before the mouse's first report; logs `t5-*.txt`).

- **Mouse vs case fans:** on all 9 clean blinks the mouse's light switched (in a single 8 ms frame) *before* the fans flashed:
  −39, −69, −53, −45, −62, −45, −15, −52, −26 ms, **median −45 ms**. The spread is the strip's 60/s cadence and the 120 fps frames.
  So the mouse reacts within a few tens of ms of the write, and is the quicker of the two. The old 0.2–1.2 s range was wrong: it came from the phone's file clock (about 1 s off).
  VANTA aligns the mouse to what the fans show, so the setting is −45 ms (mouse relative to fans).
- **Identical re-send restarts the cycle: confirmed.** Byte 20, cyan, sent again 3.016 s after the first write. Dips came at 61.41, 71.65, 81.88, 92.12 and 102.36 s (period 10.240 s).
  Reset predicted the first dip at 61.36 s; no reset predicted 58.34 s, and nothing happened there.
- **Darkest point:** 5.174 s after the re-send's light change, i.e. 0.505 of the period. That matches test 2 (0.504–0.506).

## 6. Test 6 (save timing): NOT saved after 10 minutes either

The mouse was set magenta at 21:19:00 and left untouched for 10+ minutes (the owner used only the keyboard); it was then switched off and on. It came back **blue** (#1A3565, VANTA's Downpour companion), the same as in test 1.
- So a lighting write isn't committed by waiting, or by the mouse idling for 10 minutes. These lighting reports behave as temporary in practice, so re-sync writes don't pile up saves.
- Still unknown: how the blue *was* saved at some point. Possibilities include a long sleep, a charge/cable event, a receiver re-pair, or something VANTA or CORE once sent.
- Consequence: after the mouse is switched off and on it shows the old blue. VANTA doesn't notice: the receiver stays connected, so there's no reconnect event.
  With "Locked to VANTA" the 5-minute re-sync puts the right setting back. Without it, the blue stays until the look changes. Follow-up idea: re-send periodically, or detect a mouse power-cycle.
(The "infinity cannot be written as JSON" message at the end was a bug in the probe's log file when there's no strip marker. The write itself worked. Fixed.)

## Built since (2026-10-23, in `src\HwSuite\bin\Release` only, NOT installed; self-test 805/0)

- "Locked to VANTA" switch on the Mouse card (config `MouseSync`, off by default; `MouseSyncLatencyMs` default −45 = measured).
  When on and the effect is Breathing:
  - VANTA's breathing snaps to the nearest 0.512 s step (speed slider 0–100 % → mouse bytes 16..3).
  - The single write is timed on the engine clock so the mouse's darkest point (0.505 of a period after its light changes, 45 ms ahead of the fans) lands on VANTA's as the fans show it.
  - It lines up once per launch, and again every 5 minutes.
- Nothing else changes: other effects, scenes and the sync-off behaviour are as before (self-tests check this).


## 7. Test 7: multi-colour breathing (effect 3), red + blue, byte 5, cable (`video\test7.mp4`, 120 fps)

After the write (fan flash at 14.82 s) the mouse showed red for half a breath, then alternated blue, red, blue, red... one full breath per colour.
Each breath is 2.56 s (the same 0.512 s × byte rule), and the colour changes at every dark point (15.0-16.1 red, 16.25-18.6 blue, 18.75-21.0 red, 21.25-23.6 blue...).
So it's "one breath per colour, in order", starting from the brightest point of colour 0.

## 8. Test 8: live colour stream over the receiver (2026-10-23 22:09, cable unplugged)

Static-colour effect report only, rainbow sweep, 10 s per rate. The owner moved the mouse throughout and felt no stutter at any rate.

| Rate | Writes | Failed | Achieved | Write time mean / 99 % / max |
|---|---|---|---|---|
| 10/s | 101 | 0 | 10.1/s | 0.39 / 0.76 / 0.86 ms |
| 20/s | 201 | 0 | 20.1/s | 0.42 / 0.79 / 2.65 ms |
| 30/s | 301 | 0 | 30.1/s | 0.38 / 0.65 / 0.71 ms |
| 60/s | 601 | 0 | 60.1/s | 0.40 / 0.69 / 2.95 ms |

VANTA's setting was put back afterwards (ok). Live colour streaming is viable, the way SignalRGB does it.
Not yet measured: battery drain while streaming. (A write returning OK means the receiver took it; delivery over the radio wasn't filmed.)
