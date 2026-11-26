namespace Codery.Village.Laws;

/// <summary>
/// What a process does to its inputs, when every condition in <see cref="When"/> holds. The effects in
/// <see cref="Then"/> change the mixed profile in order; <see cref="Becomes"/> gives the result its kind.
/// </summary>
public sealed record Law(string Id, string Process, IReadOnlyList<Condition> When, IReadOnlyList<Effect> Then, string? Becomes);

/// <summary>
/// One test on the inputs. It looks at the inputs of <see cref="Kind"/>, or at all of them when that is null, and tests
/// one thing, the first that is given:
/// <list type="bullet">
/// <item><see cref="Property"/>: the mixed profile of those inputs lies within <see cref="Min"/> and <see cref="Max"/>
/// (0 and 100 when absent);</item>
/// <item><see cref="Melts"/>: every one of those inputs melts at the heat of the process (true), or none does (false);</item>
/// <item>otherwise their share: with a kind, the parts of that kind in a hundred; without one, the share of the main input
/// (the one with most parts), within <see cref="Min"/> (50 when absent) and <see cref="Max"/> (100).</item>
/// </list>
/// A condition whose kind is not among the inputs does not hold.
/// </summary>
public sealed record Condition(string? Kind, Property? Property, int? Min, int? Max, bool? Melts);

/// <summary>
/// A change to one property: <see cref="Add"/> to it, <see cref="Set"/> it, or set it <see cref="AboveBoth"/>: to the
/// highest value among the inputs plus that much. A law with an effect above both fires only on two or more different
/// inputs, all molten. Values stay within 0 and 100.
/// </summary>
public sealed record Effect(Property Property, int? Add, int? Set, int? AboveBoth);
