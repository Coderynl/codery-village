using System.Text.Json.Nodes;

namespace Codery.Village.Core.Save;

/// <summary>
/// The village as it stands, to be taken up again: each module's own part, the events on their way to the
/// next tick, and the log. The calendar is written down but not read back: after a load the clock gives it.
/// </summary>
public sealed record SavedVillage(
    int Format,
    ulong Seed,
    long Tick,
    DateTimeOffset Calendar,
    IReadOnlyList<SavedModule> Modules,
    // published in the last tick, due in the next
    IReadOnlyList<SavedEvent> Pending,
    // the number of the first line in Log
    long LogStart,
    IReadOnlyList<LogEntry> Log)
{
    /// <summary>The format this code writes and reads. Anything else is not loaded.</summary>
    public const int CurrentFormat = 1;
}

/// <summary>
/// A module's part: what its <c>Save</c> gave, at its version, and where its random stream stood.
/// <paramref name="Stopped"/>: the module had stopped; it stays stopped after a load until a newer version takes the part up.
/// </summary>
public sealed record SavedModule(string Name, int Version, ulong Random, JsonNode Data, bool Stopped = false);

/// <summary>An event waiting for the next tick; <paramref name="Type"/> is the name of its record.</summary>
public sealed record SavedEvent(string Type, JsonNode Data);
