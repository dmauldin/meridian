using Meridian.Core.Characters;
using Meridian.Core.Combat;
using Meridian.Core.Items;
using Meridian.Core.Stats;

namespace Meridian.Core.Content;

/// <summary>What one cost tier means in skill points.</summary>
/// <param name="CostPerPoint">Skill points spent per point of the stat.</param>
/// <param name="CapPerLevel">How far above its starting value the stat may be trained, per level.</param>
public sealed record TierRule(int CostPerPoint, int CapPerLevel);

/// <summary>Levelling and skill-point economy.</summary>
/// <param name="XpToNext">Experience needed to go from level L to L+1, as a curve over L.</param>
/// <param name="PointsPerLevel">Skill points granted on reaching level L, as a curve over L.</param>
public sealed record ProgressionDef(
    int MaxLevel,
    QlCurve XpToNext,
    QlCurve PointsPerLevel,
    int StartingPoints,
    IReadOnlyDictionary<CostTier, TierRule> Tiers);

/// <summary>A profession's skill pricing and starting kit.</summary>
/// <param name="GroupTiers">Price of each skill group; groups not listed are Normal.</param>
/// <param name="SkillTiers">Per-skill exceptions to <paramref name="GroupTiers"/>.</param>
/// <param name="StartingSkills">Added to the common starting stats.</param>
public sealed record ProfessionDef(
    Profession Id,
    string Key,
    string Name,
    IReadOnlyDictionary<SkillGroup, CostTier> GroupTiers,
    IReadOnlyDictionary<Stat, CostTier> SkillTiers,
    IReadOnlyDictionary<Stat, int> StartingSkills,
    string StartingWeaponKey,
    int StartingWeaponQl);

/// <summary>An origin (breed): attributes, their prices, and bonus points.</summary>
/// <param name="Attributes">Starting attribute values (replace the common ones).</param>
/// <param name="AttributeTiers">Price of raising each attribute; unlisted attributes are Normal.</param>
public sealed record OriginDef(
    Origin Id,
    string Key,
    string Name,
    IReadOnlyDictionary<Stat, int> Attributes,
    IReadOnlyDictionary<Stat, CostTier> AttributeTiers,
    int BonusPointsPerLevel);

/// <summary>Why training was refused.</summary>
public enum TrainResult : byte
{
    Trained = 0,
    NotTrainable = 1,
    Locked = 2,
    NotEnoughPoints = 3,
    AboveCap = 4,
    InvalidAmount = 5,
}

/// <summary>
/// A character's progression under the content rules: level, experience, unspent skill points,
/// and the cost and cap of every trainable stat. Server and client run this same code, so the
/// training screen and the server always agree.
/// </summary>
public sealed class CharacterBuild
{
    /// <summary>Largest single training request; keeps cost arithmetic far from overflow.</summary>
    public const int MaxPointsPerRequest = 10_000;

    private readonly Dictionary<Stat, int> _starting;

    private CharacterBuild(Character character, ProfessionDef profession, OriginDef origin, ProgressionDef rules, Dictionary<Stat, int> starting)
    {
        Character = character;
        Profession = profession;
        Origin = origin;
        Rules = rules;
        _starting = starting;
    }

    public Character Character { get; }

    public ProfessionDef Profession { get; }

    public OriginDef Origin { get; }

    public ProgressionDef Rules { get; }

    public int Level => Character.Stats.GetBase(Stat.Level);

    public long Xp { get; private set; }

    public int UnspentPoints { get; private set; }

    /// <summary>XP needed for the next level; 0 at the level cap.</summary>
    public long XpToNext => Level >= Rules.MaxLevel ? 0 : Rules.XpToNext.At(Level);

    /// <summary>The starting kit: common stats, then origin attributes, then profession skills.</summary>
    public static CharacterBuild Create(ContentPack pack, Profession profession, Origin origin)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var professionDef = pack.Professions[(int)profession];
        var originDef = pack.Origins[(int)origin];

        var starting = new Dictionary<Stat, int>(pack.NewCharacter.Stats);
        foreach (var (stat, value) in originDef.Attributes)
        {
            starting[stat] = value;
        }
        foreach (var (stat, value) in professionDef.StartingSkills)
        {
            starting[stat] = starting.GetValueOrDefault(stat) + value;
        }
        starting[Stat.Level] = 1;

