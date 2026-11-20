using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.People;
using PeopleModule = Codery.Village.Modules.People.People;

namespace Codery.Village.Modules.Wishes;

/// <summary>
/// What the residents wish for. A wish counts for a resident who fits its <c>who</c>, and weighs
/// <c>weight × (1 + sensitivity / 100)</c> for him. It is fulfilled when a standing building on his island serves
/// its label within the building's range of his home. What the data can fulfil, Community builds
/// (<see cref="IWishSource"/>). What it cannot, the village writes a task for: one for the whole land, for the
/// heaviest such wish that an island weighs more than <c>taskThreshold</c>. The task stays open until the data
/// knows the label, after a release and a restart. A wish with <c>after</c> counts only once the data knows all it
/// names, so one release can bring the next wish. Eddie wishes for nothing.
/// </summary>
public sealed class Wishes : IModule, IWishSource
{
    private const string Opened = "task-opened";
    private const string Closed = "task-closed";
    private const string HeldKind = "task-held";

    private WishSettings _settings = null!;
    private IReadOnlyList<Wish> _wishes = [];
    private Dictionary<string, Wish> _byId = [];
    private Dictionary<string, Blueprint> _blueprints = [];
    private HashSet<string> _served = [];
    // the wishes whose after the data knows: the others do not count yet
    private HashSet<string> _known = [];
    private ModuleContext _village = null!;
    // the calendar moment the village began; a wish with afterDays waits from here
    private DateTimeOffset _began;
    private OpenTask? _task;
    // the wishes the village does not ask a task for, for now
    private readonly HashSet<string> _held = [];

    public string Name => "wishes";

    public int Version => 2;

    /// <summary>The wishes on hold: they are tallied and fulfilled as ever, but no task is written for them.</summary>
    public IReadOnlySet<string> Held => _held;

    /// <summary>Puts a wish on hold. The open task for it is dropped, as <see cref="TaskClosed"/> with a <c>task-held</c> line.</summary>
    public void Hold(string wish)
    {
        _held.Add(wish);
        if (_task is not { } task || task.Wish != wish) return;
        _task = null;
        _village.Publish(new TaskClosed(_village.Clocks.Tick, wish));
        _village.Log(new LogEntry(_village.Clocks.Tick, HeldKind, wish));
    }

    /// <summary>Takes a wish off hold: the next time the village writes a task, it counts again.</summary>
    public void Release(string wish) => _held.Remove(wish);

    /// <summary>The task for Eddie, or null when there is none open.</summary>
    public OpenTask? Task => _task;

    /// <summary>The wishes of the island that are not fulfilled, by id, added up over its residents; the heaviest first.</summary>
    public IReadOnlyList<(string Wish, double Weight)> Of(int island) =>
        [.. Tally(island).Select((tally, index) => (Wish: _wishes[index].Id, tally.Weight, Index: index))
            .Where(item => item.Weight > 0)
            .OrderByDescending(item => item.Weight).ThenBy(item => item.Index)
            .Select(item => (item.Wish, item.Weight))];

    /// <summary>The ids of the wishes that count for the resident, fulfilled or not, in the order of the data.</summary>
    public IReadOnlyList<string> Of(Resident resident) =>
        [.. _wishes.Where(wish => Counts(resident, wish)).Select(wish => wish.Id)];

    /// <summary>Whether a standing building on the resident's island serves the wish within its range of his home.</summary>
    public bool Fulfilled(Resident resident, string wish) =>
        _byId.TryGetValue(wish, out var known) && Fulfilled(Serving(), resident, known);

    /// <summary>The wishes of the island that the data can fulfil, by label, added up; the heaviest first.</summary>
    public IReadOnlyList<(string Label, double Weight)> Wanted(int island) =>
        [.. Of(island).Where(item => _served.Contains(_byId[item.Wish].Label))
            .GroupBy(item => _byId[item.Wish].Label)
            .Select(group => (Label: group.Key, Weight: group.Sum(item => item.Weight)))
            .OrderByDescending(item => item.Weight)];

    public void Start(ModuleContext village)
    {
        Read(village);
        _began = village.Clocks.Calendar.Now;
        _task = null;
    }

