using System.Collections.Frozen;
using Meridian.Core.Characters;
using Meridian.Core.Stats;

namespace Meridian.Core.Requirements;

/// <summary>
/// A requirement tree, e.g. <c>All(Rifle &gt;= 420, Agility &gt;= 210, Profession in {Operative, Trooper})</c>.
/// Every weapon, armor piece, implant and Protocol carries one.
/// </summary>
public abstract record Requirement
{
    /// <summary>Always satisfied.</summary>
    public static Requirement None { get; } = new AllOf([]);

    /// <summary>
    /// Evaluates the requirement. When <paramref name="failures"/> is given, every unmet clause
    /// is appended so a tooltip can show e.g. "Rifle 398 / 420".
    /// </summary>
    public abstract bool Evaluate(ICharacterView character, List<RequirementFailure>? failures = null);

    /// <summary>Convenience: the list of unmet clauses (empty when met).</summary>
    public IReadOnlyList<RequirementFailure> Explain(ICharacterView character)
    {
        var failures = new List<RequirementFailure>();
        Evaluate(character, failures);
        return failures;
    }

    public static Requirement AtLeast(Stat stat, int minimum) => new StatAtLeast(stat, minimum);

    public static Requirement Level(int minimum) => new StatAtLeast(Stat.Level, minimum);

    public static Requirement Professions(params Profession[] allowed) => new ProfessionIn(allowed.ToFrozenSet());

    public static Requirement Origins(params Origin[] allowed) => new OriginIn(allowed.ToFrozenSet());

    public static Requirement All(params Requirement[] clauses) => new AllOf(clauses);

    public static Requirement Any(params Requirement[] alternatives) => new AnyOf(alternatives);
}

/// <summary>The effective value of <see cref="Stat"/> must be at least <see cref="Minimum"/>.</summary>
public sealed record StatAtLeast(Stat Stat, int Minimum) : Requirement
{
    public override bool Evaluate(ICharacterView character, List<RequirementFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        var actual = character.Get(Stat);
        if (actual >= Minimum)
        {
            return true;
        }
        failures?.Add(new StatShortfall(Stat, actual, Minimum));
        return false;
    }

    public override string ToString() => $"{Stat} >= {Minimum}";
}

public sealed record ProfessionIn(FrozenSet<Profession> Allowed) : Requirement
{
    public override bool Evaluate(ICharacterView character, List<RequirementFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (Allowed.Contains(character.Profession))
        {
            return true;
        }
        failures?.Add(new ProfessionNotAllowed(character.Profession, Allowed));
        return false;
    }

    public override string ToString() => $"Profession in {{{string.Join(", ", Allowed.Order())}}}";
}

public sealed record OriginIn(FrozenSet<Origin> Allowed) : Requirement
{
    public override bool Evaluate(ICharacterView character, List<RequirementFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (Allowed.Contains(character.Origin))
        {
            return true;
        }
        failures?.Add(new OriginNotAllowed(character.Origin, Allowed));
        return false;
    }

    public override string ToString() => $"Origin in {{{string.Join(", ", Allowed.Order())}}}";
}

/// <summary>Every clause must hold. Evaluates all clauses so every failure is reported.</summary>
public sealed record AllOf(IReadOnlyList<Requirement> Clauses) : Requirement
{
    public override bool Evaluate(ICharacterView character, List<RequirementFailure>? failures = null)
    {
        var ok = true;
        foreach (var clause in Clauses)
        {
            // No short-circuit when collecting failures: the player wants the whole list.
            ok &= clause.Evaluate(character, failures);
            if (!ok && failures is null)
            {
                return false;
            }
        }
        return ok;
    }

    public override string ToString() => $"All({string.Join(", ", Clauses)})";
}

/// <summary>At least one alternative must hold.</summary>
public sealed record AnyOf(IReadOnlyList<Requirement> Alternatives) : Requirement
{
    public override bool Evaluate(ICharacterView character, List<RequirementFailure>? failures = null)
    {
        if (failures is null)
        {
            foreach (var alt in Alternatives)
            {
                if (alt.Evaluate(character))
                {
                    return true;
                }
            }
            return false;
        }

        var perAlternative = new List<IReadOnlyList<RequirementFailure>>(Alternatives.Count);
        foreach (var alt in Alternatives)
        {
            var altFailures = new List<RequirementFailure>();
            if (alt.Evaluate(character, altFailures))
            {
                return true;
            }
            perAlternative.Add(altFailures);
        }
        failures.Add(new NoAlternativeMet(perAlternative));
        return false;
    }

    public override string ToString() => $"Any({string.Join(", ", Alternatives)})";
}

/// <summary>Why a requirement was not met.</summary>
public abstract record RequirementFailure;

public sealed record StatShortfall(Stat Stat, int Actual, int Required) : RequirementFailure
{
    public int Missing => Required - Actual;

    public override string ToString() => $"{Stat} {Actual} / {Required}";
}

public sealed record ProfessionNotAllowed(Profession Actual, FrozenSet<Profession> Allowed) : RequirementFailure;

public sealed record OriginNotAllowed(Origin Actual, FrozenSet<Origin> Allowed) : RequirementFailure;

public sealed record NoAlternativeMet(IReadOnlyList<IReadOnlyList<RequirementFailure>> Alternatives) : RequirementFailure;
