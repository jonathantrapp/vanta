#!/bin/bash
# Regenerates the mechanically converted UI files from src/HwSuite/Ui. Files listed in HAND_PORTED are maintained by hand
# in linux/src/Vanta.Linux/Ui and are NOT overwritten. Run from anywhere.
set -euo pipefail
cd "$(dirname "$0")/../.."
HAND_PORTED=" Motion WindowPlacement WpfCompat MainWindow.Layout "
for f in "$@"; do
  case "$HAND_PORTED" in *" $f "*) echo "skip $f (hand-ported)"; continue ;; esac
  src="src/HwSuite/Ui/$f.cs"; dst="linux/src/Vanta.Linux/Ui/$f.cs"
  [ "$f" = "MainWindow" ] && { src="src/HwSuite/Ui/MainWindow.xaml.cs"; }
  python linux/tools/wpf2avalonia.py "$src" "$dst"
done
