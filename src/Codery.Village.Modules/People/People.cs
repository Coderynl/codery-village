using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Contracts;

namespace Codery.Village.Modules.People;

/// <summary>
/// The residents: who lives where, in which household, and how their life goes. Each ordinary island starts
/// with twelve to fifteen people; Eddie lives on his own islet. People grow up, may study, work, retire and
/// move to the coast. Newcomers come when there is a free home. Nobody dies.
/// </summary>
public sealed class People : IModule
{
    /// <summary>The reason of everyone who leaves: they move to the coast.</summary>
    public const string Coast = "coast";

    private const string Arrived = "arrived";
    private const string LeftCoast = "left-coast";
    private static readonly string[] LogKinds = [Arrived, LeftCoast, .. new[] { LifePhase.Student, LifePhase.Working, LifePhase.Retired }.Select(PhaseKind)];

    private readonly List<Resident> _residents = [];
    private PeopleSettings _settings = null!;
    private LifeCourse _course = null!;
    private Dictionary<string, Interest> _interests = [];
    private Clocks _clocks = null!;
    private int _nextResident;
    private int _nextHousehold;
    // the calendar day newcomers were last looked for
    private DateOnly _day;

    public string Name => "people";

    public int Version => 1;

    /// <summary>Everyone who lives in the village, in the order they came.</summary>
    public IReadOnlyList<Resident> All => _residents.AsReadOnly();

    /// <summary>Throws <see cref="KeyNotFoundException"/> for someone who does not live here (any more).</summary>
    public Resident Get(int id) =>
        _residents.Find(resident => resident.Id == id) ?? throw new KeyNotFoundException($"No resident {id} lives in the village.");

    public IEnumerable<Resident> On(int island) => _residents.Where(resident => resident.Island == island);

    /// <summary>The age in life years now. Eddie stays the age the data gives him.</summary>
    public double AgeOf(Resident resident) =>
        resident.IsEddie ? _course.EddieAge : _clocks.Life.YearsAt(_clocks.Tick - resident.BornTick);

    public void Start(ModuleContext village)
    {
        Read(village);
        _day = Today(village);
        foreach (var island in village.Map.Islands)
        {
            if (island.Eddie)
            {
                if (!_residents.Any(resident => resident.IsEddie)) AddEddie(village, island.Id);
                continue;
            }
            var count = _settings.StartMin + village.Random.Next(_settings.StartMax - _settings.StartMin + 1);
            for (var placed = 0; placed < count;) placed += AddHousehold(village, island.Id, count - placed).Count;
        }
    }

    public void Step(ModuleContext village)
    {
        Live(village);

        var today = Today(village);
        var newDay = today != _day;
        _day = today;

        // without homes everyone counts as housed and nobody new comes
        if (village.Find<IHousing>() is not { } housing) return;
        MoveIn(housing);
        if (newDay) Arrive(village, housing);
    }

    // phases follow the age; the retired move to the coast after their years
    private void Live(ModuleContext village)
    {
        var tick = village.Clocks.Tick;
        var leaveAt = _course.RetireFrom + _course.RetiredYears;
        List<Resident>? leaving = null;
        foreach (var resident in _residents)
        {
            if (resident.IsEddie) continue;
            var age = AgeOf(resident);
            if (age >= leaveAt)
            {
                (leaving ??= []).Add(resident);
                continue;
            }
            var phase = PhaseAt(resident, age);
            if (phase == resident.Phase) continue;
            resident.Phase = phase;
            village.Publish(new ResidentPhaseChanged(tick, resident.Id, phase));
            village.Log(new LogEntry(tick, PhaseKind(phase), resident.Name));
        }
        if (leaving is null) return;
        foreach (var resident in leaving)
        {
            _residents.Remove(resident);
            village.Publish(new ResidentLeft(tick, resident.Id, Coast, resident.Household, resident.Island, resident.Money));
            village.Log(new LogEntry(tick, LeftCoast, resident.Name));
        }
    }

