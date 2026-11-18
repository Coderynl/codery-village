using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Core.Modules;
using Codery.Village.Modules.Building;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.Work;
using BuildingModule = Codery.Village.Modules.Building.Building;
using PeopleModule = Codery.Village.Modules.People.People;

namespace Codery.Village.Modules.Community;

/// <summary>
/// What an island builds. Every <c>decideTicks</c> each ordinary island with room for another site starts the
/// first of, in this order: what a site of the island waits for and nobody on the island makes (also as one site
/// beyond <c>maxSites</c>, so sites waiting for each other never block the island); a home, when a
/// household has none or no home is free for a newcomer; a workshop for food or heat, when the stock holds less
/// than <c>lowStockDays</c> days of it and nothing on the island makes it; a building for the heaviest wish the
/// data can fulfil. A building needs what the island lacks and does not make, so it comes after its maker: a
/// bakery after a farm. What the open sites still have to pay counts as spent. Each goes as near the heart of the island as it fits. Eddie's islet builds nothing.
/// </summary>
public sealed class Community : IModule
{
    private CommunitySettings _settings = null!;
    private EconomySettings _economy = null!;
    private IReadOnlySet<string> _eddie = new HashSet<string>();
    private List<Blueprint> _blueprints = [];
    private Dictionary<string, Blueprint> _byId = [];
    private List<Material> _materials = [];

    public string Name => "community";

    public int Version => 1;

    public void Start(ModuleContext village) => Read(village);

    public void Step(ModuleContext village)
    {
        if (village.Clocks.Tick % _settings.DecideTicks != 0) return;
        if (village.Find<BuildingModule>() is not { } building || village.Find<IStock>() is not { } stock) return;
        foreach (var island in village.Map.Islands)
        {
            if (island.Eddie) continue;
            var here = new Island(island.Id, building, stock, village.Find<PeopleModule>(), village.Find<IWishSource>());
            var open = building.All.Count(structure => structure.Island == island.Id && !structure.Done);
            // with every place for a site taken, only the maker of what those sites wait for may come, as one site more
            var wants = open < _settings.MaxSites ? Supplies(here).Concat(Wants(here))
                : open == _settings.MaxSites ? Supplies(here)
                : [];
            foreach (var wanted in wants)
            {
                if (Resolve(here, wanted, []) is not { } chosen) continue;
                building.Place(chosen.Blueprint, chosen.Anchor, chosen.Rotation);
                break;
            }
        }
    }

    // ── what the island wants, in order ──

    // what a site of the island waits for, or will, and nobody on the island makes
    private IEnumerable<string> Supplies(Island here)
    {
        foreach (var site in OpenSites(here))
            if (Missing(here, StillToPay(site), site) is { } material)
                foreach (var maker in Makers(material)) yield return maker;
    }

    private IEnumerable<string> Wants(Island here)
    {
        // a home for every household, and one to spare for a newcomer
        if (Homeless(here) + 1 > FreeHomes(here))
        {
            var dwellings = _blueprints.Where(plan => plan.Homes > 0).ToList();
            foreach (var plan in dwellings.Where(plan => Missing(here, Needs(plan)) is null)) yield return plan.Id;
            foreach (var plan in dwellings.Where(plan => Missing(here, Needs(plan)) is not null)) yield return plan.Id;
        }

        // food and heat
        foreach (var (need, amount) in _economy.Needs)
        {
            var goods = _materials.Where(material => material.Satisfies?.Contains(need) == true).Select(material => material.Id).ToHashSet();
            if (!Low(here, goods, amount) || here.Building.All.Any(structure => structure.Island == here.Id && Makes(structure.Blueprint, goods))) continue;
            foreach (var plan in _blueprints.Where(plan => Makes(plan.Id, goods))) yield return plan.Id;
        }

        // the heaviest wish the data can fulfil, unless it is going up already
        foreach (var (label, _) in here.Wishes?.Wanted(here.Id).OrderByDescending(wish => wish.Weight) ?? Enumerable.Empty<(string, double)>())
        {
            if (here.Building.All.Any(structure => structure.Island == here.Id && !structure.Done && _byId.TryGetValue(structure.Blueprint, out var plan) && plan.Serves == label)) continue;
            foreach (var plan in _blueprints.Where(plan => plan.Serves == label)) yield return plan.Id;
        }
    }

    /// <summary>
    /// The blueprint to start for what is wanted, with its place: the wanted one when it fits on the island and
    /// the island has or makes all it needs; else the maker of the first thing it lacks, in the same way.
    /// </summary>
    private (string Blueprint, Codery.Village.Core.Map.Cell Anchor, int Rotation)? Resolve(Island here, string blueprint, HashSet<string> seen)
    {
        if (!seen.Add(blueprint) || !_byId.TryGetValue(blueprint, out var plan) || here.Building.FindPlace(blueprint, here.Id) is not { } place) return null;
        if (Missing(here, Needs(plan)) is not { } material) return (blueprint, place.Anchor, place.Rotation);
        foreach (var maker in Makers(material))
            if (Resolve(here, maker, seen) is { } chosen) return chosen;
        return null;
    }

