using Codery.Village.Modules.Day;

namespace Codery.Village.Modules.Happiness;

/// <summary>The part "happiness" of the section "life".</summary>
public sealed record HappinessSettings(
    int Base,
    // how far happiness follows its target in one calendar hour
    int MaxStepPerHour,
    // the activities that count as free time
    IReadOnlyList<Activity> Leisure,
    // by reason: home, work, leisure and each need of the economy
    IReadOnlyDictionary<string, Reason> Reasons);

/// <summary>The points of a reason: <paramref name="Plus"/> when the resident has it, <paramref name="Minus"/> when not.</summary>
public sealed record Reason(int Plus, int Minus, string Nl, string En);

internal sealed record LifeWithHappiness(HappinessSettings Happiness);