    // a household without a home takes a free one on its island
    private void MoveIn(IHousing housing)
    {
        var seen = new HashSet<int>();
        foreach (var resident in _residents)
        {
            if (resident.Household is not { } household || !seen.Add(household) || housing.HasHome(household)) continue;
            if (housing.FreeHome(resident.Island) is { } home) housing.MoveIn(household, home);
        }
    }

    // once a calendar day, per island with a free home, a household may come
    private void Arrive(ModuleContext village, IHousing housing)
    {
        foreach (var island in village.Map.Islands)
        {
            if (island.Eddie || housing.FreeHome(island.Id) is not { } home) continue;
            var living = _residents.Count(resident => resident.Island == island.Id);
            var chance = _settings.ArrivalChance / (1 + living / _settings.ArrivalDamping);
            if (village.Random.NextDouble() >= chance) continue;

            var newcomers = AddHousehold(village, island.Id, int.MaxValue);
            housing.MoveIn(newcomers[0].Household!.Value, home);
            foreach (var resident in newcomers)
            {
                village.Publish(new ResidentArrived(village.Clocks.Tick, resident.Id));
                village.Log(new LogEntry(village.Clocks.Tick, Arrived, resident.Name));
            }
        }
    }

    private LifePhase PhaseAt(Resident resident, double age)
    {
        if (resident.IsEddie) return LifePhase.Working;
        if (age < _course.WorkFrom) return LifePhase.Child;
        if (age >= _course.RetireFrom) return LifePhase.Retired;
        if (age < _course.WorkFrom + _course.StudyYears && Studies(resident)) return LifePhase.Student;
        return LifePhase.Working;
    }

    // open, with an interest that has a study
    private bool Studies(Resident resident) =>
        resident.Traits.Openness > 50
        && resident.Interests.Any(id => _interests.TryGetValue(id, out var interest) && interest.Study);

    private static string PhaseKind(LifePhase phase) => "phase-" + phase.ToString().ToLowerInvariant();

    // ── making people ──
    // Every draw is made whatever the data holds, so the same seed gives the same people with other names.

    /// <summary>A couple with or without children, or a single, of at most <paramref name="room"/> people.</summary>
    private List<Resident> AddHousehold(ModuleContext village, int island, int room)
    {
        var random = village.Random;
        var kind = Pick(random, _settings.Households, kind => kind.Weight);
        var adults = Math.Min(kind.Adults, room);
        var children = adults < 2 ? 0 : Math.Min(kind.Children, room - adults);
        var household = _nextHousehold++;

        var band = Pick(random, children > 0 ? _settings.ParentAges : _settings.AdultAges, band => band.Weight);
        var first = band.From + random.Next(band.To - band.From + 1);
        var youngest = first;
        var made = new List<Resident>();
        for (var i = 0; i < adults; i++)
        {
            var whole = i == 0 ? first : Math.Clamp(first + random.Next(2 * _settings.PartnerGap + 1) - _settings.PartnerGap, band.From, band.To);
            youngest = Math.Min(youngest, whole);
            made.Add(Add(village, island, household, whole + random.NextDouble()));
        }
        for (var i = 0; i < children; i++)
        {
            var childBand = Pick(random, _settings.ChildAges, band => band.Weight);
            var whole = Math.Min(childBand.From + random.Next(childBand.To - childBand.From + 1), Math.Max(0, youngest - _settings.ParentGap));
            made.Add(Add(village, island, household, whole + random.NextDouble()));
        }
        return made;
    }

    private Resident Add(ModuleContext village, int island, int household, double age)
    {
        var random = village.Random;
        var sex = random.Next(2) == 0 ? Sex.Man : Sex.Woman;
        var name = FreeName(sex == Sex.Man ? _settings.Names.Man : _settings.Names.Woman, random.Next(int.MaxValue));
        var resident = new Resident(_nextResident++, name, sex, village.Clocks.Tick - village.Clocks.Life.TickOfAge(age),
            DrawTraits(random), DrawInterests(random), island, household, LifePhase.Child, isEddie: false, money: 0);
        resident.Phase = PhaseAt(resident, age);
        _residents.Add(resident);
        return resident;
    }

