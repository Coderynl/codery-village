namespace Codery.Village.Core;

/// <summary>
/// A line in the village log. <paramref name="Kind"/> is a short key that has a sentence in the data
/// (under <c>"log"</c> in <c>data/*.json</c>); <paramref name="Subject"/> and <paramref name="Detail"/> fill it in.
/// </summary>
public sealed record LogEntry(long Tick, string Kind, string Subject, string? Detail = null);
