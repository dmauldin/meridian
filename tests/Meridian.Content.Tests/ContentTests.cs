using Meridian.Content;
using Meridian.ContentCompiler;
using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;

namespace Meridian.Content.Tests;

internal static class Repo
{
    public static string Root { get; } = Find();

    public static string Content => Path.Combine(Root, "content");

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Meridian.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

public class PackTests
{
    [Fact]
    public void The_real_content_compiles_cleanly()
    {
        var result = Compiler.Compile(Repo.Content);
        Assert.Empty(result.Errors);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void The_committed_pack_is_up_to_date()
    {
        var result = Compiler.Compile(Repo.Content);
        var committed = File.ReadAllText(Path.Combine(Repo.Content, "meridian.pack.json"));
        Assert.True(result.Json == committed, "content/meridian.pack.json is stale. Run: dotnet run --project src/Meridian.ContentCompiler");
    }

    [Fact]
    public void The_embedded_pack_is_the_committed_one()
    {
        var committed = File.ReadAllText(Path.Combine(Repo.Content, "meridian.pack.json"));
        Assert.Equal(ContentPacks.HashOf(committed), ContentPacks.Embedded.Hash);
    }

    [Fact]
    public void Parse_then_serialize_reproduces_the_same_bytes()
    {
        var json = File.ReadAllText(Path.Combine(Repo.Content, "meridian.pack.json"));
        Assert.Equal(json, ContentPacks.Serialize(ContentPacks.ToDto(ContentPacks.Parse(json))));
    }

    [Fact]
    public void Hash_ignores_line_ending_style_only()
    {
        var json = File.ReadAllText(Path.Combine(Repo.Content, "meridian.pack.json"));
        Assert.Equal(ContentPacks.HashOf(json), ContentPacks.HashOf(json.Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.NotEqual(ContentPacks.HashOf(json), ContentPacks.HashOf(json.Replace("Kestrel", "Kestral", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_starter_carbine_matches_the_M1_numbers_at_QL_1()
    {
        var w = ContentPacks.Embedded.Items["rifle.combine_carbine"].WeaponAt(1)!;
        Assert.Equal((8, 20, 1_500, 25_000), (w.MinDamage, w.MaxDamage, w.BaseAttackTimeMs, w.RangeMm));
        Assert.Equal(Stat.Rifle, w.AttackSkill);
    }

    [Fact]
    public void Items_resolve_requirements_and_effects_at_a_quality_level()
    {
        var kestrel = ContentPacks.Embedded.Items["rifle.kestrel_mk2"];
        var req = (AllOf)kestrel.RequirementAt(150);
        // 40 + (1300 - 40) * 149 / 299 = 667.89, rounded half away from zero.
        Assert.Contains(req.Clauses, c => c is StatAtLeast { Stat: Stat.Rifle, Minimum: 668 });
        Assert.Contains(req.Clauses, c => c is ProfessionIn p && p.Allowed.Contains(Profession.Operative));

        var gloves = ContentPacks.Embedded.Items["armor.marksman_gloves"].Resolve(42, 300);
        Assert.Contains(gloves.Effects, e => e.Stat == Stat.Rifle && e.Value == 80);
        Assert.Equal(42, gloves.InstanceId);
    }

    [Fact]
    public void Protocols_convert_durations_to_ticks_and_keep_their_line()
    {
        var p = ContentPacks.Embedded.Protocols["buff.rifle_enhancement_2"];
        var def = p.ToDef(tickMs: 50);
        Assert.Equal(12_000, def.DurationTicks); // 600 s at 20 Hz
        Assert.Equal(ContentPacks.Embedded.Lines["rifle_enhancement"].Id, def.Line.Id);
        Assert.Equal(2, def.Strength);
    }
}

public class ValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "meridian-content-" + Guid.NewGuid().ToString("N"));

    public ValidationTests()
    {
        foreach (var file in Directory.EnumerateFiles(Repo.Content, "*.yaml", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_dir, Path.GetRelativePath(Repo.Content, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private IReadOnlyList<string> CompileWith(string file, string find, string replace)
    {
        var path = Path.Combine(_dir, file);
        var text = File.ReadAllText(path);
        Assert.Contains(find, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));
        var result = Compiler.Compile(_dir);
        Assert.False(result.Succeeded);
        return result.Errors;
    }

    [Theory]
    [InlineData("items/weapons.yaml", "attack_skill: Rifle", "attack_skill: Rifel", "not a valid Stat")]
    [InlineData("items/weapons.yaml", "id: 1002", "id: 1001", "Duplicate item id 1001")]
    [InlineData("items/weapons.yaml", "max_damage: { 1: 30, 300: 700 }", "max_damage: { 1: 10, 300: 700 }", "need 0 <= min <= max")]
    [InlineData("items/weapons.yaml", "slot: TwoHand\n  ql: [1, 300]\n  professions", "slot: Hands\n  ql: [1, 300]\n  professions", "hand slot")]
    [InlineData("items/weapons.yaml", "range_m: 25", "rnage_m: 25", "rnage_m")]
    [InlineData("items/armor.yaml", "ql: [1, 300]", "ql: [1, 400]", "ql must be")]
    [InlineData("items/armor.yaml", "{ stat: Rifle, flat:", "{ stat: Rifle, percent: { 1: 1 }, flat:", "exactly one of")]
    [InlineData("protocols/buffs.yaml", "line: reflex_boost", "line: reflex_bost", "is not defined")]
    [InlineData("protocols/buffs.yaml", "target: self", "target: everyone", "target must be")]
    [InlineData("protocols/buffs.yaml", "professions: [Medic]", "professions: [Medik]", "not a valid Profession")]
    [InlineData("mobs/creatures.yaml", "aggro_m: 8", "aggro_m: 80", "aggro_m cannot exceed leash_m")]
    [InlineData("zones/glasswind_flats.yaml", "mob: mob.glasswind_skitter", "mob: mob.glasswind_skiter", "unknown mob")]
    [InlineData("zones/glasswind_flats.yaml", "outer: 120", "outer: 2500", "outside the zone bounds")]
    [InlineData("professions.yaml", "item: blunt.shock_maul", "item: armor.marksman_gloves", "is not a weapon")]
    [InlineData("professions.yaml", "starting_skills: { BodyDevelopment: 15", "starting_skills: { Strength: 5, BodyDevelopment: 15", "not a profession skill")]
    [InlineData("professions.yaml", "group_costs: { Body: cheap", "group_costs: { Body: chep", "cost tier must be")]
    [InlineData("professions.yaml", "profession: Medic", "profession: Healer", "not a valid Profession")]
    [InlineData("origins.yaml", "attribute_costs: { Intellect: cheap", "attribute_costs: { Rifle: cheap, Intellect: cheap", "is not an attribute")]
    [InlineData("progression.yaml", "max_level: 30", "max_level: 900", "max_level must be")]
    [InlineData("progression.yaml", "expensive: { cost_per_point: 4", "expensive: { cost_per_point: 0", "positive cost and cap")]
    public void Each_rule_catches_its_mistake(string file, string find, string replace, string expected)
    {
        var errors = CompileWith(file, find, replace);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Errors_name_the_file_and_entry()
    {
        var errors = CompileWith("items/weapons.yaml", "attack_skill: Rifle", "attack_skill: Rifel");
        Assert.Contains(errors, e => e.Contains("weapons.yaml", StringComparison.Ordinal) && e.Contains("rifle.combine_carbine", StringComparison.Ordinal));
    }

    [Fact]
    public void Numeric_enum_values_are_rejected()
    {
        var errors = CompileWith("items/armor.yaml", "slot: Hands", "slot: 13");
        Assert.Contains(errors, e => e.Contains("not a valid EquipSlot", StringComparison.Ordinal));
    }

    [Fact]
    public void All_errors_are_reported_in_one_run()
    {
        var weapons = Path.Combine(_dir, "items/weapons.yaml");
        File.WriteAllText(weapons, File.ReadAllText(weapons).Replace("attack_skill: Rifle", "attack_skill: Nope", StringComparison.Ordinal));
        var errors = CompileWith("protocols/buffs.yaml", "line: reflex_boost", "line: nope");
        Assert.True(errors.Count >= 3, string.Join("\n", errors)); // two weapons + one protocol
    }
}
