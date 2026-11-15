#!/usr/bin/env bash
# M2 gate: "twink a rifle with another player's buff".
#
# Starts auth, gateway and zone (with development commands, for /give), then runs two clients
# through the scenario in src/Meridian.Bots/TwinkGate.cs: an Operative cannot wear a QL 1
# Kestrel Mk2 on its own buff, can once a Medic's Reflex Boost lands, and the rifle goes
# inactive when that buff is dropped. Exits 0 on PASS.
#
# Usage: scripts/m2-gate.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/m2-gate"
BIN="bin/Release/net10.0"
mkdir -p "$OUT"
rm -f "$OUT"/*.log

echo "building (Release)..."
dotnet build "$ROOT/Meridian.slnx" -c Release -v quiet -nologo >"$OUT/build.log" 2>&1 || { cat "$OUT/build.log"; exit 1; }

export MERIDIAN_DEV_COMMANDS=1 MERIDIAN_PBKDF2_ITERATIONS=10000
export MERIDIAN_STORE=file MERIDIAN_STORE_DIR="$OUT/store"
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

dotnet "$ROOT/src/Meridian.Bots/$BIN/Meridian.Bots.dll" --twink-gate | tee "$OUT/gate.log"
exit "${PIPESTATUS[0]}"
