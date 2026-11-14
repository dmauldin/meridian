#!/usr/bin/env bash
# Applies every migration to an empty database, applies them again (must be a no-op), and
# runs db/checks.sql.
#
# With DATABASE_URL set (CI's Postgres service), uses a fresh database on that server.
# Otherwise starts a throwaway local cluster from the Postgres binaries on this machine.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DB="meridian_check_$$"

if [[ -n "${DATABASE_URL:-}" ]]; then
  psql "$DATABASE_URL" -X -q -c "CREATE DATABASE $DB"
  trap 'psql "$DATABASE_URL" -X -q -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)"' EXIT
  URL="${DATABASE_URL%/*}/$DB"
else
  BIN="$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1)"
  [[ -x "$BIN/initdb" ]] || { echo "no Postgres binaries found; set DATABASE_URL"; exit 1; }
  DATA="$(mktemp -d)"
  RUN=()
  if [[ "$(id -u)" == 0 ]]; then chown postgres "$DATA"; RUN=(runuser -u postgres --); fi
  PORT=$((20000 + $$ % 10000))
  "${RUN[@]}" "$BIN/initdb" -D "$DATA" -U postgres -A trust >/dev/null
  "${RUN[@]}" "$BIN/pg_ctl" -D "$DATA" -o "-p $PORT -k $DATA -c listen_addresses=''" -w start >/dev/null
  trap '"${RUN[@]}" "$BIN/pg_ctl" -D "$DATA" -m immediate stop >/dev/null; rm -rf "$DATA"' EXIT
  psql "host=$DATA port=$PORT user=postgres dbname=postgres" -X -q -c "CREATE DATABASE $DB"
  URL="host=$DATA port=$PORT user=postgres dbname=$DB"
fi

"$ROOT/scripts/db-migrate.sh" "$URL"
second=$("$ROOT/scripts/db-migrate.sh" "$URL" | tail -1)
[[ "$second" == "migrations: 0 applied,"* ]] || { echo "re-running migrations was not a no-op: $second"; exit 1; }
psql "$URL" -X -q -f "$ROOT/db/checks.sql"
