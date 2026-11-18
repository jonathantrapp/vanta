#!/bin/bash
# Removes VANTA for Linux. Settings in /var/lib/vanta are kept unless --purge is given.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run with sudo"; exit 1; }
systemctl disable --now vantad 2>/dev/null || true
/opt/vanta/vanta --restore 2>/dev/null || true
rm -f /etc/systemd/system/vantad.service /usr/lib/systemd/system-sleep/vanta /etc/modules-load.d/vanta.conf /usr/local/bin/vanta
rm -rf /opt/vanta /opt/vanta.previous
systemctl daemon-reload
if [ "${1:-}" = "--purge" ]; then rm -rf /var/lib/vanta; echo "settings removed"; else echo "settings kept in /var/lib/vanta"; fi
echo "VANTA removed"
