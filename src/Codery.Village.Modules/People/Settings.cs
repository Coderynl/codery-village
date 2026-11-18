namespace Codery.Village.Modules.People;

/// <summary>The section "people" of the data.</summary>
public sealed record PeopleSettings(
    int StartMin,
    int StartMax,
    NamesBySex Names,
    IReadOnlyList<Interest> Interests,
    PersonalitySpread Personality,
    IReadOnlyList<HouseholdKind> Households,
    // the ages of singles and of couples without children
    IReadOnlyList<AgeBand> AdultAges,
    IReadOnlyList<AgeBand> ParentAges,
    IReadOnlyList<AgeBand> ChildAges,
    // how many years partners differ at most
    int PartnerGap,
    // how many years a child is at least younger than its youngest parent
    int ParentGap,
    double ArrivalChance,
    double ArrivalDamping,
    string EddieName,
    IReadOnlyDictionary<string, Sentence> Log);

public sealed record NamesBySex(IReadOnlyList<string> Man, IReadOnlyList<string> Woman);

/// <summary>An interest; one with <paramref name="Study"/> can lead an open young person to study.</summary>
public sealed record Interest(string Id, string Nl, string En, bool Study = false);

/// <summary>The normal distribution of a trait: its mean and its standard deviation.</summary>
public sealed record TraitSpread(double Mean, double Spread);

public sealed record PersonalitySpread(
    TraitSpread Openness,
    TraitSpread Conscientiousness,
    TraitSpread Extraversion,
    TraitSpread Agreeableness,
    TraitSpread Sensitivity,
    TraitSpread Status,
    TraitSpread Comfort,
    TraitSpread Thrift)
{
    /// <summary>The spreads in the order of <see cref="Personality.Traits"/>.</summary>
    public IReadOnlyList<TraitSpread> All => [Openness, Conscientiousness, Extraversion, Agreeableness, Sensitivity, Status, Comfort, Thrift];
}

public sealed record HouseholdKind(int Adults, int Children, int Weight);

/// <summary>Ages from <paramref name="From"/> up to and including <paramref name="To"/> whole years.</summary>
public sealed record AgeBand(int From, int To, int Weight);

/// <summary>A log sentence; <c>{subject}</c> is filled in.</summary>
public sealed record Sentence(string Nl, string En);

/// <summary>The life course in the section "life" of the data, in life years.</summary>
public sealed record LifeCourse(int SchoolFrom, int WorkFrom, int RetireFrom, int StudyYears, int RetiredYears, int EddieAge);
