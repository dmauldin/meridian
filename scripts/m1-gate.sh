#!/usr/bin/env bash
# M1 gate: two players kill a mob together over the network, using the real Godot client.
#
# Starts auth, zone and gateway, then runs two headless Godot clients on autopilot. Each logs
# in, targets the mob nearest the zone origin (so both pick the same one), walks into range and
# auto-attacks. Passes when both clients see that mob die and both landed hits on it.
#
# Usage: scripts/m1-gate.sh [timeout_seconds=150]
# Needs: .NET 10 SDK, Godot 4.7.2 .NET build on PATH as `godot` (or set GODOT=/path/to/godot).
set -euo pipefail

TIMEOUT="${1:-150}"
GODOT="${GODOT:-godot}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/m1-gate"
BIN="bin/Release/net10.0"

mkdir -p "$OUT"
rm -f "$OUT"/*.log

echo "building servers (Release) and client..."
dotnet build "$ROOT/Meridian.slnx" -c Release -v quiet -nologo >"$OUT/build.log" 2>&1 || { cat "$OUT/build.log"; exit 1; }
dotnet build "$ROOT/client/Meridian.Client.csproj" -v quiet -nologo >>"$OUT/build.log" 2>&1 || { cat "$OUT/build.log"; exit 1; }
# First run on a fresh checkout: let Godot import the project once.
"$GODOT" --headless --path "$ROOT/client" --import >>"$OUT/build.log" 2>&1 || true

pids=()
cleanup() { for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done; wait 2>/dev/null || true; }
trap cleanup EXIT

dotnet "$ROOT/src/Meridian.Server.Zone/$BIN/Meridian.Server.Zone.dll" >"$OUT/zone.log" 2>&1 & pids+=($!)
dotnet "$ROOT/src/Meridian.Server.Gateway/$BIN/Meridian.Server.Gateway.dll" >"$OUT/gateway.log" 2>&1 & pids+=($!)
dotnet "$ROOT/src/Meridian.Server.Auth/$BIN/Meridian.Server.Auth.dll" >"$OUT/auth.log" 2>&1 & pids+=($!)

for _ in $(seq 1 60); do
  if curl -fs http://127.0.0.1:5080/healthz >/dev/null 2>&1 && grep -q "connected to zone" "$OUT/gateway.log"; then break; fi
  sleep 0.5
done
grep -q "connected to zone" "$OUT/gateway.log" || { echo "gateway never reached the zone"; tail "$OUT"/*.log; exit 1; }

echo "running two headless clients on autopilot..."
run_client() {
  "$GODOT" --headless --path "$ROOT/client" -- --autopilot --user "$1" --pass dev --timeout "$TIMEOUT" >"$OUT/client-$1.log" 2>&1
}
set +e
run_client Ann & a=$!
run_client Bram & b=$!
wait "$a"; a_exit=$?
wait "$b"; b_exit=$?
set -e

for who in Ann Bram; do
  echo "--- $who"
  grep -E "AUTOPILOT|autopilot" "$OUT/client-$who.log" || tail -5 "$OUT/client-$who.log"
done

quarry_a=$(grep -oP 'AUTOPILOT KILL quarry=\K[0-9]+' "$OUT/client-Ann.log" || true)
quarry_b=$(grep -oP 'AUTOPILOT KILL quarry=\K[0-9]+' "$OUT/client-Bram.log" || true)

echo
if [[ "$a_exit" == 0 && "$b_exit" == 0 && -n "$quarry_a" && "$quarry_a" == "$quarry_b" ]]; then
  echo "M1 GATE: PASS (both clients hit and saw mob $quarry_a die)"
  exit 0
fi
echo "M1 GATE: FAIL (exit codes $a_exit/$b_exit, quarries '$quarry_a'/'$quarry_b'); logs in $OUT"
exit 1
