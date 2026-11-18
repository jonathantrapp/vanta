#!/bin/bash
# Lists Windows source files whose Linux ported copy (src/Vanta.Linux/Ported) may be out of date.
#   linux/check-drift.sh          compare against the recorded hashes
#   linux/check-drift.sh --record re-record after re-porting (only when the Linux copy has been brought up to date)
cd "$(dirname "$0")"
REC=ported-sources.sha256
files=$(grep -ho '^// PORTED from src[^ ]*' src/Vanta.Linux/Ported/*.cs | cut -c16- | tr '\\' '/' | sort -u)
if [ "${1:-}" = "--record" ]; then
  (cd .. && for f in $files; do sha256sum "$f"; done) > "$REC"; echo "recorded $(wc -l < "$REC") source hashes"; exit 0
fi
(cd .. && sha256sum --quiet -c "linux/$REC") && echo "no drift: every ported file's Windows original is unchanged"
