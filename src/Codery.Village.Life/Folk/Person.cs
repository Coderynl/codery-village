using Codery.Village.Modules.People;

namespace Codery.Village.Life.Folk;

/// <summary>The stages of a life, by age alone: a child before school, a pupil, an adult, the old.</summary>
public enum LifeStage { Child, Pupil, Adult, Old }

/// <summary>Someone who lives, or lived, in the village. Folk makes them and changes them.</summary>
public sealed class Person
{
    internal Person(int id, string name, Sex sex, long bornTick, Personality traits, IReadOnlyList<string> interests,
        Sex drawnTo, int? mother, int? father, int household)
    {
        Id = id;
        Name = name;
        Sex = sex;
        BornTick = bornTick;
        Traits = traits;
        Interests = interests;
        DrawnTo = drawnTo;
        Mother = mother;
        Father = father;
        Household = household;
    }

    public int Id { get; }

    public string Name { get; }

    /// <summary>Chooses the name and the pronoun; a pair has a child only when it is a woman and a man.</summary>
    public Sex Sex { get; }

    /// <summary>The tick of birth; below 0 for those who were there at the start.</summary>
    public long BornTick { get; }

    public Personality Traits { get; }

    /// <summary>One to three interest ids from the section "people".</summary>
    public IReadOnlyList<string> Interests { get; }

    /// <summary>The sex this person pairs with; drawn once, at birth or at the start.</summary>
    public Sex DrawnTo { get; }

    /// <summary>Null for those who were there at the start.</summary>
    public int? Mother { get; }

    public int? Father { get; }

    /// <summary>Null when single, and again once the partner has died.</summary>
    public int? Partner { get; internal set; }

    /// <summary>A pair and their children under 18; an adult child stays until pairing.</summary>
    public int Household { get; internal set; }

    /// <summary>A trade id from the section "start", chosen at 18; null before.</summary>
    public string? Trade { get; internal set; }

    /// <summary>0 to 100; at 0 the person dies.</summary>
    public int Health { get; internal set; } = 100;

    public long? DiedTick { get; internal set; }
}
