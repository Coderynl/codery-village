using System.Globalization;
using System.Text.Json;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Building;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.People;
using Codery.Village.Modules.Wishes;
using BuildingModule = Codery.Village.Modules.Building.Building;
using Calendar = Codery.Village.Core.Time.Calendar;
using DayModule = Codery.Village.Modules.Day.Day;
using HappinessModule = Codery.Village.Modules.Happiness.Happiness;
using PeopleModule = Codery.Village.Modules.People.People;
using TalkModule = Codery.Village.Modules.Talk.Talk;
using WishesModule = Codery.Village.Modules.Wishes.Wishes;

namespace Codery.Village.Server.Feed;

/// <summary>
/// Turns the village into protocol 1: the whole of it for a page that joins, and after every tick what changed
/// since the tick before. It remembers what it sent, so it sends nothing twice. Numbers that change a little all
/// the time are rounded first: happiness to 5, the progress of a site to steps of 10, the calendar to the quarter.
/// </summary>
public sealed class VillageFeed
{
    public const int TicksPerSecond = Clocks.TicksPerSecond;

    /// <summary>How many lines of the diary a page gets when it joins.</summary>
    public const int HistoryLines = 20;

    private readonly IslandMap _map;
    private readonly Calendar _calendar;
    private readonly MapView _view;
    private readonly LogText _text;
    private readonly Dictionary<string, Blueprint> _blueprints;
    private readonly int _ticksPerCell;
    private readonly int _happinessAtStart;
    private readonly (TimeOnly From, TimeOnly To) _night;

    // what was sent
    private readonly Dictionary<int, Move> _moves = [];
    private readonly Dictionary<int, Person> _people = [];
    private readonly Dictionary<int, (int Phase, int Progress, bool Done)> _sites = [];
    private readonly Dictionary<int, int> _things = [];
    private CalendarView? _calendarSent;
    private TaskView? _task;
    // the number of the first log line not passed on yet, and the last conversation passed on
    private long _logged;
    private long _talked;
    private bool _started;

    public VillageFeed(DataSet data, IslandMap map, TimeProvider time)
    {
        _map = map;
        _calendar = new Calendar(time);
        var catalog = data.Section<Catalog>("catalog");
        _blueprints = catalog.Blueprints.ToDictionary(blueprint => blueprint.Id);
        var wishes = data.Names.Contains("wishes") ? data.Section<WishSettings>("wishes").Wishes : [];
        _text = new LogText(data, catalog, wishes);
        var day = data.Section<FeedDay>("day");
        _ticksPerCell = day.TicksPerCell;
        _happinessAtStart = data.Section<FeedLife>("life").Happiness.Base;
        _night = (TimeOnly.ParseExact(day.Night.From, "HH:mm", CultureInfo.InvariantCulture), TimeOnly.ParseExact(day.Night.To, "HH:mm", CultureInfo.InvariantCulture));
        _view = View(map, catalog, wishes);
    }

    /// <summary>The whole village, for a page that has just joined.</summary>
    public JoinMessage Join(Engine engine)
    {
        if (!_started) Remember(engine);
        var now = Now.Of(engine);
        return new JoinMessage(
            Protocol.Version,
            _view,
            [.. People(now).Select(item => item.Person)],
            [.. Moves(now)],
            [.. Sites(now)],
            [.. Things(now)],
            Calendar(),
            Task(now),
            History(engine),
            engine.Tick,
            TicksPerSecond);
    }

