# Project Meridian

A sci-fi sandbox MMO for Windows in the spirit of the classic 2000s MMOs: deep build
complexity, many distinct professions, a frontier corporate-colony world. The setting,
names and systems are original.

- Design: [Game Design Document](https://claude.ai/artifact/8FGVNSTFDMqDiQZ9AN5WzB)
- Architecture: [Technical Architecture & Roadmap](https://claude.ai/artifact/6owmrpxx4VkgdvmS3wDvvQ)

## Status: M0, M1 and M2 complete

| Gate | Result |
| --- | --- |
| M0: 100 bots in one zone at a steady 20 Hz | **Pass.** 20.00 Hz, p99 tick work 1.1–2.2 ms, 0 overruns, ~7 KB/s per client, every bot at 10 Hz snapshots (2-core box, bots and all services co-located, 60 s) |
| M1: 2 players kill a mob together over the net | **Pass with the real client.** `scripts/m1-gate.sh` runs two headless Godot clients on autopilot; both hit the same skitter and see it die (~21 s). Also covered server-side by `EndToEndTests`. |
| M2: twink a rifle with another player's buff | **Pass.** `scripts/m2-gate.sh`: an Operative cannot wear a QL 1 Kestrel Mk2 alone, can once a Medic's Reflex Boost lands, walks it into a second zone (still worn and active, same connection), and it goes inactive when the buff is dropped (~18 s). |
| Combat load: 100 hunting bots vs 40 mobs | 20.00 Hz, p99 tick work 1.5–3.9 ms, 0 overruns, 1,802 hits and 114 kills in 60 s |

Headroom check, same machine: 300 bots still held 20 Hz with no overruns; p99 tick work
was 10–20 ms, much of it CPU contention from the 300 bot network threads sharing two cores.

## Layout

```
src/
  Meridian.Core/            Rules: stats, modifiers, requirements, Protocol stacking,
                            equipment/twinking, QL curves, combat formulas, movement, RNG.
                            Pure C#, no I/O, no dependencies. Shared by server and client.
  Meridian.Wire/            Wire format: Schema/messages.wire -> Generated/Messages.g.cs,
                            bounds-checked reader/writer, envelopes, direction-split dispatch.
  Meridian.Wire.Gen/        The schema compiler.
  Meridian.Net/             Session encryption (ChaCha20-Poly1305), login tickets, LiteNetLib
                            channel mapping, and the client-side GameConnection.
  Meridian.Persistence/     Accounts and characters: records, the store interface, password
                            hashing, and the in-memory and development-file stores.
  Meridian.Server.Auth/     HTTP accounts, character select/create, and play tickets
                            (session key + sealed single-use ticket).
  Meridian.Server.Gateway/  Client-facing UDP edge: tickets, decryption, schema check,
                            rate limiting, relay to the zone.
  Meridian.Content/         The compiled content pack (embedded) and its JSON mapping.
  Meridian.ContentCompiler/ YAML in content/ -> validated meridian.pack.json.
  Meridian.Server.Zone/     Authoritative 20 Hz simulation (ZoneSimulation), mob AI and
                            auto-attack combat (ZoneSimulation.Combat), and its host.
content/                    Game data as YAML: items, Protocols, mobs, zones. See content/README.md.
  Meridian.ClientState/     The client's world model, engine-free: interpolation, prediction
                            and reconciliation, input pacing, events. Unit-tested.
  Meridian.Bots/            Headless load-test clients.
client/                     Godot 4.7 (.NET) game client. Scripts in client/src draw
                            ClientState; scenes and UI are built in code for reviewable diffs.
tests/                      xunit v3 on Microsoft.Testing.Platform.
db/                         Postgres schema migrations and their checks. See db/README.md.
scripts/m0-gate.sh          Builds, starts the stack, runs bots, checks the gate.
scripts/db-check.sh         Applies the migrations to a throwaway database and checks them.
scripts/m2-gate.sh          M2 gate: twink a rifle with another player's buff and carry it into
                            another zone (two clients, two zones).
deploy/                     Dockerfile and docker-compose for the server stack.
```

## Prerequisites

- .NET 10 SDK (see `global.json`)
- Windows or Linux for development; Linux for production servers

## Common tasks

```bash
dotnet build                                   # warnings are errors
dotnet test                                    # all tests
dotnet run --project src/Meridian.Wire.Gen     # after editing Schema/messages.wire
dotnet run --project src/Meridian.ContentCompiler   # after editing anything in content/
scripts/m0-gate.sh 100 60                      # load test: bots, seconds
scripts/m0-gate.sh 100 60 hunt                 # same, with bots fighting mobs
scripts/db-check.sh                            # migrations against a throwaway Postgres
scripts/m2-gate.sh                             # M2 gate: two clients, two zones
```

Run the stack by hand, each in its own terminal (development keys are used and a warning
is printed when the `MERIDIAN_*_KEY` variables are unset). Auth and zone share a development
store, a JSON file in your local app-data folder (`MERIDIAN_STORE_DIR` moves it), so accounts
and characters survive restarts without a database:

```bash
dotnet run --project src/Meridian.Server.Zone
dotnet run --project src/Meridian.Server.Gateway
dotnet run --project src/Meridian.Server.Auth
dotnet run --project src/Meridian.Bots -- --count 10 --duration 30
```

That runs one zone, Glasswind Flats. To also run Rustfall Basin, start a second zone and tell
the gateway about both (see [Zones](#zones-m2)):

```bash
MERIDIAN_ZONE_KEY=glasswind_flats MERIDIAN_ZONE_PORT=7800 dotnet run --project src/Meridian.Server.Zone
MERIDIAN_ZONE_KEY=rustfall_basin  MERIDIAN_ZONE_PORT=7801 dotnet run --project src/Meridian.Server.Zone
MERIDIAN_ZONES="1@127.0.0.1:7800,2@127.0.0.1:7801" dotnet run --project src/Meridian.Server.Gateway
```

Or with Docker: copy `deploy/.env.example` to `deploy/.env`, set real keys, then
`docker compose -f deploy/docker-compose.yml up --build`.
The compose file has been syntax-checked but not yet run end to end (the build machine
could not pull base images).

## Playing the client (Windows)

1. Install the **.NET 10 SDK** and **Godot 4.7.2, .NET edition** (the standard build cannot run C#).
2. Start the servers (any OS), each in its own terminal:
   `dotnet run --project src/Meridian.Server.Zone`, `...Gateway`, `...Auth`.
3. Open `client/project.godot` in Godot and press Play (F5), or from a shell:
   `godot --path client`.
4. Create an account (any name, a password of 8+ characters), create a character, and press
   Play. To play with a friend, run the servers on a
   machine you both reach and set `MERIDIAN_PUBLIC_GATEWAY_HOST` on auth to its address.

Controls: WASD move, right-drag to look, wheel to zoom, Tab or left-click to target,
Q toggles auto-attack, Esc clears the target, Enter chats. C opens the character window
(train skills with +1/+5), I the inventory (click an item for its requirements against your
stats, then Equip, Unequip, Upload or Discard), and 1-9 cast the Protocols on the bar along the
bottom: on your target if it is a player, otherwise on yourself. Running buffs show under your
health and energy; right-click one to drop it. The chat commands (`/train`, `/inv`, `/cast`
and so on, `/help` lists them) still work.

Headless autoplay (what the M1 gate uses):
`godot --headless --path client -- --autopilot --user Ann --pass autopilot1 --timeout 120`
(registers the account and creates a character named after it on first run)

## How a session works

1. The client logs in to `auth` (`POST /v1/login`; `/v1/register` creates the account) and
   gets a short-lived bearer token and its character list. It can create characters
   (`POST /v1/characters`), then asks to play one (`POST /v1/play`). Auth checks the character
   is the account's and returns a random 32-byte session key and a ticket, sealed with a key
   only auth and gateway share, naming the account and character.
2. The client connects to the gateway over UDP, sending the schema hash and the ticket as
   connection data. The gateway rejects version mismatches, forged/expired tickets and
   reused tickets, with a reason the client can show.
3. Every packet after that is sealed with ChaCha20-Poly1305 under the session key. Each
   transport channel is its own stream with its own counter and 1,024-packet replay window;
   sender role and stream id are part of the nonce.
4. The gateway decrypts, fully decodes each packet against the schema (so only well-formed,
   client-legal messages pass), rate-limits, and forwards to the zone.
5. The zone applies intents, steps at 20 Hz, and sends each player a snapshot of the
   nearest 64 entities within 100 m at 10 Hz, as a delta against the newest snapshot the
   client acknowledged (`SnapshotAck`): unchanged entities cost nothing, moving ones a few
   bytes. Loss costs bandwidth, never correctness. Names and appearance (`EntityInfo`) go once,
   reliably, when an entity first comes into view; snapshots carry only state.

## Combat (M1)

Tab-target auto-attack. A player targets a mob (`SetTarget`) and turns auto-attack on
(`SetAttack`). Each tick the zone counts down every attacker's cooldown; when it reaches zero
and the target is in weapon range, `Formulas.ResolveAttack` rolls hit, damage and crit from
the two characters' stats, and a `CombatEvent` goes to everyone nearby. Rolls come from the
zone's seeded `DeterministicRandom`, so a fight with the same inputs replays byte for byte
(there is a test for it).

Mobs wander near their spawn, attack players who come within aggro range, chase whoever
has generated the most threat, and give up and walk home healed past their leash. While
walking home they are immune (attacks show as "evaded"). Threat equals damage dealt (a miss
counts as 1), scaled by the attacker's `ThreatModifier` stat, which Protocols can raise or
lower. After a kill the corpse stays targeted until the player clears it. Dead mobs
respawn at their spawn point; dead players respawn at a spawn point after 5 s at full health.

## Accounts and saving (M2)

- **Accounts**: usernames are unique ignoring case. Passwords are hashed as self-describing
  PHC strings, so the algorithm can change without a migration: weaker hashes are replaced on
  the next login. New hashes are Argon2id at 64 MiB and 3 passes (libsodium, about 60–90 ms);
  PBKDF2 hashes from before it still verify and are upgraded at the next login. A login for an
  unknown user costs the same as a wrong password. At most one hash per core runs at once
  (each holds its 64 MiB), so a burst of logins queues instead of exhausting memory. Load tests
  lower the cost with `MERIDIAN_ARGON2_MEMORY_KIB` and `MERIDIAN_ARGON2_PASSES` (auth warns).
- **Stores** (`MERIDIAN_STORE`): `file` (default; one JSON file auth and zone share through a
  lock file and atomic replace) or `memory` (one process, tests). The Postgres store implements
  the same contract against `db/migrations`; its contract tests are the ones the other stores pass.
- **What is saved**: level, XP, unspent points, how far each skill was trained above its
  starting value (so content changes to starting values reach existing characters), and zone,
  position and health. Identity (account, name, profession, origin) is fixed at creation; a
  save can never change it.
- **Write-behind**: the zone never waits on the store. A changed character is checkpointed
  every 30 s (staggered), on leave, and on shutdown (SIGTERM included); a background saver
  writes batches. Per character the newest record wins and saves run in order.
- **Versions**: every save names the version it read. If something else saved the character
  meanwhile, the zone stops saving it and drops the player instead of overwriting.
- **Reconnects**: a join reads the saver's unsaved copy before the store's, and the same
  character logging in again takes over the live player and drops the old session.

## Characters (M2)

A character is a profession (Warden, Medic, Operative, Overload so far) and an origin
(Baseline, Nightborn). All of it is content (`content/professions.yaml`, `origins.yaml`,
`progression.yaml`); the rules are `CharacterBuild` in `Meridian.Core`, shared by the
server and the client so the client can show costs and caps without asking.

- **Starting values**: common stats from `new_character.yaml`, then origin attributes, then
  profession starting skills on top.
- **Cost tiers**: every trainable stat is Cheap, Normal, Expensive or Locked for a given
  character. Attributes are priced by origin; skills by profession (a group default with
  per-skill exceptions). Cost per point and cap per level come from the tier.
- **Caps**: starting value + level × tier cap per level. Training checks, in order: valid
  amount, trainable, not locked, under the cap, enough points.
- **Experience**: everyone on a mob's threat list when it dies gets its full XP (no split, so
  grouping never costs anyone). Levelling grants points from the progression curve plus the
  origin's bonus, and heals to the new maximum.

The zone sends a `CharacterSheet` (every stat's base and effective value) on join and
whenever stats, XP or points change; `XpGained` and `Notice` (training refusals) are events.
Profession and origin are chosen when the character is created and never change.

## Items (M2)

An item is a content template at a quality level (QL 1-300); requirements and bonuses
interpolate between the template's QL anchors. The rules are `Outfit` in `Meridian.Core`
(inventory plus worn equipment), shared by zone and client.

- **Equipping swaps**: whatever the new item displaces (its slot, or both hands for a
  two-hander) goes back to the inventory, starting at the position the new item came from.
- **Twinking**: requirements count buffs and other items, never the item's own bonuses, and
  never the bonuses of the item being replaced. An item whose requirement later fails stays
  worn but goes inactive (no bonuses; an inactive weapon cannot be fired, so you fight with
  fists) until it is met again. The zone re-checks worn items whenever stats change and tells
  the player what turned inactive or active.
- **Loot** rolls each of a mob's entries on its death (chance in basis points, QL = mob level
  plus or minus a spread) and goes to the player with the most threat.
- **Saving**: items are rows in `character_items` (inventory position or worn slot, template,
  QL), written with the character in one transaction. A saved twinked item comes back worn but
  inactive if its twink is gone.
- **Messages**: `EquipItem`, `UnequipItem`, `DiscardItem` (intents); `InventoryState` (sent on
  join and on any change). The inventory window (I) or `/inv`, `/equip <#>`,
  `/unequip <slot>`, `/discard <#>` in chat.
- **Development**: with `MERIDIAN_DEV_COMMANDS=1` the zone accepts `/give <item key> <ql>`
  and `/goto <x m> <z m>` (and warns at start). There is no permission model yet, so never on a public server.

## Protocols (M2)

Protocols are the game's buffs (and later nukes, heals and debuffs). The rules are `Lattice`
in `Meridian.Core`: the Protocols a character knows, its energy, and what runs on it.

- **Learning**: each profession starts knowing some (`starting_protocols` in content); more
  come from Protocol items, uploaded with `/upload <#>`. Uploading needs the Protocol's cast
  requirements and consumes the item.
- **Casting** (`CastProtocol`, `/cast <protocol> [me]`) on yourself or another player in range.
  Checks, in order: known, target allowed (some are self-only), alive, range, the caster's
  requirements, energy; then the target's line and lattice-capacity rules. Energy is spent only
  when it lands. Casts are instant for now.
- **Lines**: only the strongest Protocol of a line runs on a target; recasting the same one
  renews it; a weaker one is refused.
- **Energy** regenerates at `Formulas.EnergyRegenBpPerSecond` (2% of maximum per second) and is
  full on login. It travels in `SelfStatus`.
- **Ending**: Protocols expire, can be dropped with `/cancel <protocol>`, and all end on death.
  Worn items are re-checked the same tick, so a rifle twinked with someone's buff goes inactive
  the moment the buff ends.
- **Saving**: known Protocols and running buffs (with their remaining time) are saved with the
  character (`character_protocols`, `character_buffs`). On load buffs go back on before items,
  so a twink held by a saved buff still holds.

The M2 gate, `scripts/m2-gate.sh`, plays this through the real stack with two clients: an
Operative cannot wear a QL 1 Kestrel Mk2 on its own buff, can once a Medic's Reflex Boost
lands, and the rifle goes inactive when that buff is dropped.

## Zones (M2)

Each zone is its own process with its own simulation and tick; the gateway holds one link per
zone and routes each session to the zone that owns it. There are two zones: Glasswind Flats
(id 1, skitters, where new characters start) and Rustfall Basin (id 2, level 6 striders). Each
has an exit pad at its edge (`exits` in content) that leads to the other.

- **Config**: a zone serves `MERIDIAN_ZONE_KEY` (default `glasswind_flats`); the gateway's
  `MERIDIAN_ZONES` is `id@host:port,...` (default: zone 1 only). On connecting, a zone sends
  `ZoneHello` with its id and the gateway drops the link if it is not the zone it expected.
- **Logging in**: the ticket names the zone the character was saved in, and the gateway refuses
  the join with a clear reason if that zone is not up. Losing one zone's link drops only that
  zone's players.
- **Crossing**: when a player steps onto an exit, the zone tells the client (`ZoneChange`),
  takes the player out, and saves the character placed at the arrival point in the other zone.
  Only once that save is confirmed does it ask the gateway to move the session
  (`TransferClient`); the new zone then loads the character from the store as on any login. The
  old zone never saves that character again, so the new zone always loads exactly what left.
  If the save fails or the destination is down, the player is dropped with a message and logs
  back in where the store last had them. Arriving players ignore exits for 2 s, so arrival
  points cannot bounce them back.
- **Same connection**: the client keeps its UDP connection and session key; only the zone
  behind it changes. The gateway ignores anything an old zone still sends for a session it no
  longer owns (`stale` in its metrics), and the client resets its snapshot state on
  `ZoneChange`, since the new zone's ticks are unrelated.
- **What travels**: everything saved: skills, items (twinked ones too), known Protocols and
  running buffs with their remaining time, health. Energy is full on arrival, as on login.

The M2 gate ends with the twinked Operative walking into Rustfall Basin and checking the rifle
is still worn and active, on the same connection. The load bots steer clear of exits so the
single-zone M0 gate measures what it always did. The autopilot's `--travel` walks a real client
across (and with `--screenshot`, saves the pad and the arrival).

## Client UI (M2)

Windows are built in code (`client/src/Ui/`) until there is real UI art. What they show comes
from small view models in `Meridian.ClientState/Views` (stat rows, item details with
requirements checked against your current stats, Protocol buttons, buff countdowns), which are
unit tested; the Godot classes only lay them out. Every button calls the same `GameSession`
method as its chat command, and the server decides: windows refresh when its answer arrives.

The UI is laid out for 1600x900 and scales with the window (Godot's canvas_items stretch; 3D
renders at full resolution). At startup the window opens at 1600x900 times the monitor's scale
(DPI / 96 on Windows), capped to the screen, so a hi-DPI display gets the same apparent size.
`-- --ui-scale 1.25` or `MERIDIAN_UI_SCALE=1.25` makes the UI bigger or smaller (0.5-3);
an explicit `--resolution` is left alone.

The autopilot's `--show-ui` opens the windows, casts a Protocol and presses a real train button;
the M1 gate runs one client that way and requires the server to confirm the point was spent.
With a display (or `xvfb-run`) and `--screenshot`, it also saves a frame of the UI.

## Known limits and next steps

- **The Postgres store is not in yet**: the schema, migration runner and checks are done; the
  development file store stands in and is not meant for production.
- **Casts are instant** and there is no Protocol initiative or cast bar yet; energy is not saved
  (full on login).
- **Items have no ids yet**: an item is identified by where it sits. Trading (M3) adds ids.
- **Characters with no items at all get their kit on join**, so characters created before
  items existed are not left unarmed. Discarding everything therefore brings the kit back.
- **Zones are separate maps joined by pads**: nothing is seen across a zone border, and a
  crossing takes about one save (tens of milliseconds locally) with the world briefly empty.
  There is no zone directory yet: the gateway's zone list is static config.
- **Login sessions live in the auth process's memory**, so restarting auth logs everyone out of
  character select (not out of the game). Fine for one auth instance.
- **Placeholder art**: capsules and a glassy six-legged skitter built from primitives.
- **Prediction blends rather than replays inputs.** Fine without collision; input replay
  arrives with terrain.
- **Some allocation on the hot path**: one small array per snapshot for the delta bytes, and
  the gateway's decode of each zone envelope. Worth removing if profiling at higher player
  counts asks for it.
- **The auth service speaks plain HTTP** for local development. Production must put TLS in
  front of it; session keys must never travel in clear.
- **Only four professions and two origins** are in content; the remaining ids are reserved.
  Asking for one at login is refused.
