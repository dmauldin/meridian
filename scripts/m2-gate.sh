#!/usr/bin/env bash
# M2 gate: "twink a rifle with another player's buff", and carry it into another zone.
#
# Starts auth, a gateway and two zones (Glasswind Flats and Rustfall Basin, with development
# commands for /give), then runs two clients through src/Meridian.Bots/TwinkGate.cs: an
# Operative cannot wear a QL 1 Kestrel Mk2 on its own buff, can once a Medic's Reflex Boost
# lands, walks into Rustfall Basin with the rifle and both buffs intact on the same connection,
# and the rifle goes inactive when the Medic's buff is dropped. Exits 0 on PASS.
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

MERIDIAN_ZONE_KEY=glasswind_flats MERIDIAN_ZONE_PORT=7800 \
  dotnet "$ROOT/src/Meridian.Server.Zone/$BIN/Meridian.Server.Zone.dll" >"$OUT/zone-glasswind.log" 2>&1 & pids+=($!)
MERIDIAN_ZONE_KEY=rustfall_basin MERIDIAN_ZONE_PORT=7801 \
  dotnet "$ROOT/src/Meridian.Server.Zone/$BIN/Meridian.Server.Zone.dll" >"$OUT/zone-rustfall.log" 2>&1 & pids+=($!)
MERIDIAN_ZONES="1@127.0.0.1:7800,2@127.0.0.1:7801" \
  dotnet "$ROOT/src/Meridian.Server.Gateway/$BIN/Meridian.Server.Gateway.dll" >"$OUT/gateway.log" 2>&1 & pids+=($!)
dotnet "$ROOT/src/Meridian.Server.Auth/$BIN/Meridian.Server.Auth.dll" >"$OUT/auth.log" 2>&1 & pids+=($!)

echo "waiting for services..."
for _ in $(seq 1 60); do
  if curl -fs http://127.0.0.1:5080/healthz >/dev/null 2>&1 \
     && grep -q "connected to zone 1 " "$OUT/gateway.log" && grep -q "connected to zone 2 " "$OUT/gateway.log"; then break; fi
  sleep 0.5
done
grep -q "connected to zone 2 " "$OUT/gateway.log" || { echo "gateway never reached both zones"; tail "$OUT"/*.log; exit 1; }

dotnet "$ROOT/src/Meridian.Bots/$BIN/Meridian.Bots.dll" --twink-gate | tee "$OUT/gate.log"
exit "${PIPESTATUS[0]}"