    /// <summary>What changed since the last call, with what residents said besides their conversations.</summary>
    public TickMessage Next(Engine engine, IReadOnlyList<Said>? said = null)
    {
        if (!_started) Remember(engine);
        var now = Now.Of(engine);

        List<Move>? moves = null;
        var here = new HashSet<int>();
        foreach (var move in Moves(now))
        {
            here.Add(move.Person);
            if (_moves.TryGetValue(move.Person, out var sent) && Same(sent, move)) continue;
            _moves[move.Person] = move;
            (moves ??= []).Add(move);
        }

        List<Person>? people = null;
        foreach (var (id, person) in People(now))
        {
            here.Add(id);
            if (_people.TryGetValue(id, out var sent) && Same(sent, person)) continue;
            _people[id] = person;
            (people ??= []).Add(person);
        }
        List<int>? left = null;
        foreach (var id in _people.Keys.Concat(_moves.Keys).Distinct().Where(id => !here.Contains(id)).Order().ToList())
        {
            _people.Remove(id);
            _moves.Remove(id);
            (left ??= []).Add(id);
        }

        List<Site>? sites = null;
        var standing = new HashSet<int>();
        foreach (var site in Sites(now))
        {
            standing.Add(site.Id);
            var key = (site.Phase, site.Progress, site.Done);
            if (_sites.TryGetValue(site.Id, out var sent) && sent == key) continue;
            _sites[site.Id] = key;
            (sites ??= []).Add(site);
        }
        List<int>? gone = null;
        foreach (var id in _sites.Keys.Where(id => !standing.Contains(id)).Order().ToList())
        {
            _sites.Remove(id);
            (gone ??= []).Add(id);
        }

        List<Thing>? things = null;
        foreach (var thing in Things(now))
        {
            if (_things.TryGetValue(thing.Id, out var cell) && cell == thing.Cell) continue;
            _things[thing.Id] = thing.Cell;
            (things ??= []).Add(thing);
        }

        var calendar = Calendar();
        var newQuarter = calendar != _calendarSent;
        _calendarSent = calendar;

        var task = Task(now);
        var newTask = task is not null && task != _task;
        var noTask = task is null && _task is not null;
        _task = task;

        List<Said>? spoken = null;
        foreach (var conversation in now.Talk?.Recent.Where(conversation => conversation.Id > _talked) ?? [])
        {
            (spoken ??= []).Add(new Said(conversation.A, conversation.B, conversation.Lines.Nl, conversation.Lines.En));
            _talked = conversation.Id;
        }
        if (said is { Count: > 0 }) (spoken ??= []).AddRange(said);

        // counted in line numbers, not places in the list: the village may have forgotten older lines in between
        List<FeedEvent>? events = null;
        foreach (var (_, entry) in engine.LogFrom(_logged))
            if (_text.Render(entry) is { } line)
                (events ??= []).Add(new FeedEvent(entry.Kind, line.Nl, line.En));
        _logged = engine.LogEnd;

        return new TickMessage(engine.Tick, moves, people, left, sites, gone, things,
            newQuarter ? calendar : null, newTask ? task : null, noTask ? true : null, spoken, events);
    }

    /// <summary>Takes the village as it is as the starting point: what is there now is not news.</summary>
    private void Remember(Engine engine)
    {
        _started = true;
        var now = Now.Of(engine);
        foreach (var move in Moves(now)) _moves[move.Person] = move;
        foreach (var (id, person) in People(now)) _people[id] = person;
        foreach (var site in Sites(now)) _sites[site.Id] = (site.Phase, site.Progress, site.Done);
        foreach (var thing in Things(now)) _things[thing.Id] = thing.Cell;
        _calendarSent = Calendar();
        _task = Task(now);
        _talked = now.Talk?.Recent.Select(conversation => conversation.Id).DefaultIfEmpty(0).Max() ?? 0;
        _logged = engine.LogEnd;
    }

    // ── the parts ──

    private IEnumerable<(int Id, Person Person)> People(Now now)
    {
        if (now.People is not { } people) yield break;
        foreach (var resident in people.All)
        {
            int happiness;
            try
            {
                happiness = Round(now.Happiness?.Of(resident.Id) ?? _happinessAtStart, 5);
            }
            catch (KeyNotFoundException)
            {
                // before his first tick he has the happiness of the start
                happiness = Round(_happinessAtStart, 5);
            }
            yield return (resident.Id, new Person(
                resident.Id,
                resident.Name,
                resident.Sex == Sex.Man ? "man" : "woman",
                resident.Island,
                (int)Math.Floor(people.AgeOf(resident)),
                resident.Phase.ToString().ToLowerInvariant(),
                now.Jobs?.JobOf(resident.Id)?.Role,
                happiness,
                [.. resident.Traits.Notable().Select(trait => $"{trait.Trait}-{(trait.High ? "high" : "low")}")],
                now.Wishes?.Of(resident) ?? [],
                resident.IsEddie));
        }
    }

