using System.Text.Json;
using System.Text.Json.Serialization;
using Codery.Village.Core.Data;

namespace Codery.Village.Server.Feed;

// Protocol 1: what a page gets from the village. In JSON every name is in camelCase, and what is null is left
// out, so a quiet tick is only its number. A tile is one number, row * cols + col.

/// <summary>
/// What a page gets when it joins: the whole village as it stands. <see cref="Moves"/> says where everyone is,
/// or which way he walks; <see cref="Log"/> holds the last lines of the diary, oldest first. <see cref="Fires"/>
/// (added to protocol 1) has every fireplace with how strong its fire burns.
/// </summary>
public sealed record JoinMessage(
    int Protocol,
    MapView Map,
    IReadOnlyList<Person> People,
    IReadOnlyList<Move> Moves,
    IReadOnlyList<Site> Sites,
    IReadOnlyList<Thing> Things,
    CalendarView Calendar,
    TaskView? Task,
    IReadOnlyList<LogLine> Log,
    long Tick,
    int TicksPerSecond,
    IReadOnlyList<FireView> Fires);

/// <summary>
/// What changed in one tick; what did not change is left out. <see cref="Left"/> and <see cref="Gone"/> are the
/// residents and the buildings that are no longer there. <see cref="NoTask"/> is true when the open task closed
/// and no other is open; a new task comes as <see cref="Task"/>. Added to protocol 1: <see cref="Fires"/> has the
/// fireplaces whose fire burns stronger or weaker than before, <see cref="Paths"/> the tiles that became a path and
/// <see cref="PathsGone"/> the paths that grew over.
/// </summary>
public sealed record TickMessage(
    long Tick,
    IReadOnlyList<Move>? Moves = null,
    IReadOnlyList<Person>? People = null,
    IReadOnlyList<int>? Left = null,
    IReadOnlyList<Site>? Sites = null,
    IReadOnlyList<int>? Gone = null,
    IReadOnlyList<Thing>? Things = null,
    CalendarView? Calendar = null,
    TaskView? Task = null,
    bool? NoTask = null,
    IReadOnlyList<Said>? Said = null,
    IReadOnlyList<FeedEvent>? Events = null,
    IReadOnlyList<FireView>? Fires = null,
    IReadOnlyList<int>? Paths = null,
    IReadOnlyList<int>? PathsGone = null);

/// <summary>
/// Where a resident goes: over <see cref="Cells"/>, the first being where he stands at <see cref="Start"/>, one
/// tile per <see cref="TicksPerCell"/> ticks. A resident who stands still has one cell. <see cref="Inside"/>: he
/// is inside a building there, out of sight. <see cref="Carry"/> (added to protocol 1): the material he carries
/// on his back while he walks, by id, as on an errand to or from the store; left out when his hands are empty.
/// </summary>
public sealed record Move(int Person, IReadOnlyList<int> Cells, long Start, int TicksPerCell, bool Inside, string? Carry = null);

/// <summary>
/// A resident as a page shows him. <see cref="Sex"/> is <c>man</c> or <c>woman</c>, <see cref="Phase"/> <c>child</c>,
/// <c>student</c>, <c>working</c> or <c>retired</c>, <see cref="Job"/> a role id of <see cref="MapView.Roles"/>.
/// <see cref="Happiness"/> is rounded to 5. <see cref="Notable"/> holds the traits that stand out, as
/// <c>openness-high</c> or <c>thrift-low</c>; <see cref="Wishes"/> the ids of his wishes in <see cref="MapView.Wishes"/>.
/// <see cref="Home"/> (added to protocol 1) is the tile of the dwelling he lives in, left out when he has none.
/// </summary>
public sealed record Person(
    int Id,
    string Name,
    string Sex,
    int Island,
    int Age,
    string Phase,
    string? Job,
    int Happiness,
    IReadOnlyList<string> Notable,
    IReadOnlyList<string> Wishes,
    bool Eddie,
    int? Home = null);

/// <summary>
/// Something built, or going up. <see cref="Phase"/> is the index of the phase going on (equal to
/// <see cref="Phases"/> when it is done); <see cref="Progress"/> goes from 0 to 100 within the phase, in steps of 10.
/// </summary>
public sealed record Site(int Id, string Blueprint, IReadOnlyList<int> Cells, int Rotation, int Phase, int Phases, int Progress, bool Done);

/// <summary>The fire of a fireplace (a <see cref="Site"/> by its id) and how strong it burns: 0 is out, 3 is full.</summary>
public sealed record FireView(int Structure, int Strength);

/// <summary>A loose thing a visitor can move; <see cref="Kind"/> is an id of <see cref="MapView.Things"/>.</summary>
public sealed record Thing(int Id, string Kind, int Cell);

/// <summary>The Dutch time, to the quarter (<c>2026-06-01T13:15</c>), and whether it is night.</summary>
public sealed record CalendarView(string Local, bool Night);

/// <summary>The task the village wrote out for Eddie: the wish, the island that wishes it most, and its title.</summary>
public sealed record TaskView(string Wish, int Island, string TitleNl, string TitleEn);

/// <summary>A line of the diary, in both languages.</summary>
public sealed record LogLine(long Tick, string Kind, string Nl, string En);

/// <summary>A line of the diary written in this tick, in both languages.</summary>
public sealed record FeedEvent(string Kind, string Nl, string En);

/// <summary>
/// What residents said, line by line, each line starting with the name of who says it. <see cref="B"/> is null
/// when one of them speaks alone, such as about a thing a visitor moved.
/// </summary>
public sealed record Said(int A, int? B, IReadOnlyList<string> Nl, IReadOnlyList<string> En);

/// <summary>
/// The ground and what can stand on it. <see cref="Ground"/> has one letter per tile, row after row, as in
/// <c>map.txt</c>. The names of roles, wishes and things are for what a page shows of a resident or a thing.
/// <see cref="Paths"/> (added to protocol 1) has the tiles that are worn into a path when the page joins.
/// </summary>
public sealed record MapView(
    int Rows,
    int Cols,
    string Ground,
    IReadOnlyList<IslandView> Islands,
    IReadOnlyList<BlueprintView> Blueprints,
    IReadOnlyList<NameView> Roles,
    IReadOnlyList<NameView> Wishes,
    IReadOnlyList<NameView> Things,
    IReadOnlyList<int>? Paths = null);

/// <summary>An island and its tiles; <see cref="Eddie"/> for Eddie's islet.</summary>
public sealed record IslandView(int Id, string Name, bool Eddie, IReadOnlyList<int> Cells);

/// <summary>
/// What can be built: its names, how it looks, and the ids of its phases in order. <see cref="PhaseNames"/> holds the
/// same phases with their names in both languages (added to protocol 1; a page that does not know it ignores it).
/// </summary>
public sealed record BlueprintView(string Id, string Nl, string En, Look Look, IReadOnlyList<string> Phases, IReadOnlyList<NameView> PhaseNames);

/// <summary>An id with its name in both languages.</summary>
public sealed record NameView(string Id, string Nl, string En);

/// <summary>The version of the protocol and how it is written.</summary>
public static class Protocol
{
    public const int Version = 1;

    /// <summary>camelCase, and null left out.</summary>
    public static JsonSerializerOptions Json { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Sets the same rules on options of someone else, such as SignalR's.</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}
