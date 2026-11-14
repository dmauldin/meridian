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
