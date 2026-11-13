#!/usr/bin/env bash
# M0 gate: N bots in one zone at a steady 20 Hz.
#
# Starts auth, zone and gateway from a Release build, runs the bot swarm, then checks:
#   - every bot connected and received snapshots at >= 8 Hz (10 Hz nominal)
#   - in steady state (after 10 s of warm-up), every 5 s window held >= 19.5 Hz with no
#     overruns, and p99 tick work over all steady-state ticks together is under 5 ms
#
# Usage: scripts/m0-gate.sh [bots=100] [seconds=60] [wander|hunt]
#   hunt: bots fight the zone's mobs instead of wandering (combat load).
set -euo pipefail

BOTS="${1:-100}"
SECONDS_TO_RUN="${2:-60}"
MODE="${3:-wander}"
BOT_FLAGS=()
if [[ "$MODE" == "hunt" ]]; then BOT_FLAGS+=(--hunt); fi
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/m0-gate"
BIN="bin/Release/net10.0"

mkdir -p "$OUT"
rm -f "$OUT"/*.log

echo "building (Release)..."
dotnet build "$ROOT/Meridian.slnx" -c Release -v quiet -nologo >"$OUT/build.log" 2>&1 || { cat "$OUT/build.log"; exit 1; }

export MERIDIAN_LOGINS_PER_MINUTE_PER_IP=100000
export DOTNET_gcServer=0
# A fresh development store per run, shared by auth and zone. Bots register on first login;
# cheap password hashing keeps 100 registrations from dominating start-up (auth warns about it).
export MERIDIAN_STORE=file MERIDIAN_STORE_DIR="$OUT/store" MERIDIAN_PBKDF2_ITERATIONS=10000
rm -rf "$MERIDIAN_STORE_DIR"

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

echo "running $BOTS bots ($MODE) for ${SECONDS_TO_RUN}s..."
set +e
dotnet "$ROOT/src/Meridian.Bots/$BIN/Meridian.Bots.dll" --count "$BOTS" --duration "$SECONDS_TO_RUN" "${BOT_FLAGS[@]}" | tee "$OUT/bots.log"
bots_exit=${PIPESTATUS[0]}
set -e

# Steady-state windows only: skip the first two 5 s windows with all bots in. They include
# JIT warm-up (in hunt mode, combat code first runs when bots reach their mobs), which on CI
# runners can spike one window's p99 past the threshold. The gate is about steady 20 Hz.
grep "METRICS players=$BOTS " "$OUT/zone.log" | tail -n +3 >"$OUT/zone-metrics.log" || true
grep "TICKS players=$BOTS " "$OUT/zone.log" | tail -n +3 >"$OUT/zone-ticks.log" || true
echo
echo "ZONE METRICS (5 s windows with all $BOTS players)"
cat "$OUT/zone-metrics.log"

# Rate and overruns per window: a window below 19.5 Hz is a real stall.
windows_ok=$(awk -v n="$(wc -l <"$OUT/zone-metrics.log")" '
  { for (i = 1; i <= NF; i++) { split($i, kv, "="); v[kv[1]] = kv[2] }
    if (v["hz"] < 19.5 || v["overruns"] > 0) bad++ }
  END { print (n > 0 && bad == 0) ? "yes" : "no" }' "$OUT/zone-metrics.log")

# Tick work percentiles over the whole steady-state run, not per window: a window has only
# 100 ticks, so its p99 is its second-worst tick and one isolated pause would fail it.
read -r ticks p50 p99 max < <(sed 's/.*work_us=//' "$OUT/zone-ticks.log" | tr ',' '\n' | grep . | sort -n | awk '
  { w[NR] = $1 }
  function at(p,  i) { i = int(p * NR); if (i < p * NR) i++; if (i < 1) i = 1; return w[i] / 1000 }
  END { if (NR == 0) print 0, 0, 0, 0; else printf "%d %.3f %.3f %.3f\n", NR, at(0.5), at(0.99), w[NR] / 1000 }')
echo
echo "STEADY STATE  ticks=$ticks work_p50_ms=$p50 work_p99_ms=$p99 work_max_ms=$max"
p99_ok=$(awk -v t="$ticks" -v p="$p99" 'BEGIN { print (t > 0 && p < 5) ? "yes" : "no" }')
zone_ok=$([[ "$windows_ok" == yes && "$p99_ok" == yes ]] && echo yes || echo no)

echo
if [[ "$bots_exit" == 0 && "$zone_ok" == "yes" ]]; then
  echo "M0 GATE: PASS"
  exit 0
fi
echo "M0 GATE: FAIL (bots_exit=$bots_exit zone_ok=$zone_ok); logs in $OUT"
exit 1
