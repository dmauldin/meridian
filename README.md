# Project Meridian

A sci-fi sandbox MMO for Windows in the spirit of the classic 2000s MMOs: deep build
complexity, many distinct professions, a frontier corporate-colony world. The setting,
names and systems are original.

- Design: [Game Design Document](https://claude.ai/artifact/8FGVNSTFDMqDiQZ9AN5WzB)
- Architecture: [Technical Architecture & Roadmap](https://claude.ai/artifact/6owmrpxx4VkgdvmS3wDvvQ)

## Status: M0 and M1 complete; M2 (build loop) in progress

| Gate | Result |
| --- | --- |
| M0: 100 bots in one zone at a steady 20 Hz | **Pass.** 20.00 Hz, p99 tick work 1.1–2.2 ms, 0 overruns, ~7 KB/s per client, every bot at 10 Hz snapshots (2-core box, bots and all services co-located, 60 s) |
| M1: 2 players kill a mob together over the net | **Pass with the real client.** `scripts/m1-gate.sh` runs two headless Godot clients on autopilot; both hit the same skitter and see it die (~21 s). Also covered server-side by `EndToEndTests`. |
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
  Meridian.Server.Auth/     HTTP login -> session key + sealed single-use ticket.
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
scripts/m0-gate.sh          Builds, starts the stack, runs bots, checks the gate.
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
```

Run the stack by hand, each in its own terminal (development keys are used and a warning
is printed when the `MERIDIAN_*_KEY` variables are unset):

```bash
dotnet run --project src/Meridian.Server.Zone
dotnet run --project src/Meridian.Server.Gateway
dotnet run --project src/Meridian.Server.Auth
dotnet run --project src/Meridian.Bots -- --count 10 --duration 30
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
4. Log in with any name and the password `dev`, choosing a profession and origin. To play with a friend, run the servers on a
   machine you both reach and set `MERIDIAN_PUBLIC_GATEWAY_HOST` on auth to its address.

Controls: WASD move, right-drag to look, wheel to zoom, Tab or left-click to target,
Q toggles auto-attack, Esc clears the target, Enter chats. `/train Rifle 10` spends skill
points (a proper training window arrives in M2.5).

Headless autoplay (what the M1 gate uses):
`godot --headless --path client -- --autopilot --user Ann --pass dev --timeout 120`

## How a session works

1. The client posts credentials to `auth` (`POST /v1/login`) and gets back a random 32-byte
   session key and a ticket sealed with a key only auth and gateway share.
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
Until persistence lands (M2.3), the profession and origin are chosen at login and ride in
the sealed ticket; characters are still created fresh on each join.

## Known limits and next steps

- **No persistence yet.** Auth accepts any valid username with the dev password; characters
  are created fresh on join. Postgres and real accounts arrive in M2.
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
- **Single-tick spikes on the shared CI box.** The load gate's 5 s windows hold 100 ticks, so
  their p99 is the second-worst tick; an isolated 8–30 ms pause (seen on this branch and its
  parent alike) can fail a window. Worth computing p99 over the whole run instead.
