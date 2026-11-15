# Content

Game data as YAML. After editing, compile and commit the pack:

    dotnet run --project src/Meridian.ContentCompiler

The compiler validates everything (unique ids and keys, references, stat names, QL curves,
damage ranges) and writes `meridian.pack.json`, which is embedded in `Meridian.Content` and
loaded by the zone, bots and game client. A test fails if the committed pack is stale.

Rules:
- `id` is a stable number used on the wire and in the database. Append; never renumber or reuse.
- `key` is a stable text id used for references between files.
- Distances are metres, times are milliseconds or seconds as the field name says.
- QL curves are maps of quality level to value, e.g. `{ 1: 8, 300: 180 }`; values in between
  are interpolated (see `QlCurve`).
- Stat names are the `Stat` enum names, e.g. `Rifle`, `KineticFlux`, `MaxHealth`.

## Characters

- `progression.yaml`: level cap, starting skill points, XP to next level and points per level
  (both QL curves keyed by level), and the cost tiers (`cheap`, `normal`, `expensive`), each
  with `cost_per_point` and `cap_per_level`. A tier left out of a character's costs is `normal`.
- `professions.yaml`: one entry per playable profession (`profession:` is the `Profession`
  enum name). `group_costs` sets a tier per skill group (`Body`, `Melee`, `Ranged`,
  `Initiative`, `Defence`, `Lattice`, `Tech`); `skill_costs` overrides single skills; `locked`
  makes a skill untrainable. `starting_skills` are added to `new_character.yaml`'s values, and
  `starting_weapon` is the kit weapon.
- `origins.yaml`: starting attributes, attribute cost tiers and `bonus_points_per_level`.
  Attributes are priced only by origin, never by profession.
- `mobs`: `xp` is what each player on the mob's threat list earns when it dies. `loot` lists
  possible drops, each rolled on its own: `item` (key), `chance` (basis points, 1..10000) and
  `ql_spread` (QL = mob level plus or minus this, clamped to the item's range).
- `protocols/buffs.yaml`: `range_m` is how far the target may be from the caster (1-100);
  `requirements` and `professions` gate both casting and uploading.
- `items/protocols.yaml`: Protocol items have `protocol:` (the Protocol key) and no `slot`.
- `professions.yaml`: `starting_protocols` lists Protocol keys every new character of that
  profession knows; each must be castable by the profession.
- `new_character.yaml`: `unarmed` is the weapon item used when no active weapon is worn;
  `inventory_slots` is how many items a character carries besides what it wears.
