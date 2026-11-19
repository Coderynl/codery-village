using Codery.Village.Modules.People;

namespace Codery.Village.Modules.Building;

/// <summary>The section "building" of the data.</summary>
public sealed record BuildingSettings(
    // one hut per household at the start, at most so many per island
    int MaxStartHuts,
    // the blueprints that stand finished on Eddie's islet from the start; outside the economy, never built by the islands
    IReadOnlyList<string> Eddie,
    int ThingsAtStart,
    int ThingsPerHouse,
    // the label a school serves, and the label of a place to gather, such as a market
    string School,
    string Gathering,
    CommunitySettings Community,
    IReadOnlyDictionary<string, Sentence> Log,
    // the blueprint that stands finished on every ordinary island from the start, where it gathers while nothing serves the gathering label
    string? Fireplace = null,
    // a moved thing in someone's way goes back within so many minutes
    int PutBackMinutes = 5,
    // what a helper does at a site, as a share of a builder
    double HelpShare = 0.5,
    // per island by name, the workplaces that stand finished on it in a fresh village, after the fireplace and before the huts
    IReadOnlyDictionary<string, IReadOnlyList<string>>? StartWorkplaces = null);

/// <summary>How an island decides what to build.</summary>
public sealed record CommunitySettings(
    // sites going up at the same time, per island
    int MaxSites,
    // a workshop for a need comes when the stock holds less than so many days of it
    int LowStockDays,
    // how often the islands decide
    int DecideTicks);

/// <summary>What Building reads from the section "economy": the full crew of a site and the role of a builder.</summary>
internal sealed record CrewSettings(int Crew, string BuilderRole);
