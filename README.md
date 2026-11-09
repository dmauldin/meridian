# Project Meridian

A sci-fi sandbox MMO for Windows in the spirit of the classic 2000s MMOs: deep build
complexity, many distinct professions, a frontier corporate-colony world. The setting,
names and systems are original.

- Design: [Game Design Document](https://claude.ai/artifact/8FGVNSTFDMqDiQZ9AN5WzB)
- Architecture: [Technical Architecture & Roadmap](https://claude.ai/artifact/6owmrpxx4VkgdvmS3wDvvQ)

## Status: M0 (Foundations) complete

| Gate | Result |
| --- | --- |
| 100 bots in one zone at a steady 20 Hz | **Pass.** 20.00 Hz, p99 tick work 1.1–2.2 ms, 0 overruns, ~7 KB/s per client, every bot at 10 Hz snapshots (2-core box, bots and all services co-located, 60 s) |

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
  Meridian.Server.Zone/     Authoritative 20 Hz simulation (ZoneSimulation) and its host.
  Meridian.Bots/            Headless load-test clients.
tests/                      xunit v3 on Microsoft.Testing.Platform (151 tests).
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
scripts/m0-gate.sh 100 60                      # load test: bots, seconds
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

## How a session works

1. The client posts credentials to `auth` (`POST /v1/login`) and gets back a random 32-byte
   session key and a ticket sealed with a key only auth and gateway share.
2. The client connects to the gateway over UDP, sending the schema hash and the ticket as
   connection data. The gateway rejects version mismatches, forged/expired tickets and
   reused tickets, with a reason the client can show.
3. Every packet after that is sealed with ChaCha20-Poly1305 under the session key (counter
   nonce, sender role in the nonce, 64-packet replay window).
4. The gateway decrypts, fully decodes each packet against the schema (so only well-formed,
   client-legal messages pass), rate-limits, and forwards to the zone.
5. The zone applies intents, steps at 20 Hz, and sends each player a snapshot of the
   nearest 64 entities within 100 m at 10 Hz.

## Known limits and next steps (M1)

- **No persistence yet.** Auth accepts any valid username with the dev password; characters
  are created fresh on join. Postgres and real accounts arrive in M2.
- **Snapshots are full state**, not deltas against an acknowledged baseline. Fine at ~7 KB/s
  per client in M0; delta compression comes with the Godot client in M1.
- **Allocation on the hot path.** Snapshot arrays and the zone-to-gateway envelope copy the
  payload. Pooled buffers are the first optimisation if profiling at higher counts asks for it.
- **The auth service speaks plain HTTP** for local development. Production must put TLS in
  front of it; session keys must never travel in clear.
- **Skill training costs and caps** (points per level, cost tiers by profession and origin)
  are deferred to M2 with the rest of the build loop.