    private IEnumerable<Move> Moves(Now now)
    {
        if (now.People is not { } people || now.Day is not { } day) yield break;
        foreach (var resident in people.All)
        {
            Doing doing;
            try
            {
                doing = day.Of(resident.Id);
            }
            catch (KeyNotFoundException)
            {
                // he came this tick and has no plan yet
                continue;
            }
            yield return doing.Walk is { } walk
                ? new Move(resident.Id, [Flat(doing.At), .. walk.Cells.Select(Flat)], walk.StartTick, walk.TicksPerCell, Inside: false)
                : new Move(resident.Id, [Flat(doing.At)], now.Tick, _ticksPerCell, doing.Inside);
        }
    }

    private IEnumerable<Site> Sites(Now now) =>
        now.Building?.All.Select(structure => new Site(
            structure.Id,
            structure.Blueprint,
            [.. structure.Cells.Select(Flat)],
            structure.Rotation,
            structure.Phase,
            structure.Phases,
            Progress(structure),
            structure.Done)) ?? [];

    private IEnumerable<Thing> Things(Now now) =>
        now.Building?.Things.Select(thing => new Thing(thing.Id, thing.Kind, Flat(thing.At))) ?? [];

    private static TaskView? Task(Now now) =>
        now.Wishes?.Task is { } task ? new TaskView(task.Wish, task.Island, task.TitleNl, task.TitleEn) : null;

    private CalendarView Calendar()
    {
        var local = _calendar.Now;
        var quarter = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute - local.Minute % 15, 0);
        var time = TimeOnly.FromDateTime(quarter);
        var night = _night.From <= _night.To ? time >= _night.From && time < _night.To : time >= _night.From || time < _night.To;
        return new CalendarView(quarter.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture), night);
    }

    /// <summary>The last lines of the diary that a visitor can read, oldest first.</summary>
    private List<LogLine> History(Engine engine)
    {
        var lines = new List<LogLine>(HistoryLines);
        var log = engine.Log;
        for (var at = log.Count - 1; at >= 0 && lines.Count < HistoryLines; at--)
            if (_text.Render(log[at]) is { } line) lines.Add(new LogLine(log[at].Tick, log[at].Kind, line.Nl, line.En));
        lines.Reverse();
        return lines;
    }

    // how far the phase going on is, from 0 to 100 in steps of 10
    private int Progress(Structure structure)
    {
        if (structure.Done || !_blueprints.TryGetValue(structure.Blueprint, out var plan)) return 0;
        var ticks = plan.Phases[structure.Phase].Ticks;
        return Math.Clamp(structure.Progress * 100 / ticks / 10 * 10, 0, 100);
    }

    private int Flat(Cell cell) => cell.Row * _map.Cols + cell.Col;

    private static int Round(int value, int step) => (int)Math.Round(value / (double)step, MidpointRounding.AwayFromZero) * step;

    // a still resident who stands where the last move ended, as he was, is no news
    private static bool Same(Move sent, Move now) =>
        now.Cells.Count == 1
            ? sent.Cells[^1] == now.Cells[0] && sent.Inside == now.Inside
            : sent.Start == now.Start && sent.TicksPerCell == now.TicksPerCell && sent.Inside == now.Inside && sent.Cells.SequenceEqual(now.Cells);

    private static bool Same(Person a, Person b) =>
        a with { Notable = b.Notable, Wishes = b.Wishes } == b && a.Notable.SequenceEqual(b.Notable) && a.Wishes.SequenceEqual(b.Wishes);

    private static MapView View(IslandMap map, Catalog catalog, IReadOnlyList<Wish> wishes)
    {
        var ground = new char[map.Rows * map.Cols];
        for (var row = 0; row < map.Rows; row++)
            for (var col = 0; col < map.Cols; col++)
                ground[row * map.Cols + col] = map.At(new Cell(row, col)) switch
                {
                    Ground.Water => '~',
                    Ground.Fishing => 'v',
                    Ground.Forest => 'T',
                    Ground.Fertile => 'F',
                    Ground.Rock => 'R',
                    Ground.Clay => 'C',
                    Ground.Lot => 'h',
                    _ => '.',
                };
        return new MapView(
            map.Rows,
            map.Cols,
            new string(ground),
            [.. map.Islands.Select(island => new IslandView(island.Id, island.Name, island.Eddie, [.. island.Cells.Select(cell => cell.Row * map.Cols + cell.Col).Order()]))],
            [.. catalog.Blueprints.Select(blueprint => new BlueprintView(blueprint.Id, blueprint.Nl, blueprint.En, blueprint.Look,
                [.. blueprint.Phases.Select(phase => phase.Id)], [.. blueprint.Phases.Select(phase => new NameView(phase.Id, phase.Nl, phase.En))]))],
            [.. catalog.Roles.Select(role => new NameView(role.Id, role.Nl, role.En))],
            [.. wishes.Select(wish => new NameView(wish.Id, wish.Nl, wish.En))],
            [.. catalog.Things.Select(thing => new NameView(thing.Id, thing.Nl, thing.En))]);
    }

    /// <summary>The modules the feed reads, as they run now; a module that stopped is not there.</summary>
    private sealed record Now(long Tick, PeopleModule? People, DayModule? Day, IJobs? Jobs, BuildingModule? Building, WishesModule? Wishes,
        HappinessModule? Happiness, TalkModule? Talk)
    {
        public static Now Of(Engine engine)
        {
            var running = engine.Running;
            return new Now(engine.Tick, running.OfType<PeopleModule>().FirstOrDefault(), running.OfType<DayModule>().FirstOrDefault(),
                running.OfType<IJobs>().FirstOrDefault(), running.OfType<BuildingModule>().FirstOrDefault(), running.OfType<WishesModule>().FirstOrDefault(),
                running.OfType<HappinessModule>().FirstOrDefault(), running.OfType<TalkModule>().FirstOrDefault());
        }
    }

    /// <summary>What the feed reads from the section "day": how fast residents walk, and when it is night.</summary>
    private sealed record FeedDay(int TicksPerCell, NightHours Night);

    private sealed record NightHours(string From, string To);

    /// <summary>What the feed reads from the section "life": the happiness of someone who has had no tick yet.</summary>
    private sealed record FeedLife(FeedHappiness Happiness);

    private sealed record FeedHappiness(int Base);
}

