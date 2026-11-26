namespace Codery.Village.Life.Feelings;

/// <summary>
/// "I want something that serves <see cref="Use"/>", since <see cref="SinceTick"/>, for a <see cref="Cause"/>.
/// <see cref="Because"/> names what lies behind it: for <c>comparison</c> the id of the person whose household has it, for
/// <c>promise</c> the substance found, for <c>shortage</c> what ran out; null otherwise. <see cref="Weight"/> runs up to
/// the rules' maximum.
/// </summary>
public sealed record Wish(string Use, string Cause, long SinceTick, double Weight, string? Because = null)
{
    /// <summary>The causes a wish can have.</summary>
    public static IReadOnlyList<string> Causes { get; } = ["shortage", "comparison", "promise", "habituation", "loss"];
}

/// <summary>Something that happened to someone, with how much it weighs on their mood when it happens (fading after).</summary>
public sealed record Moment(long Tick, string Kind, string Subject, int Weight);

/// <summary>A wish that came true, or a gift: the joy it gave at the start.</summary>
public sealed record Fulfilment(string Use, long Tick, double Joy);

/// <summary>Something lost: the pain at the start.</summary>
public sealed record Loss(string Use, long Tick, double Pain);
