-- 0004: credits, the currency (M3).
--
-- One balance per character, saved with the character row on every save like its XP. The cap
-- matches EconomyDef.MaxCredits (10^12): far beyond play, far from overflowing a bigint, and
-- a backstop if a bug ever tried to save more. Characters from before this start at 0.

ALTER TABLE characters
    ADD COLUMN credits bigint NOT NULL DEFAULT 0
        CONSTRAINT characters_credits_range CHECK (credits BETWEEN 0 AND 1000000000000);
