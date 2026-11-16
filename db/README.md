# Database

Postgres 16+. Schema changes are versioned SQL files in `migrations/`, applied in order by
file name. The applied versions are recorded in `schema_migrations`.

- Never edit a migration after it is merged; add a new one (`0002_...sql`).
- Each file runs in one transaction, so a failed migration leaves nothing half-applied.
- Ids stored in the database (profession, origin, stat ids) are the same append-only ids as
  the wire format and content.

`scripts/db-check.sh` creates a throwaway cluster, applies every migration, applies them
again to prove the runner skips what is done, and runs `db/checks.sql` (constraint and index
sanity). CI runs it on every push.

`scripts/db-test.sh` migrates a template database and runs the Postgres store's tests, each in
a fresh clone of it (`CREATE DATABASE ... TEMPLATE`), then runs the M2 gate with every service
on Postgres. A plain `dotnet test` skips the Postgres tests; this script fails if any were
skipped. Both scripts use `DATABASE_URL` when set (CI's Postgres service) and otherwise start a
throwaway local cluster (`scripts/lib/scratch-postgres.sh`). When a migration is added, bump
`PostgresGameStore.SchemaVersion`; a test checks it matches the newest file.