    public void Step(ModuleContext village)
    {
        // the data knows the wish now: Community can build it
        if (_task is { } task && (!_byId.TryGetValue(task.Wish, out var wish) || _served.Contains(wish.Label)))
        {
            _task = null;
            village.Publish(new TaskClosed(village.Clocks.Tick, task.Wish));
            village.Log(new LogEntry(village.Clocks.Tick, Closed, task.Wish));
        }
        if (_task is not null || village.Clocks.Tick % _settings.EveryTicks != 0) return;
        Write(village);
    }

    // the heaviest wish the data cannot fulfil, when an island weighs it more than the threshold
    private void Write(ModuleContext village)
    {
        var islands = village.Map.Islands.Where(island => !island.Eddie).ToList();
        var tallies = islands.Select(island => Tally(island.Id)).ToList();
        int? chosen = null;
        var heaviest = 0.0;
        for (var index = 0; index < _wishes.Count; index++)
        {
            if (_served.Contains(_wishes[index].Label) || _held.Contains(_wishes[index].Id) || !tallies.Any(tally => tally[index].Weight > _settings.TaskThreshold)) continue;
            var total = tallies.Sum(tally => tally[index].Weight);
            if (chosen is not null && total <= heaviest) continue;
            chosen = index;
            heaviest = total;
        }
        if (chosen is not { } at) return;

        var most = 0;
        for (var i = 1; i < islands.Count; i++)
            if (tallies[i][at].Weight > tallies[most][at].Weight) most = i;
        var wish = _wishes[at];
        var count = tallies[most][at].Count;
        var values = new Dictionary<string, string>
        {
            ["count"] = count.ToString(CultureInfo.InvariantCulture),
            ["island"] = islands[most].Name,
            ["elsewhere"] = (tallies.Sum(tally => tally[at].Count) - count).ToString(CultureInfo.InvariantCulture),
            ["since"] = village.Clocks.Calendar.Now.ToString("d-M-yyyy", CultureInfo.InvariantCulture),
            ["weight"] = Math.Round(heaviest).ToString(CultureInfo.InvariantCulture),
            ["threshold"] = _settings.TaskThreshold.ToString("0.##", CultureInfo.InvariantCulture),
        };
        var template = _settings.Task;
        _task = new OpenTask(wish.Id, islands[most].Id, village.Clocks.Tick, heaviest,
            Fill(template.TitleNl, values, wish.Nl), Fill(template.TitleEn, values, wish.En), Fill(template.BodyNl, values, wish.Nl));
        village.Publish(new TaskOpened(village.Clocks.Tick, _task));
        village.Log(new LogEntry(village.Clocks.Tick, Opened, wish.Id));
    }

    private static string Fill(string template, Dictionary<string, string> values, string wish)
    {
        var text = template.Replace("{wish}", wish);
        foreach (var (key, value) in values) text = text.Replace("{" + key + "}", value);
        return text;
    }

    // ── who wishes what ──

    /// <summary>Per wish, in the order of the data: what the island weighs it and how many wish it, of those who do not have it.</summary>
    private (double Weight, int Count)[] Tally(int island)
    {
        var tally = new (double Weight, int Count)[_wishes.Count];
        if (_village.Find<PeopleModule>() is not { } people) return tally;
        var serving = Serving();
        foreach (var resident in people.On(island))
        {
            if (resident.IsEddie) continue;
            Cell? home = null;
            var looked = false;
            for (var index = 0; index < _wishes.Count; index++)
            {
                var wish = _wishes[index];
                if (!Counts(resident, wish)) continue;
                if (!looked) (home, looked) = (_village.Find<IPlaces>()?.HomeOf(resident.Id), true);
                if (Fulfilled(serving, resident.Island, home, wish)) continue;
                tally[index] = (tally[index].Weight + wish.Weight * (1 + resident.Traits.Sensitivity / 100.0), tally[index].Count + 1);
            }
        }
        return tally;
    }

