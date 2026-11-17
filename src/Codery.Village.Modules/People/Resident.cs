namespace Codery.Village.Modules.People;

/// <summary>Chooses the name and nothing else.</summary>
public enum Sex { Man, Woman }

public enum LifePhase { Child, Student, Working, Retired }

/// <summary>Someone who lives in the village. People makes them; Work changes <see cref="Money"/>.</summary>
public sealed class Resident
{
    internal Resident(int id, string name, Sex sex, long bornTick, Personality traits, IReadOnlyList<string> interests,
        int island, int? household, LifePhase phase, bool isEddie, long money)
    {
        Id = id;
        Name = name;
        Sex = sex;
        BornTick = bornTick;
        Traits = traits;
        Interests = interests;
        Island = island;
        Household = household;
        Phase = phase;
        IsEddie = isEddie;
        Money = money;
    }

    public int Id { get; }

    public string Name { get; }

    public Sex Sex { get; }

    /// <summary>The tick of birth, before the village began for those who were there at the start. Eddie does not age: ask <see cref="People.AgeOf"/>.</summary>
    public long BornTick { get; }

    public Personality Traits { get; }

    /// <summary>One to three interest ids from the data.</summary>
    public IReadOnlyList<string> Interests { get; }

    public int Island { get; }

    public int? Household { get; internal set; }

    public LifePhase Phase { get; internal set; }

    public bool IsEddie { get; }

    /// <summary>In cents. Only Work changes it.</summary>
    public long Money { get; set; }
}