        var character = new Character(profession, origin);
        foreach (var (stat, value) in starting)
        {
            character.Stats.SetBase(stat, value);
        }
        return new CharacterBuild(character, professionDef, originDef, pack.Progression, starting)
        {
            UnspentPoints = pack.Progression.StartingPoints,
        };
    }

    /// <summary>
    /// Rebuilds a character from saved progression: a fresh build for the profession and origin,
    /// then the stored level, experience, unspent points and trained base values. Used by the
    /// client to mirror its character sheet and (M2.3) by persistence to load characters.
    /// </summary>
    public static CharacterBuild Restore(
        ContentPack pack,
        Profession profession,
        Origin origin,
        int level,
        long xp,
        int unspentPoints,
        IEnumerable<KeyValuePair<Stat, int>> trainedBases)
    {
        ArgumentNullException.ThrowIfNull(trainedBases);
        var build = Create(pack, profession, origin);
        build.Character.Stats.SetBase(Stat.Level, Math.Clamp(level, 1, build.Rules.MaxLevel));
        foreach (var (stat, value) in trainedBases)
        {
            if (Skills.IsTrainable(stat))
            {
                build.Character.Stats.SetBase(stat, value);
            }
        }
        build.Xp = Math.Max(0, xp);
        build.UnspentPoints = Math.Max(0, unspentPoints);
        return build;
    }

    /// <summary>The value a stat started at; training caps are measured from here.</summary>
    public int StartingValue(Stat stat) => _starting.GetValueOrDefault(stat);

    public CostTier TierOf(Stat stat)
    {
        if (!Skills.IsTrainable(stat))
        {
            return CostTier.Locked;
        }
        var group = Skills.GroupOf(stat);
        if (group == SkillGroup.Attribute)
        {
            return Origin.AttributeTiers.GetValueOrDefault(stat, CostTier.Normal);
        }
        return Profession.SkillTiers.TryGetValue(stat, out var exception)
            ? exception
            : Profession.GroupTiers.GetValueOrDefault(group, CostTier.Normal);
    }

    /// <summary>Skill points per point of <paramref name="stat"/>; 0 if it cannot be trained.</summary>
    public int CostPerPoint(Stat stat) =>
        Rules.Tiers.TryGetValue(TierOf(stat), out var rule) ? rule.CostPerPoint : 0;

    /// <summary>Highest base value training may reach at the current level.</summary>
    public int Cap(Stat stat) =>
        Rules.Tiers.TryGetValue(TierOf(stat), out var rule) ? StartingValue(stat) + (Level * rule.CapPerLevel) : StartingValue(stat);

    /// <summary>Spends skill points to raise a stat's base value.</summary>
    public TrainResult Train(Stat stat, int points)
    {
        if (points is <= 0 or > MaxPointsPerRequest)
        {
            return TrainResult.InvalidAmount;
        }
        if (!Skills.IsTrainable(stat))
        {
            return TrainResult.NotTrainable;
        }
        var costPerPoint = CostPerPoint(stat);
        if (costPerPoint == 0)
        {
            return TrainResult.Locked;
        }
        var target = (long)Character.Stats.GetBase(stat) + points;
        if (target > Cap(stat))
        {
            return TrainResult.AboveCap;
        }
        var cost = (long)points * costPerPoint;
        if (cost > UnspentPoints)
        {
            return TrainResult.NotEnoughPoints;
        }
        UnspentPoints -= (int)cost;
        Character.Stats.SetBase(stat, (int)target);
        return TrainResult.Trained;
    }

    /// <summary>Adds experience and levels up as often as it allows. Returns levels gained.</summary>
    public int AddXp(long amount)
    {
        if (amount <= 0 || Level >= Rules.MaxLevel)
        {
            return 0;
        }
        Xp += amount;
        var gained = 0;
        while (Level < Rules.MaxLevel && Xp >= XpToNext)
        {
            Xp -= XpToNext;
            var newLevel = Level + 1;
            Character.Stats.SetBase(Stat.Level, newLevel);
            UnspentPoints += Rules.PointsPerLevel.At(newLevel) + Origin.BonusPointsPerLevel;
            gained++;
        }
        if (Level >= Rules.MaxLevel)
        {
            Xp = 0; // nothing more to earn
        }
        return gained;
    }

    /// <summary>The weapon from this profession's kit at the kit's QL (validated when the pack loads).</summary>
    public WeaponProfile StartingWeapon(ContentPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return pack.Items[Profession.StartingWeaponKey].WeaponAt(Profession.StartingWeaponQl)!;
    }
}
