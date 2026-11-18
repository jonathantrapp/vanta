# Safety

VANTA writes to fan headers, SMBus RGB controllers and USB devices. A wrong address or register on the wrong board can do real damage, so these are the rules it follows and the ones to keep if you change it.

## Drivers

- It never bundles or loads WinRing0, InpOutx64 or the old `gdrv.sys`. Windows Defender blocks them, and they give any program raw port access.
- Sensors and SMBus go through LibreHardwareMonitor with PawnIO. GPU fans go through NVML. Devices on USB use HID.

## Fans

- Fan control is off until you turn it on in the app.
- Every fan has a minimum duty, found with a stall test. VANTA never drives a fan below it, and it restarts a stalled fan from a higher value.
- VANTA hands every fan back to firmware on exit, on sleep and after a crash. A small file of controlled channels lets a watchdog finish the hand-back if the app dies.
- It stops the Gigabyte services (EasyTune, GService, RGB Fusion) before it touches the Super I/O or SMBus, and restarts only the ones it stopped.

## SMBus

- Every SMBus access takes the system-wide mutex `Global\Access_SMBus.HTP.Method` first, in both spellings the vendor tools use.
- Memory LED counts are read from the controllers, not assumed.

## Lighting and devices

- Lighting output is off until you enable it.
- Stored settings on the mouse and keyboard are never written once per frame. Live frames use the devices' streaming paths.
- On the AL80 VANTA never sends opcodes 0xB0-0xB7, VIA commands 0x0A, 0x0B, 0x06 or 0x10, or vendor opcode 0x20. It never mixes lighting into a screen transfer, and it works over the USB cable only.
- Screen pictures are sent only after you confirm the keyboard's 8 flash slots are full, so they stay in RAM and do not wear the flash.

## Tests

- `--selftest --no-hardware` opens no device. It counts every attempt to cross a hardware boundary and refuses it.
- Run a real-hardware test only with the Gigabyte services stopped and the app closed, and never leave a stall test unattended.
