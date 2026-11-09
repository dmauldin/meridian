using Meridian.Wire.Gen;

// Usage: dotnet run --project src/Meridian.Wire.Gen [-- <repo root>] [--check]
//   --check  exit 1 if the generated file is stale instead of rewriting it (for CI).
var check = args.Contains("--check");
var root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? FindRepoRoot();
var schemaPath = Path.Combine(root, "src", "Meridian.Wire", "Schema", "messages.wire");
var outputPath = Path.Combine(root, "src", "Meridian.Wire", "Generated", "Messages.g.cs");

try
{
    var generated = Generator.Generate(File.ReadAllText(schemaPath));
    var existing = File.Exists(outputPath) ? File.ReadAllText(outputPath) : null;
    if (existing == generated)
    {
        Console.WriteLine($"Up to date: {outputPath}");
        return 0;
    }
    if (check)
    {
        Console.Error.WriteLine($"Stale: {outputPath}. Run: dotnet run --project src/Meridian.Wire.Gen");
        return 1;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, generated);
    Console.WriteLine($"Wrote {outputPath}");
    return 0;
}
catch (InvalidDataException e)
{
    Console.Error.WriteLine($"{schemaPath}: {e.Message}");
    return 2;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Meridian.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repo or pass its path.");
}
