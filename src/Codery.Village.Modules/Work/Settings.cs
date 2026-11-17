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
    StartHoldings Start);

/// <summary>How much of a need a household buys a day: so much per member and so much for the household.</summary>
public sealed record Need(int PerPerson = 0, int PerHousehold = 0);

/// <summary>What an ordinary island starts with: cash, and a stock by the island's name.</summary>
public sealed record StartHoldings(long Cash, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Stock);
