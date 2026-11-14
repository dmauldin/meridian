-- Sanity checks run by scripts/db-check.sh after migrating. Each block must raise nothing.
\set ON_ERROR_STOP 1
\o /dev/null

-- Expects the statement to fail with the given SQLSTATE; anything else is a failure.
CREATE FUNCTION pg_temp.expect_error(stmt text, state text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  EXECUTE stmt;
  RAISE EXCEPTION 'expected % from: %', state, stmt;
EXCEPTION WHEN OTHERS THEN
  IF SQLSTATE <> state THEN RAISE; END IF;
END $$;

BEGIN;
INSERT INTO accounts (username, password_hash) VALUES ('Alice', '$pbkdf2-sha256$i=1$c2FsdA$aGFzaGhhc2hoYXNo');
SELECT pg_temp.expect_error($q$INSERT INTO accounts (username, password_hash) VALUES ('alice', '$pbkdf2-sha256$i=1$c2FsdA$aGFzaGhhc2hoYXNo')$q$, '23505'); -- case-insensitive unique
SELECT pg_temp.expect_error($q$INSERT INTO accounts (username, password_hash) VALUES ('a b', '$pbkdf2-sha256$i=1$c2FsdA$aGFzaGhhc2hoYXNo')$q$, '23514');   -- pattern
SELECT pg_temp.expect_error($q$INSERT INTO accounts (username, password_hash) VALUES ('Bobby', 'short')$q$, '23514');

INSERT INTO characters (account_id, name, profession, origin, level, xp, unspent_points, skills, zone, pos_x, pos_y, pos_z, health)
SELECT id, 'Rook', 3, 1, 1, 0, 60, '{"71": 30}', 'glasswind_flats', 0, 0, 0, 108 FROM accounts WHERE username = 'Alice';
SELECT pg_temp.expect_error($q$INSERT INTO characters (account_id, name, profession, origin, level, xp, unspent_points, zone, pos_x, pos_y, pos_z, health)
  SELECT id, 'ROOK', 3, 1, 1, 0, 60, 'z', 0, 0, 0, 1 FROM accounts$q$, '23505');
SELECT pg_temp.expect_error($q$UPDATE characters SET skills = '[1]'$q$, '23514');
SELECT pg_temp.expect_error($q$UPDATE characters SET pos_x = NULL$q$, '23514'); -- placement is all or nothing
UPDATE characters SET zone = NULL, pos_x = NULL, pos_y = NULL, pos_z = NULL, health = NULL;  -- unplaced is fine
SELECT pg_temp.expect_error($q$UPDATE characters SET unspent_points = -1$q$, '23514');

DO $$ BEGIN
  IF (SELECT version FROM characters WHERE name = 'Rook') <> 1 THEN RAISE EXCEPTION 'version should start at 1'; END IF;
END $$;

INSERT INTO character_items (character_id, location, position, template_id, ql)
SELECT id, 2, 3, 1001, 1 FROM characters WHERE name = 'Rook';
SELECT pg_temp.expect_error($q$INSERT INTO character_items SELECT id, 2, 3, 1002, 1 FROM characters WHERE name = 'Rook'$q$, '23505'); -- one item per slot
SELECT pg_temp.expect_error($q$INSERT INTO character_items SELECT id, 3, 0, 1001, 1 FROM characters WHERE name = 'Rook'$q$, '23514');  -- location
SELECT pg_temp.expect_error($q$INSERT INTO character_items SELECT id, 1, 0, 1001, 301 FROM characters WHERE name = 'Rook'$q$, '23514'); -- ql

-- Deleting an account deletes its characters.
DELETE FROM accounts WHERE username = 'Alice';
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM characters) THEN RAISE EXCEPTION 'characters should cascade'; END IF;
  IF EXISTS (SELECT 1 FROM character_items) THEN RAISE EXCEPTION 'items should cascade'; END IF;
END $$;
ROLLBACK;

\o
\echo 'db checks: PASS'
