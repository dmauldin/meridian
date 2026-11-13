# Working in this repo

Project Meridian: an original sci-fi MMO. Godot 4.7 (C#) client, .NET 10 server on Linux,
shared rules in `Meridian.Core`. Specs:
- Design: https://claude.ai/artifact/8FGVNSTFDMqDiQZ9AN5WzB
- Architecture & roadmap: https://claude.ai/artifact/6owmrpxx4VkgdvmS3wDvvQ

The owner is a senior engineer who reviews every change. Optimise for reviewability.

## Rules

- Never use names, lore or assets from Anarchy Online / Funcom. Everything is original.
- `Meridian.Core` stays pure: no I/O, no engine types, no package dependencies, integer math
  only in rules, deterministic given the same inputs and `DeterministicRandom` seed.
- Stat, message and enum ids are wire and database ids: append, never renumber or reuse.
- Edit `src/Meridian.Wire/Schema/messages.wire`, then run
  `dotnet run --project src/Meridian.Wire.Gen`. Never hand-edit `Generated/Messages.g.cs`;
  a test fails if it is stale.
- Game data lives in `content/*.yaml`. After editing it, run
  `dotnet run --project src/Meridian.ContentCompiler` and commit `content/meridian.pack.json`;
  a test fails if it is stale. Content ids are append-only like wire ids. Never put tuning
  numbers for items, Protocols or mobs in C#.
- Everything from a client is hostile: bound every length, reject trailing bytes, never
  trust results, only intents.
- Tuning numbers live in `StatCatalog.Default` and `Formulas`; changing one must change a
  golden test in the same commit.
- Warnings are errors. Keep `dotnet build` and `dotnet test` green on every commit.
- Small commits with a body explaining why. Run `scripts/m0-gate.sh 100 60` after any change
  to the zone, gateway or wire format, and quote the numbers in the commit.

## Naming

- "Protocol" means an in-game ability (buffs, nukes). The network format is "Wire".
- Positions are millimetres (i32). Percentages are basis points (100 = 1%).
