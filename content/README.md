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