    private void AddEddie(ModuleContext village, int island)
    {
        var random = village.Random;
        _residents.Add(new Resident(_nextResident++, _settings.EddieName, Sex.Man,
            village.Clocks.Tick - village.Clocks.Life.TickOfAge(_course.EddieAge),
            DrawTraits(random), DrawInterests(random), island, _nextHousehold++, LifePhase.Working, isEddie: true, money: 0));
    }

    // the drawn name, or the next one in the list that nobody here has yet; a draw, not a search, so the stream stays the same
    private string FreeName(IReadOnlyList<string> names, int drawn)
    {
        for (var step = 0; step < names.Count; step++)
        {
            var name = names[(drawn + step) % names.Count];
            if (!_residents.Any(resident => resident.Name == name)) return name;
        }
        return names[drawn % names.Count];
    }

    // per trait a normal draw, the sum of twelve uniform draws less six, held between 0 and 100
    private Personality DrawTraits(Rng random)
    {
        var spreads = _settings.Personality.All;
        var values = new int[spreads.Count];
        for (var i = 0; i < spreads.Count; i++)
        {
            var z = -6.0;
            for (var k = 0; k < 12; k++) z += random.NextDouble();
            var value = Math.Round(spreads[i].Mean + spreads[i].Spread * z, MidpointRounding.AwayFromZero);
            values[i] = (int)Math.Clamp(value, 0, 100);
        }
        return Personality.From(values);
    }

    // one to three different interests
    private List<string> DrawInterests(Rng random)
    {
        var all = _settings.Interests;
        var count = 1 + random.Next(Math.Min(3, all.Count));
        var chosen = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var at = random.Next(all.Count);
            while (chosen.Contains(all[at].Id)) at = (at + 1) % all.Count;
            chosen.Add(all[at].Id);
        }
        return chosen;
    }

    private static T Pick<T>(Rng random, IReadOnlyList<T> items, Func<T, int> weight)
    {
        var roll = random.Next(items.Sum(weight));
        foreach (var item in items)
        {
            roll -= weight(item);
            if (roll < 0) return item;
        }
        return items[^1];
    }

    private static DateOnly Today(ModuleContext village) => DateOnly.FromDateTime(village.Clocks.Calendar.Now.DateTime);

    private void Read(ModuleContext village)
    {
        _settings = village.Data.Section<PeopleSettings>("people");
        _course = village.Data.Section<LifeCourse>("life");
        _interests = _settings.Interests.ToDictionary(interest => interest.Id);
        _clocks = village.Clocks;
        var missing = LogKinds.Where(kind => !_settings.Log.ContainsKey(kind)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"The data section 'people' has no log sentence for {string.Join(", ", missing)}.");
    }

    // ── saving ──

    public JsonNode Save() =>
        JsonSerializer.SerializeToNode(new SavedPeople(_nextResident, _nextHousehold, _day, [.. _residents.Select(SavedResident.Of)]), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedPeople>(SaveJson.Options) ?? throw new InvalidDataException("The people part of the save is empty.");
        _nextResident = saved.NextResident;
        _nextHousehold = saved.NextHousehold;
        _day = saved.Day;
        _residents.Clear();
        _residents.AddRange(saved.Residents.Select(resident => resident.ToResident()));
    }

    private sealed record SavedPeople(int NextResident, int NextHousehold, DateOnly Day, IReadOnlyList<SavedResident> Residents);

    private sealed record SavedResident(int Id, string Name, Sex Sex, long BornTick, Personality Traits, IReadOnlyList<string> Interests,
        int Island, int? Household, LifePhase Phase, bool IsEddie, long Money)
    {
        public static SavedResident Of(Resident r) =>
            new(r.Id, r.Name, r.Sex, r.BornTick, r.Traits, r.Interests, r.Island, r.Household, r.Phase, r.IsEddie, r.Money);

        public Resident ToResident() => new(Id, Name, Sex, BornTick, Traits, Interests, Island, Household, Phase, IsEddie, Money);
    }
}
