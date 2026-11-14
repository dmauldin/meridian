-- 0003: Protocols a character knows, and the ones running on it.
--
-- Known Protocols are permanent (uploading consumes the item). Running buffs are saved with
-- their remaining time so logging out does not strip them; the zone re-applies them through
-- the normal line and capacity rules on load. Both are replaced with the character row in one
-- transaction on every save.

CREATE TABLE character_protocols (
    character_id  bigint  NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
    protocol_id   integer NOT NULL CHECK (protocol_id > 0),   -- content Protocol id (append-only)
    PRIMARY KEY (character_id, protocol_id)
);

CREATE TABLE character_buffs (
    character_id  bigint  NOT NULL REFERENCES characters (id) ON DELETE CASCADE,
    protocol_id   integer NOT NULL CHECK (protocol_id > 0),
    remaining_ms  integer NOT NULL CHECK (remaining_ms > 0),
    PRIMARY KEY (character_id, protocol_id)
);
