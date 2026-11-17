using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.People;
using Codery.Village.Modules.Wishes;
using Codery.Village.Modules.Work;
using DayModule = Codery.Village.Modules.Day.Day;
using PeopleModule = Codery.Village.Modules.People.People;
using WishesModule = Codery.Village.Modules.Wishes.Wishes;

namespace Codery.Village.Modules.Happiness;

/// <summary>
/// How happy each resident is, from 0 up to and including 100. The target is <c>base</c> plus the points of his
/// reasons: a home, each need of the economy that his island has in stock, work (for who is of working age), free
/// time had today, and each wish that counts for him, fulfilled or not. A minus weighs
/// <c>1 + sensitivity / 100</c> times. Happiness follows the target by at most <c>maxStepPerHour</c> per calendar
/// hour; a newcomer starts at his target. A reason whose module does not run is left out. Happiness is told, never
/// a threshold for growth.
/// </summary>
public sealed class Happiness : IModule
{
    private const string Home = "home";
    private const string Work = "work";
    private const string Free = "leisure";
    private const string WishPrefix = "wish:";

    private readonly Dictionary<int, int> _values = [];
    // the calendar day each resident last had free time
    private readonly Dictionary<int, DateOnly> _leisure = [];
    // the calendar hour (UTC) happiness last moved
    private DateTimeOffset _hour;
    private HappinessSettings _settings = null!;
    private List<(string Need, HashSet<string> Goods)> _needs = [];
    private WishPoints? _wishPoints;
    private ModuleContext _village = null!;

    public string Name => "happiness";

    public int Version => 1;

    /// <summary>How happy the resident is now. Throws <see cref="KeyNotFoundException"/> for someone who does not live here.</summary>
    public int Of(int resident) =>
        _values.TryGetValue(resident, out var value) ? value : throw new KeyNotFoundException($"No resident {resident} lives in the village.");

    /// <summary>
    /// What makes up the resident's target now: <c>home</c>, the needs (<c>food</c>, <c>heat</c>), <c>work</c>,
    /// <c>leisure</c> and <c>wish:</c> plus the wish id, with their points; a minus is already weighed.
    /// </summary>
    public IReadOnlyList<(string Reason, int Points)> Reasons(int resident)
    {
        var who = _village.Module<PeopleModule>().Get(resident);
        return Reasons(who, Today());
    }

    public void Start(ModuleContext village)
    {
        Read(village);
        _values.Clear();
        _leisure.Clear();
        // everyone starts at his target on the first tick, when the households have moved into their homes
        _hour = HourOf(village);
    }

    public void Step(ModuleContext village)
    {
        if (village.Find<PeopleModule>() is not { } people) return;
        var today = Today();

        NoteLeisure(people);

        // a newcomer starts at his target; who left is forgotten
        if (people.All.Count != _values.Count || people.All.Any(resident => !_values.ContainsKey(resident.Id)))
        {
            var living = people.All.Select(resident => resident.Id).ToHashSet();
            foreach (var gone in _values.Keys.Where(id => !living.Contains(id)).ToList())
            {
                _values.Remove(gone);
                _leisure.Remove(gone);
            }
            foreach (var resident in people.All.Where(resident => !_values.ContainsKey(resident.Id)))
                _values[resident.Id] = Target(resident, today);
        }

        var hour = HourOf(village);
        if (hour < _hour) _hour = hour;
        if (hour <= _hour) return;
        var step = (long)_settings.MaxStepPerHour * (long)(hour - _hour).TotalHours;
        _hour = hour;
        foreach (var resident in people.All)
        {
            var value = _values[resident.Id];
            _values[resident.Id] = (int)(value + Math.Clamp(Target(resident, today) - value, -step, step));
        }
    }

    // who is at his free time now has had it today
    private void NoteLeisure(PeopleModule people)
    {
        if (_village.Find<DayModule>() is not { } day) return;
        var today = Today();
        foreach (var resident in people.All)
        {
            try
            {
                if (_settings.Leisure.Contains(day.Of(resident.Id).Activity)) _leisure[resident.Id] = today;
            }
            catch (KeyNotFoundException)
            {
                // a newcomer Day has not planned yet
            }
        }
    }

