using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Server.Feed;
using BuildingModule = Codery.Village.Modules.Building.Building;
using PeopleModule = Codery.Village.Modules.People.People;

namespace Codery.Village.Server;

/// <summary>
/// What residents say about the hand of a visitor: when a thing was moved, the resident nearest to where it now
/// stands who is awake says the sentence of <c>talk.json</c> (<c>hand</c>) a few seconds later. It changes nothing
/// in the village and is not saved; after a restart it is forgotten.
/// </summary>
public sealed class Hand
{
    private readonly HandSentence _sentence;
    private readonly Dictionary<string, (string Nl, string En)> _names;
    private readonly List<(long Due, int Resident, string Kind)> _waiting = [];

    public Hand(DataSet data)
    {
        _sentence = data.Section<HandSection>("talk").Hand;
        if (_sentence.AfterSeconds is < 0 or >= 60)
            throw new InvalidDataException("The data section 'talk' has a hand sentence that does not come within a minute.");
        _names = data.Section<Catalog>("catalog").Things.ToDictionary(thing => thing.Id, thing => (thing.Nl, thing.En));
    }

    /// <summary>A thing was moved: the nearest resident who is awake will say something about it.</summary>
    public void Moved(Engine engine, int thing)
    {
        if (engine.Running.OfType<BuildingModule>().FirstOrDefault() is not { } building
            || building.Things.FirstOrDefault(item => item.Id == thing) is not { } moved
            || building.NearestAwake(moved.At) is not { } who) return;
        _waiting.Add((engine.Tick + _sentence.AfterSeconds * VillageFeed.TicksPerSecond, who, moved.Kind));
    }

    /// <summary>What is said now, each once; nothing by someone who left in the meantime.</summary>
    public IReadOnlyList<Said> Due(Engine engine)
    {
        if (_waiting.Count == 0) return [];
        var people = engine.Running.OfType<PeopleModule>().FirstOrDefault();
        var said = new List<Said>();
        foreach (var (due, resident, kind) in _waiting.Where(item => item.Due <= engine.Tick).ToList())
        {
            _waiting.Remove((due, resident, kind));
            if (people?.All.FirstOrDefault(who => who.Id == resident) is not { } speaker) continue;
            var (nl, en) = _names.TryGetValue(kind, out var name) ? name : (kind, kind);
            said.Add(new Said(resident, null,
                [_sentence.Nl.Replace("{name}", speaker.Name).Replace("{thing}", nl)],
                [_sentence.En.Replace("{name}", speaker.Name).Replace("{thing}", en)]));
        }
        return said;
    }

    /// <summary>Forgets what was still to be said: the village it was about is not there any more.</summary>
    public void Clear() => _waiting.Clear();

    private sealed record HandSection(HandSentence Hand);
}

/// <summary>The sentence about a moved thing, with <c>{name}</c> (who says it) and <c>{thing}</c>, and how many seconds after the move it comes.</summary>
public sealed record HandSentence(string Nl, string En, int AfterSeconds);
