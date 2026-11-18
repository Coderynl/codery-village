using Codery.Village.Core.Data;

namespace Codery.Village.Modules.Work;

/// <summary>The section "economy" of the data. Money is in cents.</summary>
public sealed record EconomySettings(
    // paid per hour on the job, a 14400th of it every tick
    int WagePerHour,
    // a day, to children and students
    int AllowancePerDay,
    // a day, to the retired
    int PensionPerDay,
    // of what is left after the daily purchase, the share in percent a resident without any thrift spends
    int SpendPercent,
    // a day, per building that stands
    int UpkeepPerDay,
    // a day: startSubsidy / (1 + inhabitants / subsidyDamping)
    int StartSubsidy,
    double SubsidyDamping,
    // places per ordinary island for the island's own builders
    int Builders,
    string BuilderRole,
    Shift BuilderShift,
    string EddieRole,
    string EddieWorkshop,
    int ForestRegrowTicks,
    // how far from its hut a woodcutter fells
    int ForestRange,
    // what a household buys every day, by need
    IReadOnlyDictionary<string, Need> Needs,
    // by material
    IReadOnlyDictionary<string, int> Prices,
    // by role, the interests that fit it
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fits,
    StartHoldings Start,
    // a day, to who is of working age, less the wage he earned that day: in full to who has no job
    int SupportPerDay = 0,
    // how far from its workplace an outdoor job with a recipe is done, in tiles; 0 keeps every job at its workplace
    int WorkRange = 0,
    // how many quarters of an hour a worker keeps his tile of the work area
    int StayQuarters = 1,
    // by ground, how a tile of it that is worked runs out and grows back; a ground without it never runs out
    IReadOnlyDictionary<string, NatureSettings>? Nature = null,
    // whether a household fetches the food it bought at the store where it lies, for all to see; the purchase is booked either way
    bool FetchFood = false);

/// <summary>
/// How a tile of a ground runs out and grows back. Its yield goes from 100 (full) to 0; every batch made on it takes
/// <see cref="PerBatch"/>, and in <see cref="RegrowDays"/> days it grows from nothing back to full, but only on the days
/// of the months in <see cref="GrowsInMonths"/> (1 to 12; every month when left out).
/// </summary>
public sealed record NatureSettings(int PerBatch, int RegrowDays, IReadOnlyList<int>? GrowsInMonths = null);

/// <summary>How much of a need a household buys a day: so much per member and so much for the household.</summary>
public sealed record Need(int PerPerson = 0, int PerHousehold = 0);

/// <summary>What an ordinary island starts with: cash, and a stock by the island's name.</summary>
public sealed record StartHoldings(long Cash, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Stock);
