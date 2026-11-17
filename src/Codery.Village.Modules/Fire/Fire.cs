using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.Day;
using BuildingModule = Codery.Village.Modules.Building.Building;
using DayModule = Codery.Village.Modules.Day.Day;
using PeopleModule = Codery.Village.Modules.People.People;

namespace Codery.Village.Modules.Fire;

/// <summary>
/// The fires of the islands. A fireplace burns with a strength from 0 (out) to 3. At dusk its fire keeper goes to
/// it and lights it, and through the night he tends it every <c>fireFeedMinutes</c>: each time a piece of fuel from
/// the island's stock (a material that meets the need <c>fireFuel</c>) brings it back to 3. He leaves the homes what
/// they need of it for <c>fireReserveDays</c> days. A tending that cannot be done, for want of fuel to spare or of a
/// keeper who is awake, lets it sink a step. By day it smoulders at 1 and goes out after
/// <c>fireSmoulderMinutes</c>. Dusk and dawn come per month from the section "day".
/// </summary>
public sealed class Fire : IModule
{
    public const int Full = 3;

    private readonly SortedDictionary<int, Hearth> _fires = [];
    private FireSettings _settings = null!;
    private string? _fireplace;
    private List<string> _fuel = [];
    // what the island needs of the fuel's need a day: per resident and per household
    private (int PerPerson, int PerHousehold) _need;
    private TimeOnly[] _dusk = [];
    private TimeOnly[] _dawn = [];

    public string Name => "fire";

    public int Version => 1;

    /// <summary>How strong the fire of the fireplace burns, from 0 (out) to 3; 0 for anything else.</summary>
    public int Strength(int structure) => _fires.TryGetValue(structure, out var fire) ? fire.Strength : 0;

    /// <summary>Every fireplace that stands, by id, with the strength of its fire.</summary>
    public IReadOnlyList<(int Structure, int Strength)> All => [.. _fires.Select(fire => (fire.Key, fire.Value.Strength))];

    public void Start(ModuleContext village)
    {
        Read(village);
        _fires.Clear();
        // the fireplaces that stand from the start, cold
        foreach (var structure in village.Find<BuildingModule>()?.All ?? [])
            if (structure.Done && structure.Blueprint == _fireplace) _fires[structure.Id] = new Hearth();
    }

    public void Step(ModuleContext village)
    {
        if (_fireplace is null || village.Find<BuildingModule>() is not { } building) return;
        var now = village.Clocks.Calendar.Now;
        var night = IsNight(now);
        var hearths = building.All.Where(structure => structure.Done && structure.Blueprint == _fireplace).ToList();
        foreach (var gone in _fires.Keys.Where(id => hearths.All(structure => structure.Id != id)).ToList()) _fires.Remove(gone);

        // a keeper who came to the fire tends it
        foreach (var reached in village.Received.OfType<Reached>())
        {
            if (reached.Errand != Activity.Tend || !night) continue;
            foreach (var structure in hearths)
                if (structure.Anchor == reached.At && _fires.TryGetValue(structure.Id, out var fire) && fire.Asked is not null)
                {
                    fire.Asked = null;
                    Tend(village, fire, structure.Island);
                }
        }

        foreach (var structure in hearths)
        {
            if (!_fires.TryGetValue(structure.Id, out var fire)) _fires[structure.Id] = fire = new Hearth();
            if (night)
            {
                fire.Since = null;
                if (fire.Due is not { } due || now >= due) Act(village, structure.Id, structure.Island, structure.Anchor, fire, now);
                continue;
            }
            // by day it smoulders, and goes out
            fire.Due = null;
            fire.Asked = null;
            if (fire.Strength > 1) (fire.Strength, fire.Since) = (1, now);
            else if (fire.Strength == 1 && fire.Since is null) fire.Since = now;
            else if (fire.Strength == 1 && now - fire.Since >= TimeSpan.FromMinutes(_settings.FireSmoulderMinutes)) (fire.Strength, fire.Since) = (0, null);
        }
    }

    // the fire is to be lit or fed: the keeper does it if he is there, goes there if he is awake, and else it sinks
    private void Act(ModuleContext village, int id, int island, Cell spot, Hearth fire, DateTimeOffset now)
    {
        // asked before, and not come: that feed was missed
        if (fire.Asked is not null) Sink(fire);
        fire.Asked = null;
        fire.Due = now + TimeSpan.FromMinutes(_settings.FireFeedMinutes);
        if (village.Find<DayModule>() is not { } day || Keeper(village, id) is not { } keeper)
        {
            Sink(fire);
            return;
        }
        Doing doing;
        try
        {
            doing = day.Of(keeper);
        }
        catch (KeyNotFoundException)
        {
            Sink(fire);
            return;
        }
        if (doing is { Walk: null, Inside: false } && doing.At == spot) Tend(village, fire, island);
        else if (doing.Activity != Activity.Sleep && day.Send(keeper, new Errand(Activity.Tend, spot))) fire.Asked = now;
        else Sink(fire);
    }

