-- 0001: accounts and characters.
--
-- Rules for every migration (see db/README.md):
--   * Never edit a migration once merged; add a new one.
--   * Ids stored here (profession, origin, stat ids inside skills) are the same append-only
--     ids as the wire and content; never renumber them.

CREATE TABLE accounts (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username       text        NOT NULL CHECK (username ~ '^[A-Za-z][A-Za-z0-9]{2,23}$'),
    -- PHC-format string, e.g. $argon2id$v=19$m=...,t=...,p=...$salt$hash. The algorithm is in
    -- the string, so hashes upgrade on login without a migration.
    password_hash  text        NOT NULL CHECK (length(password_hash) BETWEEN 20 AND 512),
    created_at     timestamptz NOT NULL DEFAULT now(),
    last_login_at  timestamptz
);
-- Usernames are unique ignoring case; the account keeps the spelling it registered with.
CREATE UNIQUE INDEX accounts_username_key ON accounts (lower(username));

CREATE TABLE characters (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id      bigint      NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
    name            text        NOT NULL CHECK (name ~ '^[A-Za-z][A-Za-z0-9]{2,23}$'),
    profession      smallint    NOT NULL CHECK (profession > 0),
    origin          smallint    NOT NULL CHECK (origin > 0),
    level           integer     NOT NULL CHECK (level >= 1),
    xp              bigint      NOT NULL CHECK (xp >= 0),
    unspent_points  integer     NOT NULL CHECK (unspent_points >= 0),
    -- Trained base values by stat id: {"71": 35, ...}. Only trainable stats; everything else
    -- is derived from content on load.
    skills          jsonb       NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(skills) = 'object'),
    -- Where the character was last saved. All NULL until a zone first places it (a new
    -- character spawns at a spawn point with full health).
    zone            text,
    pos_x           integer,                -- millimetres
    pos_y           integer,
    pos_z           integer,
    health          integer,                -- <= 0 means dead: respawn on load
    -- Optimistic concurrency: a save names the version it read and bumps it. A zone that
    -- loses ownership of a character finds out on its next save instead of overwriting.
    version         bigint      NOT NULL DEFAULT 1,
    created_at      timestamptz NOT NULL DEFAULT now(),
    saved_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT characters_placement_all_or_nothing CHECK (
        num_nulls(zone, pos_x, pos_y, pos_z, health) IN (0, 5))
);
CREATE UNIQUE INDEX characters_name_key ON characters (lower(name));
CREATE INDEX characters_account ON characters (account_id);