    private bool Counts(Resident resident, Wish wish)
    {
        if (resident.IsEddie || !_known.Contains(wish.Id)) return false;
        if (wish.AfterDays > 0 && (_village.Clocks.Calendar.Now - _began).TotalDays < wish.AfterDays) return false;
        if (wish.Who is not { } who) return true;
        if (who.Interests is { } interests && !interests.Any(resident.Interests.Contains)) return false;
        if (who.Phases is { } phases && !phases.Contains(resident.Phase)) return false;
        if (who.Traits is { } traits)
        {
            var values = resident.Traits.Values;
            foreach (var (trait, least) in traits)
                if (values[IndexOf(trait)] < least) return false;
        }
        return true;
    }

    // the standing buildings that serve a label, with their range
    private List<(StructureView Building, string Label, int? Range)> Serving()
    {
        var standing = _village.Find<IStructures>()?.Standing() ?? [];
        return [.. standing.Where(building => _blueprints.TryGetValue(building.Blueprint, out var plan) && plan.Serves is not null)
            .Select(building => (building, _blueprints[building.Blueprint].Serves!, _blueprints[building.Blueprint].Range))];
    }

    private bool Fulfilled(List<(StructureView Building, string Label, int? Range)> serving, Resident resident, Wish wish) =>
        Fulfilled(serving, resident.Island, _village.Find<IPlaces>()?.HomeOf(resident.Id), wish);

    // without a home nothing is within range; a building without a range serves the whole island
    private static bool Fulfilled(List<(StructureView Building, string Label, int? Range)> serving, int island, Cell? home, Wish wish) =>
        home is { } at && serving.Any(item => item.Building.Island == island && item.Label == wish.Label
            && (item.Range is not { } range || item.Building.Place.DistanceTo(at) <= range));

    private static int IndexOf(string trait)
    {
        for (var i = 0; i < Personality.Traits.Count; i++)
            if (Personality.Traits[i] == trait) return i;
        return -1;
    }

    // ── the data ──

    private void Read(ModuleContext village)
    {
        _village = village;
        _settings = village.Data.Section<WishSettings>("wishes");
        _wishes = _settings.Wishes;
        _byId = _wishes.ToDictionary(wish => wish.Id);
        var catalog = village.Data.Section<Catalog>("catalog");
        _blueprints = catalog.Blueprints.ToDictionary(plan => plan.Id);
        _served = [.. catalog.Blueprints.Where(plan => plan.Serves is not null).Select(plan => plan.Serves!)];
        var ids = catalog.Materials.Select(material => material.Id).Concat(catalog.Blueprints.Select(plan => plan.Id)).ToHashSet();
        _known = [.. _wishes.Where(wish => wish.After?.All(ids.Contains) ?? true).Select(wish => wish.Id)];

        var interests = village.Data.Section<PeopleSettings>("people").Interests.Select(interest => interest.Id).ToHashSet();
        if (_settings.EveryTicks <= 0 || _settings.TaskThreshold < 0)
            throw new InvalidDataException("The data section 'wishes' needs everyTicks above 0 and a taskThreshold of at least 0.");
        foreach (var kind in new[] { Opened, Closed, HeldKind })
            if (!_settings.Log.ContainsKey(kind)) throw new InvalidDataException($"The data section 'wishes' has no log sentence for {kind}.");
        foreach (var wish in _wishes)
        {
            if (wish.Weight < 0 || wish.AfterDays < 0)
                throw new InvalidDataException($"The wish '{wish.Id}' has a weight or afterDays below 0.");
            if (wish.Who?.Interests?.FirstOrDefault(interest => !interests.Contains(interest)) is { } interest)
                throw new InvalidDataException($"The wish '{wish.Id}' names the unknown interest '{interest}'.");
            if (wish.Who?.Traits?.Keys.FirstOrDefault(trait => IndexOf(trait) < 0) is { } trait)
                throw new InvalidDataException($"The wish '{wish.Id}' names the unknown trait '{trait}'.");
        }
    }

    // ── saving ──

    public JsonNode Save() => JsonSerializer.SerializeToNode(new SavedWishes(_began, _task, [.. _held.Order(StringComparer.Ordinal)]), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedWishes>(SaveJson.Options) ?? throw new InvalidDataException("The wishes part of the save is empty.");
        _began = saved.Began;
        _task = saved.Task;
        _held.Clear();
        _held.UnionWith(saved.Held ?? []);
    }

    private sealed record SavedWishes(DateTimeOffset Began, OpenTask? Task = null, IReadOnlyList<string>? Held = null);
}
