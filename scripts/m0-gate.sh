#!/usr/bin/env bash
# M0 gate: N bots in one zone at a steady 20 Hz.
#
# Starts auth, zone and gateway from a Release build, runs the bot swarm, then checks:
#   - every bot connected and received snapshots at >= 8 Hz (10 Hz nominal)
#   - the zone held >= 19.5 Hz with p99 tick work under 5 ms and no overruns
#
# Usage: scripts/m0-gate.sh [bots=100] [seconds=60]
set -euo pipefail

BOTS="${1:-100}"
SECONDS_TO_RUN="${2:-60}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/m0-gate"
BIN="bin/Release/net10.0"

mkdir -p "$OUT"
rm -f "$OUT"/*.log

echo "building (Release)..."
dotnet build "$ROOT/Meridian.slnx" -c Release -v quiet -nologo >"$OUT/build.log" 2>&1 || { cat "$OUT/build.log"; exit 1; }

export MERIDIAN_LOGINS_PER_MINUTE_PER_IP=100000
export DOTNET_gcServer=0

pids=()
cleanup() { for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done; wait 2>/dev/null || true; }
trap cleanup EXIT

dotnet "$ROOT/src/Meridian.Server.Zone/$BIN/Meridian.Server.Zone.dll" >"$OUT/zone.log" 2>&1 & pids+=($!)
dotnet "$ROOT/src/Meridian.Server.Gateway/$BIN/Meridian.Server.Gateway.dll" >"$OUT/gateway.log" 2>&1 & pids+=($!)
dotnet "$ROOT/src/Meridian.Server.Auth/$BIN/Meridian.Server.Auth.dll" >"$OUT/auth.log" 2>&1 & pids+=($!)

echo "waiting for services..."
for _ in $(seq 1 60); do
  if curl -fs http://127.0.0.1:5080/healthz >/dev/null 2>&1 && grep -q "connected to zone" "$OUT/gateway.log"; then break; fi
  sleep 0.5
done
grep -q "connected to zone" "$OUT/gateway.log" || { echo "gateway never reached the zone"; tail "$OUT"/*.log; exit 1; }

echo "running $BOTS bots for ${SECONDS_TO_RUN}s..."
set +e
dotnet "$ROOT/src/Meridian.Bots/$BIN/Meridian.Bots.dll" --count "$BOTS" --duration "$SECONDS_TO_RUN" | tee "$OUT/bots.log"
bots_exit=${PIPESTATUS[0]}
set -e

# Zone metrics windows recorded while all bots were in (skip the ramp-up window).
grep "METRICS players=$BOTS " "$OUT/zone.log" | tail -n +2 >"$OUT/zone-metrics.log" || true
echo
echo "ZONE METRICS (5 s windows with all $BOTS players)"
cat "$OUT/zone-metrics.log"

zone_ok=$(awk -v n="$(wc -l <"$OUT/zone-metrics.log")" '
  { for (i = 1; i <= NF; i++) { split($i, kv, "="); v[kv[1]] = kv[2] }
    if (v["hz"] < 19.5 || v["work_p99_ms"] > 5 || v["overruns"] > 0) bad++ }
  END { print (n > 0 && bad == 0) ? "yes" : "no" }' "$OUT/zone-metrics.log")

echo
if [[ "$bots_exit" == 0 && "$zone_ok" == "yes" ]]; then
  echo "M0 GATE: PASS"
  exit 0
fi
echo "M0 GATE: FAIL (bots_exit=$bots_exit zone_ok=$zone_ok); logs in $OUT"
exit 1