    // a piece of fuel brings it back to full, if the homes keep what they need; else it sinks
    private void Tend(ModuleContext village, Hearth fire, int island)
    {
        var stock = village.Find<IStock>();
        var fuel = _fuel.FirstOrDefault(material => stock?.Count(island, material) > 0);
        var spare = stock is null ? 0 : _fuel.Sum(material => stock.Count(island, material)) - Reserve(village, island);
        if (fuel is not null && spare > 0 && stock!.TryTake(island, new Dictionary<string, int> { [fuel] = 1 })) fire.Strength = Full;
        else Sink(fire);
    }

    // what the homes of the island need of the fuel's need in fireReserveDays days
    private int Reserve(ModuleContext village, int island)
    {
        if (village.Find<PeopleModule>() is not { } people) return 0;
        var residents = people.On(island).ToList();
        var households = residents.Select(resident => resident.Household ?? -1 - resident.Id).Distinct().Count();
        return (int)Math.Ceiling(_settings.FireReserveDays * (_need.PerPerson * residents.Count + _need.PerHousehold * households));
    }

    private static void Sink(Hearth fire) => fire.Strength = Math.Max(0, fire.Strength - 1);

    // the resident whose job is at the fireplace
    private static int? Keeper(ModuleContext village, int fireplace)
    {
        if (village.Find<IJobs>() is not { } jobs || village.Find<PeopleModule>() is not { } people) return null;
        foreach (var resident in people.All)
            if (jobs.JobOf(resident.Id) is { } job && job.Building == fireplace) return resident.Id;
        return null;
    }

    private bool IsNight(DateTimeOffset now)
    {
        var time = TimeOnly.FromDateTime(now.DateTime);
        return time >= _dusk[now.Month - 1] || time < _dawn[now.Month - 1];
    }

    private void Read(ModuleContext village)
    {
        _settings = village.Data.Section<FireSettings>("day");
        _fireplace = village.Data.Names.Contains("building") ? village.Data.Section<FireBuilding>("building").Fireplace : null;
        var catalog = village.Data.Names.Contains("catalog") ? village.Data.Section<Catalog>("catalog") : null;
        _fuel = [.. catalog?.Materials.Where(material => material.Satisfies?.Contains(_settings.FireFuel) == true).Select(material => material.Id) ?? []];
        var need = village.Data.Names.Contains("economy") && village.Data.Section<FireEconomy>("economy").Needs.TryGetValue(_settings.FireFuel, out var found) ? found : null;
        _need = (need?.PerPerson ?? 0, need?.PerHousehold ?? 0);
        if (_settings.Dusk.Count != 12 || _settings.Dawn.Count != 12 || _settings.FireFeedMinutes < 1 || _settings.FireSmoulderMinutes < 0 || _settings.FireReserveDays < 0)
            throw new InvalidDataException("The data section 'day' needs dusk and dawn for twelve months, and fire minutes in range.");
        _dusk = [.. _settings.Dusk.Select(Time)];
        _dawn = [.. _settings.Dawn.Select(Time)];
    }

    private static TimeOnly Time(string text) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new InvalidDataException($"The data section 'day' has the time '{text}', which is not HH:mm.");

    // ── saving ──

    public JsonNode Save() =>
        JsonSerializer.SerializeToNode(new SavedFire([.. _fires.Select(fire => new SavedHearth(fire.Key, fire.Value.Strength, fire.Value.Due, fire.Value.Asked, fire.Value.Since))]),
            SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedFire>(SaveJson.Options) ?? throw new InvalidDataException("The fire part of the save is empty.");
        _fires.Clear();
        foreach (var fire in saved.Fires)
            _fires[fire.Structure] = new Hearth { Strength = fire.Strength, Due = fire.Due, Asked = fire.Asked, Since = fire.Since };
    }

    private sealed record SavedFire(IReadOnlyList<SavedHearth> Fires);

    private sealed record SavedHearth(int Structure, int Strength, DateTimeOffset? Due = null, DateTimeOffset? Asked = null, DateTimeOffset? Since = null);

    /// <summary>A fire: how strong it burns, when it is next to be tended, since when its keeper is on his way, since when it smoulders.</summary>
    private sealed class Hearth
    {
        public int Strength { get; set; }
        public DateTimeOffset? Due { get; set; }
        public DateTimeOffset? Asked { get; set; }
        public DateTimeOffset? Since { get; set; }
    }

    /// <summary>What the fire reads from the section "day".</summary>
    private sealed record FireSettings(IReadOnlyList<string> Dusk, IReadOnlyList<string> Dawn, int FireFeedMinutes = 60, int FireSmoulderMinutes = 180,
        string FireFuel = "heat", double FireReserveDays = 0);

    /// <summary>What the fire reads from the section "economy": what the homes need a day.</summary>
    private sealed record FireEconomy(IReadOnlyDictionary<string, FireNeed> Needs);

    private sealed record FireNeed(int PerPerson = 0, int PerHousehold = 0);

    /// <summary>What the fire reads from the section "building": which blueprint is the fireplace.</summary>
    private sealed record FireBuilding(string? Fireplace = null);
}