/// <summary>
/// The diary in words: the sentence of a line's kind from the data (any section's <c>log</c>), with its subject and
/// detail filled in by their names in the language of the sentence. A line whose kind has no sentence, such as a
/// module that stopped, is for the developer and is not shown.
/// </summary>
internal sealed class LogText
{
    private readonly Dictionary<string, Sentence> _sentences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Nl, string En)> _things = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Nl, string En)> _wishes = new(StringComparer.Ordinal);

    public LogText(DataSet data, Catalog catalog, IReadOnlyList<Wish> wishes)
    {
        foreach (var name in data.Names)
        {
            try
            {
                foreach (var (kind, sentence) in data.Section<LogSection>(name).Log ?? new Dictionary<string, Sentence>())
                    _sentences.TryAdd(kind, sentence);
            }
            catch (InvalidDataException)
            {
                // a section that is not an object has no sentences
            }
        }
        foreach (var blueprint in catalog.Blueprints) _things.TryAdd(blueprint.Id, (blueprint.Nl, blueprint.En));
        foreach (var thing in catalog.Things) _things.TryAdd(thing.Id, (thing.Nl, thing.En));
        foreach (var role in catalog.Roles) _things.TryAdd(role.Id, (role.Nl, role.En));
        foreach (var material in catalog.Materials) _things.TryAdd(material.Id, (material.Nl, material.En));
        foreach (var wish in wishes) _wishes.TryAdd(wish.Id, (wish.Nl, wish.En));
    }

    public (string Nl, string En)? Render(LogEntry entry)
    {
        if (!_sentences.TryGetValue(entry.Kind, out var sentence)) return null;
        // a task names a wish; everything else names something of the catalog, or a resident by his name
        var names = entry.Kind.StartsWith("task-", StringComparison.Ordinal) ? _wishes : _things;
        string Fill(string text, Func<(string Nl, string En), string> language)
        {
            string Name(string? id) => id is null ? "" : names.TryGetValue(id, out var name) || _things.TryGetValue(id, out name) ? language(name) : id;
            return text.Replace("{subject}", Name(entry.Subject)).Replace("{detail}", Name(entry.Detail));
        }
        return (Fill(sentence.Nl, name => name.Nl), Fill(sentence.En, name => name.En));
    }

    private sealed record LogSection(IReadOnlyDictionary<string, Sentence>? Log = null);
}

/// <summary>Writes the messages of the protocol as JSON, the way the hub sends them.</summary>
public static class ProtocolJson
{
    public static string Write<T>(T message) => JsonSerializer.Serialize(message, Protocol.Json);

    public static int Bytes<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Protocol.Json).Length;
}
