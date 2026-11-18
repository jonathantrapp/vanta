#!/bin/bash
# VANTA first look on Fedora. READ-ONLY: lists hardware, runs the unit checks that open no device.
# Nothing is written to fans, lighting, the SMBus or any USB device. Takes about 3 minutes.
# Run from the unpacked folder:   ./fedora-check.sh
# Results go to ./fedora-check-results/ (copy that folder back to the Windows drive, or a USB stick).
cd "$(dirname "$0")"
OUT=fedora-check-results; mkdir -p "$OUT"
{ echo "== system"; uname -a; cat /etc/fedora-release 2>/dev/null; echo; echo "== loaded modules (hardware related)"; lsmod | grep -Ei 'it87|nct|coretemp|i2c|nvidia|nouveau|hid' ; echo; echo "== kernel cmdline"; cat /proc/cmdline; echo; echo "== i2c adapters"; ls -l /sys/class/i2c-dev/ 2>&1; for d in /sys/class/i2c-dev/*; do echo "$d: $(cat $d/name 2>/dev/null)"; done; echo; echo "== hwmon"; for d in /sys/class/hwmon/*; do echo "$d: $(cat $d/name 2>/dev/null)"; done; echo; echo "== nvidia"; nvidia-smi 2>&1 | head -20; echo; echo "== usb"; lsusb 2>&1; echo; echo "== Super I/O chip search in boot log"; sudo -n dmesg 2>/dev/null | grep -iE 'it87|super ?i/o|ACPI.*resource|i801' | head -20; } > "$OUT/system.txt" 2>&1
echo "1/3 hardware report (as you)"; ./vanta --discover > "$OUT/discover-user.txt" 2>&1
echo "2/3 hardware report (with sudo: needed to read SMBIOS, NVML details and device access) - enter your password"
sudo ./vanta --discover > "$OUT/discover-root.txt" 2>&1; sudo chown "$USER" "$OUT"/*
echo "3/3 unit checks (no device opened, ~2 minutes)"; ./vanta --selftest --no-hardware > "$OUT/selftest.txt" 2>&1
tail -1 "$OUT/selftest.txt"; grep -c "" "$OUT"/*.txt >/dev/null
echo; echo "Done. Results are in $(pwd)/$OUT"
