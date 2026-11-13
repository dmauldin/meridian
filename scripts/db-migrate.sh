#!/usr/bin/env bash
# Applies pending migrations from db/migrations to the database at $1 (a libpq URL or
# connection string). Each file runs in its own transaction together with its row in
# schema_migrations, so a failure leaves the database at the previous version.
#
# Usage: scripts/db-migrate.sh postgres://user@host/db
set -euo pipefail
URL="${1:?usage: db-migrate.sh <database url>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PGOPTIONS="${PGOPTIONS:-} -c client_min_messages=warning"
PSQL=(psql "$URL" -X -q -v ON_ERROR_STOP=1 --no-psqlrc)

"${PSQL[@]}" -c "CREATE TABLE IF NOT EXISTS schema_migrations (
  version integer PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())"

applied=0
for file in "$ROOT"/db/migrations/[0-9][0-9][0-9][0-9]_*.sql; do
  name="$(basename "$file")"
  version=$((10#${name%%_*}))
  done_already=$("${PSQL[@]}" -tA -c "SELECT count(*) FROM schema_migrations WHERE version = $version")
  if [[ "$done_already" == 1 ]]; then continue; fi
  echo "applying $name"
  "${PSQL[@]}" -1 -f "$file" -c "INSERT INTO schema_migrations (version, name) VALUES ($version, '$name')"
  applied=$((applied + 1))
done
echo "migrations: $applied applied, now at version $("${PSQL[@]}" -tA -c 'SELECT coalesce(max(version), 0) FROM schema_migrations')"
