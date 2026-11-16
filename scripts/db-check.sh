#!/usr/bin/env bash
# Applies every migration to an empty database, applies them again (must be a no-op), and
# runs db/checks.sql.
#
# With DATABASE_URL set (CI's Postgres service), uses a fresh database on that server.
# Otherwise starts a throwaway local cluster from the Postgres binaries on this machine.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DB="meridian_check_$$"
source "$ROOT/scripts/lib/scratch-postgres.sh"
trap 'psql "$SERVER_URL" -X -q -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)" || true; scratch_postgres_stop' EXIT
psql "$SERVER_URL" -X -q -c "CREATE DATABASE $DB"
URL="$(scratch_url_for "$DB")"

"$ROOT/scripts/db-migrate.sh" "$URL"
second=$("$ROOT/scripts/db-migrate.sh" "$URL" | tail -1)
[[ "$second" == "migrations: 0 applied,"* ]] || { echo "re-running migrations was not a no-op: $second"; exit 1; }
psql "$URL" -X -q -f "$ROOT/db/checks.sql"
