#!/bin/bash
# VANTA for Linux installer (Fedora). Run as root:  sudo ./install.sh [--import-windows <HwSuite folder>] [--start]
#   --import-windows DIR   copy settings from the Windows install, e.g.
#                          /run/media/$USER/<Windows>/Users/<you>/AppData/Roaming/HwSuite
#   --start                start the engine now (otherwise it starts at the next boot)
# Re-running is safe: it replaces the program files and keeps settings. Uninstall: sudo ./uninstall.sh
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DEST=/opt/vanta
DATA=/var/lib/vanta/HwSuite
IMPORT=""; START=0
while [ $# -gt 0 ]; do
  case "$1" in
    --import-windows) IMPORT="$2"; shift 2 ;;
    --start) START=1; shift ;;
    *) echo "unknown option $1"; exit 2 ;;
  esac
done
[ "$(id -u)" -eq 0 ] || { echo "run with sudo"; exit 1; }
[ -x "$HERE/vanta" ] || { echo "vanta binary not found next to install.sh"; exit 1; }

if systemctl is-active --quiet vantad; then echo "stopping the running engine"; systemctl stop vantad; fi

getent group vanta >/dev/null || groupadd --system vanta
if [ -n "${SUDO_USER:-}" ] && ! id -nG "$SUDO_USER" | grep -qw vanta; then
  usermod -aG vanta "$SUDO_USER"; echo "added $SUDO_USER to group vanta (log out and in for it to apply)"
fi

mkdir -p "$DEST"
if [ -d "$DEST" ] && [ -x "$DEST/vanta" ]; then rm -rf "$DEST.previous"; cp -a "$DEST" "$DEST.previous"; echo "previous version kept in $DEST.previous"; fi
cp -a "$HERE"/. "$DEST"/
chmod 755 "$DEST/vanta"

install -m 644 "$HERE/packaging/vantad.service" /etc/systemd/system/vantad.service
install -m 755 "$HERE/packaging/vanta-sleep" /usr/lib/systemd/system-sleep/vanta
install -m 644 "$HERE/packaging/modules-load-vanta.conf" /etc/modules-load.d/vanta.conf
modprobe i2c-dev 2>/dev/null || true
modprobe it87 2>/dev/null || echo "note: it87 did not load (driver for the IT8689E not installed yet — see PORTING_STATUS.md)"

mkdir -p "$DATA"
if [ -n "$IMPORT" ]; then
  for f in config.json mouse-lighting.json keyboard-lighting.json; do
    if [ -f "$IMPORT/$f" ]; then
      [ -f "$DATA/$f" ] && cp -a "$DATA/$f" "$DATA/$f.before-import"
      cp "$IMPORT/$f" "$DATA/$f"; echo "imported $f"
    fi
  done
  echo "note: motherboard fan curves were imported but stay on BIOS control until 'sudo vanta --fan-verify' confirms the headers on Linux"
fi
chown -R root:vanta /var/lib/vanta; chmod -R g+rwX,o-rwx /var/lib/vanta

ln -sf "$DEST/vanta" /usr/local/bin/vanta
systemctl daemon-reload
systemctl enable vantad >/dev/null
echo "installed to $DEST; engine enabled at boot"
if [ "$START" -eq 1 ]; then systemctl start vantad; sleep 2; systemctl --no-pager status vantad | head -5; fi
echo "check the hardware any time with:  vanta --discover   (read-only)"
