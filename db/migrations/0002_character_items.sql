-- 0002: items a character carries or wears.
--
-- One row per item. Until items can change hands (trading, M3) an item's identity is where it
-- sits; a later migration adds item ids when they need to move between characters.
-- A zone save replaces a character's rows in the same transaction as the character row, so
-- the two can never disagree.

CREATE TABLE character_items (
    character_id  bigint   NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
    -- 1 = inventory (position is the inventory index), 2 = worn (position is the slot id).
    location      smallint NOT NULL CHECK (location IN (1, 2)),
    position      smallint NOT NULL CHECK (position BETWEEN 0 AND 255),
    template_id   integer  NOT NULL CHECK (template_id > 0),   -- content item id (append-only)
    ql            smallint NOT NULL CHECK (ql BETWEEN 1 AND 300),
    PRIMARY KEY (character_id, location, position)
);