    /// <summary>
    /// The first material, in the order of the catalog, of which the island holds too little and that nothing on it
    /// makes. What the open sites (but <paramref name="self"/>) still have to pay is not there to be had.
    /// </summary>
    private string? Missing(Island here, IReadOnlyDictionary<string, int> needs, Structure? self = null)
    {
        var promised = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var site in OpenSites(here).Where(site => site != self))
            foreach (var (material, amount) in StillToPay(site))
                promised[material] = promised.GetValueOrDefault(material) + amount;
        foreach (var material in _materials)
        {
            if (!needs.TryGetValue(material.Id, out var amount)
                || here.Stock.Count(here.Id, material.Id) - promised.GetValueOrDefault(material.Id) >= amount) continue;
            if (here.Building.All.Any(structure => structure.Island == here.Id && Makes(structure.Blueprint, new HashSet<string> { material.Id }))) continue;
            return material.Id;
        }
        return null;
    }

    private IEnumerable<Structure> OpenSites(Island here) =>
        here.Building.All.Where(structure => structure.Island == here.Id && !structure.Done && _byId.ContainsKey(structure.Blueprint));

    // the cost of the phases it has not paid yet
    private Dictionary<string, int> StillToPay(Structure site) =>
        BuildingModule.CostFrom(_byId[site.Blueprint], site.Paid ? site.Phase + 1 : site.Phase);

    // all its phases cost, and what one batch of its recipe takes
    private static Dictionary<string, int> Needs(Blueprint plan)
    {
        var needs = BuildingModule.CostFrom(plan, 0);
        foreach (var (material, amount) in plan.Recipe?.In ?? new Dictionary<string, int>())
            needs[material] = needs.GetValueOrDefault(material) + amount;
        return needs;
    }

    private IEnumerable<string> Makers(string material) =>
        _blueprints.Where(plan => plan.Recipe?.Out.ContainsKey(material) == true).Select(plan => plan.Id);

    private bool Makes(string blueprint, IReadOnlySet<string> materials) =>
        _byId.TryGetValue(blueprint, out var plan) && plan.Recipe?.Out.Keys.Any(materials.Contains) == true;

    private static int Homeless(Island here) =>
        here.People?.On(here.Id).Where(resident => !resident.IsEddie && resident.Household is not null)
            .Select(resident => resident.Household!.Value).Distinct().Count(household => !here.Building.HasHome(household)) ?? 0;

    // homes nobody lives in, and the homes of the dwellings going up
    private int FreeHomes(Island here) =>
        here.Building.All.Where(structure => structure.Island == here.Id).Sum(structure =>
            _byId.TryGetValue(structure.Blueprint, out var plan) && plan.Homes is { } homes ? homes - (structure.Done ? structure.Households.Count : 0) : 0);

    // less in stock than lowStockDays days of what the island's households buy
    private bool Low(Island here, IReadOnlySet<string> goods, Need amount)
    {
        var residents = here.People?.On(here.Id).Where(resident => !resident.IsEddie).ToList() ?? [];
        var perDay = amount.PerPerson * residents.Count + amount.PerHousehold * residents.Select(resident => resident.Household ?? -1 - resident.Id).Distinct().Count();
        return goods.Sum(material => here.Stock.Count(here.Id, material)) < _settings.LowStockDays * perDay;
    }

    // ── the data ──

    private void Read(ModuleContext village)
    {
        var settings = village.Data.Section<BuildingSettings>("building");
        _settings = settings.Community;
        _economy = village.Data.Section<EconomySettings>("economy");
        var catalog = village.Data.Section<Catalog>("catalog");
        _eddie = settings.Eddie.ToHashSet();
        // Eddie's buildings are his; the islands do not build them
        _blueprints = [.. catalog.Blueprints.Where(plan => !_eddie.Contains(plan.Id))];
        _byId = catalog.Blueprints.ToDictionary(plan => plan.Id);
        _materials = [.. catalog.Materials];
        if (_settings.DecideTicks <= 0) throw new InvalidDataException("The data section 'building' needs community.decideTicks above 0.");
    }

    // Community keeps nothing of its own: it decides from what the village is now
    public JsonNode Save() => new JsonObject();

    public void Load(ModuleContext village, JsonNode data, int version) => Read(village);

    private sealed record Island(int Id, BuildingModule Building, IStock Stock, PeopleModule? People, IWishSource? Wishes);
}
