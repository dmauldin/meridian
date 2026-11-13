using Meridian.ContentCompiler;

// Usage: dotnet run --project src/Meridian.ContentCompiler [-- <repo root>] [--check]
//   --check  exit 1 if content/meridian.pack.json is stale instead of rewriting it (for CI).
var check = args.Contains("--check");
var root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? FindRepoRoot();
var contentDir = Path.Combine(root, "content");
var packPath = Path.Combine(contentDir, "meridian.pack.json");

var result = Compiler.Compile(contentDir);
if (!result.Succeeded)
{
    foreach (var error in result.Errors)
    {
        Console.Error.WriteLine(error);
    }
    Console.Error.WriteLine($"{result.Errors.Count} content error(s).");
    return 2;
}

var existing = File.Exists(packPath) ? File.ReadAllText(packPath) : null;
if (existing == result.Json)
{
    Console.WriteLine($"Up to date: {packPath}");
    return 0;
}
if (check)
{
    Console.Error.WriteLine($"Stale: {packPath}. Run: dotnet run --project src/Meridian.ContentCompiler");
    return 1;
}
File.WriteAllText(packPath, result.Json);
Console.WriteLine($"Wrote {packPath}");
return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Meridian.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repo or pass its path.");
}
