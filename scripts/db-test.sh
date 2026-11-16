#!/usr/bin/env bash
# Runs the Postgres store's tests against a real database (the same contract tests the
# in-memory and file stores pass, plus Postgres-only ones), then the M2 gate with every
# service on Postgres: registration, character creation, saves and a zone hand-off.
#
# Migrates a template database, then hands its connection string to the tests in
# MERIDIAN_TEST_DATABASE_URL; each test clones the template (CREATE DATABASE ... TEMPLATE)
# and drops its clone. Without that variable (a plain `dotnet test`) those tests are skipped,
# so this script also fails if any were skipped.
#
# With DATABASE_URL set (CI's Postgres service), uses that server; otherwise a throwaway
# local cluster (see scripts/lib/scratch-postgres.sh).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE="meridian_test_$$"
GATE="meridian_gate_$$"
source "$ROOT/scripts/lib/scratch-postgres.sh"
trap 'for d in $TEMPLATE $GATE; do psql "$SERVER_URL" -X -q -c "DROP DATABASE IF EXISTS $d WITH (FORCE)" || true; done; scratch_postgres_stop' EXIT
psql "$SERVER_URL" -X -q -c "CREATE DATABASE $TEMPLATE"
"$ROOT/scripts/db-migrate.sh" "$(scratch_url_for "$TEMPLATE")"

export MERIDIAN_TEST_DATABASE_URL
MERIDIAN_TEST_DATABASE_URL="$(scratch_url_for "$TEMPLATE")"
OUT="$(mktemp)"
dotnet test "$ROOT/tests/Meridian.Persistence.Tests" -c Release 2>&1 | tee "$OUT"
grep -Eq "skipped: +0\b" "$OUT" || { echo "db-test: some tests were skipped (no database reached them)"; exit 1; }

echo "M2 gate on Postgres..."
psql "$SERVER_URL" -X -q -c "CREATE DATABASE $GATE"
"$ROOT/scripts/db-migrate.sh" "$(scratch_url_for "$GATE")"
MERIDIAN_STORE=postgres MERIDIAN_DATABASE_URL="$(scratch_url_for "$GATE")" "$ROOT/scripts/m2-gate.sh"
psql "$(scratch_url_for "$GATE")" -X -tA -c "SELECT 'saved in Postgres: ' || count(*) || ' accounts, ' || (SELECT count(*) FROM characters) || ' characters, ' || (SELECT count(*) FROM characters WHERE zone = 'rustfall_basin') || ' in rustfall_basin' FROM accounts"