    private int Target(Resident resident, DateOnly today) =>
        Math.Clamp(_settings.Base + Reasons(resident, today).Sum(reason => reason.Points), 0, 100);

    private List<(string Reason, int Points)> Reasons(Resident resident, DateOnly today)
    {
        var reasons = new List<(string, int)>();
        void Add(string reason, bool had) => reasons.Add((reason, Weigh(had ? _settings.Reasons[reason].Plus : _settings.Reasons[reason].Minus, resident)));

        if (_village.Find<IHousing>() is { } housing) Add(Home, resident.Household is { } household && housing.HasHome(household));
        // Eddie's islet is outside the economy
        if (_village.Find<IStock>() is { } stock && _village.Map.Islands.Any(island => island.Id == resident.Island && !island.Eddie))
            foreach (var (need, goods) in _needs)
                Add(need, goods.Any(material => stock.Count(resident.Island, material) > 0));
        if (_village.Find<IJobs>() is { } jobs && resident.Phase == LifePhase.Working) Add(Work, jobs.JobOf(resident.Id) is not null);
        if (_village.Find<DayModule>() is not null) Add(Free, _leisure.TryGetValue(resident.Id, out var day) && day == today);
        if (_village.Find<WishesModule>() is { } wishes && _wishPoints is { } points)
            foreach (var wish in wishes.Of(resident))
                reasons.Add((WishPrefix + wish, Weigh(wishes.Fulfilled(resident, wish) ? points.Fulfilled : points.Unfulfilled, resident)));
        return reasons;
    }

    // a minus weighs more for the sensitive
    private static int Weigh(int points, Resident resident) =>
        points >= 0 ? points : (int)Math.Round(points * (1 + resident.Traits.Sensitivity / 100.0), MidpointRounding.AwayFromZero);

    private DateOnly Today() => DateOnly.FromDateTime(_village.Clocks.Calendar.Now.DateTime);

    private static DateTimeOffset HourOf(ModuleContext village)
    {
        var now = village.Clocks.Calendar.Now.ToUniversalTime();
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
    }

    // ── the data ──

    private void Read(ModuleContext village)
    {
        _village = village;
        _settings = village.Data.Section<LifeWithHappiness>("life").Happiness;
        var economy = village.Data.Section<EconomySettings>("economy");
        var materials = village.Data.Section<Catalog>("catalog").Materials;
        _needs = [.. economy.Needs.Keys.Select(need => (need, materials.Where(material => material.Satisfies?.Contains(need) == true).Select(material => material.Id).ToHashSet()))];
        _wishPoints = village.Data.Names.Contains("wishes") ? village.Data.Section<WishSettings>("wishes").Points : null;

        if (_settings.MaxStepPerHour <= 0) throw new InvalidDataException("The data section 'life' needs happiness.maxStepPerHour above 0.");
        var missing = new[] { Home, Work, Free }.Concat(economy.Needs.Keys).Where(reason => !_settings.Reasons.ContainsKey(reason)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"The data section 'life' has no happiness reason for {string.Join(", ", missing)}.");
    }

    // ── saving ──

    public JsonNode Save() =>
        JsonSerializer.SerializeToNode(new SavedHappiness(_hour,
            [.. _values.OrderBy(item => item.Key).Select(item => new SavedResident(item.Key, item.Value, _leisure.TryGetValue(item.Key, out var day) ? day : null))]),
            SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedHappiness>(SaveJson.Options) ?? throw new InvalidDataException("The happiness part of the save is empty.");
        _hour = saved.Hour;
        _values.Clear();
        _leisure.Clear();
        foreach (var resident in saved.Residents)
        {
            _values[resident.Id] = resident.Value;
            if (resident.Leisure is { } day) _leisure[resident.Id] = day;
        }
    }

    private sealed record SavedHappiness(DateTimeOffset Hour, IReadOnlyList<SavedResident> Residents);

    private sealed record SavedResident(int Id, int Value, DateOnly? Leisure = null);
}
