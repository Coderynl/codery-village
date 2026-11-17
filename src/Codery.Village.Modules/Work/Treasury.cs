namespace Codery.Village.Modules.Work;

/// <summary>
/// The purse of an island, in cents: what it holds, and its books of what came in from outside the village and
/// went out of it. Wages, allowances, pensions and purchases move money between it and the residents.
/// </summary>
public sealed class Treasury
{
    public long Cash { get; internal set; }

    /// <summary>All the start subsidy the island has had.</summary>
    public long Subsidy { get; internal set; }

    /// <summary>All the island has paid for the upkeep of its buildings.</summary>
    public long Upkeep { get; internal set; }

    /// <summary>All the residents of the island have spent: money that left the village.</summary>
    public long Spent { get; internal set; }

    /// <summary>All the residents of the island took with them when they left: money that left the village.</summary>
    public long Departed { get; internal set; }
}
