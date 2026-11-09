using Meridian.Core.Characters;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;

namespace Meridian.Core.Tests;

public class RequirementTests
{
    private static Character Operative(int rifle, int agility)
    {
        var c = new Character(Profession.Operative, Origin.Splice);
        c.Stats.SetBase(Stat.Rifle, rifle);
        c.Stats.SetBase(Stat.Agility, agility);
        return c;
    }

    private static readonly Requirement RifleReq = Requirement.All(
        Requirement.AtLeast(Stat.Rifle, 420),
        Requirement.AtLeast(Stat.Agility, 210),
        Requirement.Professions(Profession.Operative, Profession.Trooper));

    [Fact]
    public void Met_requirement_has_no_failures()
    {
        var c = Operative(420, 210);
        Assert.True(RifleReq.Evaluate(c));
        Assert.Empty(RifleReq.Explain(c));
    }

    [Fact]
    public void All_reports_every_unmet_clause_not_just_the_first()
    {
        var c = Operative(398, 150);
        var failures = RifleReq.Explain(c);
        Assert.Equal(
            [new StatShortfall(Stat.Rifle, 398, 420), new StatShortfall(Stat.Agility, 150, 210)],
            failures);
        Assert.Equal("Rifle 398 / 420", failures[0].ToString());
        Assert.Equal(22, ((StatShortfall)failures[0]).Missing);
    }

    [Fact]
    public void Profession_lock_is_reported()
    {
        var medic = new Character(Profession.Medic, Origin.Baseline);
        medic.Stats.SetBase(Stat.Rifle, 999);
        medic.Stats.SetBase(Stat.Agility, 999);
        var failure = Assert.Single(RifleReq.Explain(medic));
        Assert.IsType<ProfessionNotAllowed>(failure);
    }

    [Fact]
    public void Any_passes_when_one_alternative_holds()
    {
        var req = Requirement.Any(Requirement.AtLeast(Stat.Pistol, 300), Requirement.AtLeast(Stat.Rifle, 300));
        Assert.True(req.Evaluate(Operative(300, 0)));
    }

    [Fact]
    public void Any_reports_failures_grouped_per_alternative()
    {
        var req = Requirement.Any(Requirement.AtLeast(Stat.Pistol, 300), Requirement.AtLeast(Stat.Rifle, 300));
        var failure = Assert.IsType<NoAlternativeMet>(Assert.Single(req.Explain(Operative(10, 0))));
        Assert.Equal(2, failure.Alternatives.Count);
        Assert.Equal(new StatShortfall(Stat.Rifle, 10, 300), Assert.Single(failure.Alternatives[1]));
    }

    [Fact]
    public void Buffs_count_toward_requirements()
    {
        var c = Operative(400, 210);
        Assert.False(RifleReq.Evaluate(c));
        c.Stats.AddModifier(Modifier.Flat(Stat.Rifle, 20, new ModifierSource(SourceKind.Protocol, 1)));
        Assert.True(RifleReq.Evaluate(c));
    }

    [Fact]
    public void None_always_passes()
    {
        Assert.True(Requirement.None.Evaluate(new Character(Profession.Warden, Origin.Heavyworlder)));
    }

    [Fact]
    public void ToString_reads_like_the_design_doc()
    {
        Assert.Equal("All(Rifle >= 420, Agility >= 210, Profession in {Trooper, Operative})", RifleReq.ToString());
    }
}
