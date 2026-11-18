#!/bin/bash
# Build the Fedora package folder + tarball from Windows (Git Bash) or Linux.
#   linux/build-dist.sh            -> linux/dist/vanta-linux-<version>/ and .tar.gz
set -euo pipefail
export AVALONIA_TELEMETRY_OPTOUT=1   # Avalonia build telemetry off
cd "$(dirname "$0")"
DOTNET="${DOTNET:-../tools/dotnet/dotnet.exe}"; [ -x "$DOTNET" ] || DOTNET=dotnet
VER=$(sed -n 's:.*<InformationalVersion>\(.*\)</InformationalVersion>.*:\1:p' src/Vanta.Linux/Vanta.Linux.csproj)
OUT="dist/vanta-linux-$VER"
rm -rf "$OUT"
"$DOTNET" publish src/Vanta.Linux -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false -o "$OUT" -nologo -v q
mkdir -p "$OUT/packaging"
cp packaging/vantad.service packaging/vanta-sleep packaging/modules-load-vanta.conf "$OUT/packaging/"
cp packaging/install.sh packaging/uninstall.sh packaging/fedora-check.sh PORTING_STATUS.md "$OUT/"
rm -f "$OUT"/*.pdb
# tar with Unix permissions set explicitly (NTFS has no exec bit)
python - "$OUT" <<'PY'
import sys, tarfile, os
out = sys.argv[1]; name = os.path.basename(out)
exe = {"vanta", "install.sh", "uninstall.sh", "fedora-check.sh", "packaging/vanta-sleep", "createdump"}
def f(ti):
    rel = ti.name.split('/', 1)[1] if '/' in ti.name else ''
    ti.uid = ti.gid = 0; ti.uname = ti.gname = 'root'
    ti.mode = 0o755 if ti.isdir() or rel in exe else 0o644
    return ti
with tarfile.open(out + '.tar.gz', 'w:gz') as t: t.add(out, arcname=name, filter=f)
print(out + '.tar.gz')
PY
