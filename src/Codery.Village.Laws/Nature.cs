namespace Codery.Village.Laws;

/// <summary>
/// The laws of nature: what a process makes of its inputs. Deterministic; chance lives in who tries what, never here.
/// </summary>
public sealed class Nature
{
    private readonly Dictionary<string, Process> _processes;
    private readonly Dictionary<string, List<Law>> _laws;

    public Nature(IReadOnlyList<Process> processes, IReadOnlyList<Law> laws)
    {
        _processes = processes.ToDictionary(process => process.Id, StringComparer.Ordinal);
        _laws = processes.ToDictionary(process => process.Id, _ => new List<Law>(), StringComparer.Ordinal);
        foreach (var law in laws)
        {
            if (!_laws.TryGetValue(law.Process, out var list)) throw new ArgumentException($"Law '{law.Id}' names unknown process '{law.Process}'.", nameof(laws));
            list.Add(law);
        }
    }

    /// <summary>The processes, by id.</summary>
    public IReadOnlyDictionary<string, Process> Processes => _processes;

    /// <summary>The laws of a process, in data order.</summary>
    public IReadOnlyList<Law> LawsOf(string process) => _laws[process];

    /// <summary>
    /// What <paramref name="process"/> makes of <paramref name="inputs"/>: their profiles mixed, then the effects of every
    /// law of that process whose conditions hold, in data order. Null when no law holds, when more different substances go
    /// in than the process takes, or when the result is one of the inputs again.
    /// </summary>
    public Substance? Apply(string process, IReadOnlyList<Portion> inputs) => Fire(process, inputs).Result;

    /// <summary>As <see cref="Apply"/>, with the laws that fired.</summary>
    public (Substance? Result, IReadOnlyList<Law> Fired) Fire(string process, IReadOnlyList<Portion> inputs)
    {
        if (!_processes.TryGetValue(process, out var how)) throw new ArgumentException($"Unknown process '{process}'.", nameof(process));
        if (inputs.Count == 0) throw new ArgumentException("Nothing goes in.", nameof(inputs));
        if (inputs.Any(input => input.Parts <= 0)) throw new ArgumentException("Every input counts at least one part.", nameof(inputs));
        if (inputs.Select(input => input.Substance.Id).Distinct().Count() > how.MaxInputs) return (null, []);

        var mixed = Profile.Mix([.. inputs.Select(input => (input.Substance.Profile, input.Parts))]);
        var main = Main(inputs);
        var kind = main.Substance.Kind;
        var fired = new List<Law>();
        foreach (var law in _laws[process])
        {
            if (!law.When.All(condition => Holds(condition, how, inputs, main))) continue;
            if (law.Then.Any(effect => effect.AboveBoth is not null) && !AllMolten(how, inputs)) continue;
            fired.Add(law);
            foreach (var effect in law.Then) mixed = Applied(effect, mixed, inputs);
            if (law.Becomes is not null) kind = law.Becomes;
        }
        if (fired.Count == 0) return (null, []);

        var profile = mixed.Rounded();
        if (inputs.Any(input => input.Substance.Kind == kind && input.Substance.Profile.Rounded() == profile)) return (null, fired);
        return (new Substance(Substance.IdFor(kind, profile), kind, profile, Raw: false), fired);
    }

    // the input with most parts; the first of them on a tie
    private static Portion Main(IReadOnlyList<Portion> inputs) =>
        inputs.Aggregate((best, input) => input.Parts > best.Parts ? input : best);

    private static bool Melts(Process how, Substance substance) => how.Heat > 0 && how.Heat >= substance.Profile[Property.MeltingPoint] * 20;

    private static bool AllMolten(Process how, IReadOnlyList<Portion> inputs) =>
        inputs.Select(input => input.Substance.Id).Distinct().Count() >= 2 && inputs.All(input => Melts(how, input.Substance));

    private static bool Holds(Condition condition, Process how, IReadOnlyList<Portion> inputs, Portion main)
    {
        var selected = condition.Kind is null ? inputs : [.. inputs.Where(input => input.Substance.Kind == condition.Kind)];
        if (selected.Count == 0) return false;

        if (condition.Property is { } property)
        {
            var value = Profile.Mix([.. selected.Select(input => (input.Substance.Profile, input.Parts))])[property];
            return value >= (condition.Min ?? 0) && value <= (condition.Max ?? 100);
        }
        if (condition.Melts is { } melts) return selected.All(input => Melts(how, input.Substance) == melts);

        var total = inputs.Sum(input => input.Parts);
        var parts = condition.Kind is null ? main.Parts : selected.Sum(input => input.Parts);
        var share = 100.0 * parts / total;
        return share >= (condition.Min ?? 50) && share <= (condition.Max ?? 100);
    }

    private static Profile Applied(Effect effect, Profile profile, IReadOnlyList<Portion> inputs)
    {
        var value = profile[effect.Property];
        if (effect.Add is { } add) value += add;
        if (effect.Set is { } set) value = set;
        if (effect.AboveBoth is { } above) value = inputs.Max(input => input.Substance.Profile[effect.Property]) + above;
        return profile.With(effect.Property, value);
    }
}
