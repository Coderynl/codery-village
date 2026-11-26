using System.Collections.Immutable;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;

namespace Codery.Village.Laws;

/// <summary>A name as a visitor reads it.</summary>
public sealed record Name(string Nl, string En);

/// <summary>Where a raw substance lies: each tile of <see cref="Ground"/> (at <see cref="Level"/>, any when null) holds it with <see cref="Chance"/>.</summary>
public sealed record Deposit(string Substance, Ground Ground, int? Level, double Chance);

/// <summary>A substance going into a recipe, in parts.</summary>
public sealed record RecipeInput(string Substance, int Parts);

/// <summary>One step a resident knows: <see cref="In"/> through <see cref="Process"/> makes <see cref="Out"/>. <see cref="Id"/> is <c>{process}:{out}</c>.</summary>
public sealed record Recipe(string Id, string Process, IReadOnlyList<RecipeInput> In, string Out);

/// <summary>A trade: the recipes it works, and the raw substances it <see cref="Gathers"/> from their deposits.</summary>
public sealed record Trade(string Id, IReadOnlyList<string> Recipes, IReadOnlyList<string> Gathers);

/// <summary>What the island knows at the start, in the same terms as everything found later.</summary>
public sealed record StartKnowledge(
    IReadOnlyList<string> Processes,
    IReadOnlyList<Recipe> Recipes,
    IReadOnlyList<string> Uses,
    IReadOnlyList<Trade> Trades,
    IReadOnlyList<string> Structures);

/// <summary>The bounds an island's own substance is drawn within, and where it lies.</summary>
public sealed record OwnBounds(IReadOnlyDictionary<Property, int> Min, IReadOnlyDictionary<Property, int> Max, int MinDistance, Deposit Deposit);

/// <summary>
/// The world of the new core as data: the sections <c>properties</c>, <c>substances</c>, <c>own</c>, <c>processes</c>,
/// <c>laws</c>, <c>uses</c> and <c>start</c>. <see cref="Problems.Of"/> says what does not fit; <see cref="Nature"/> and
/// <see cref="Start"/> throw on data it would find.
/// </summary>
public sealed class WorldData
{
    private readonly Lazy<Nature> _nature;
    private readonly Lazy<(StartKnowledge Start, IReadOnlyDictionary<string, Substance> Made, IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, Name> Names)> _start;
    private readonly Dictionary<string, Name> _names;

    private WorldData(DataSet data)
    {
        PropertyIds = [.. data.Section<List<PropertyData>>("properties").Select(p => p.Id)];
        var substances = data.Section<SubstancesData>("substances");
        Kinds = substances.Kinds.ToDictionary(k => k.Id, k => new Name(k.Nl, k.En), StringComparer.Ordinal);
        Raw = [.. substances.Substances.Select(s => new Substance(s.Id, s.Kind, ProfileOf(s.Profile, $"substance '{s.Id}'"), Raw: true))];
        Deposits = substances.Deposits;
        var processes = data.Section<List<ProcessData>>("processes");
        Processes = [.. processes.Select(p => new Process(p.Id, p.Heat, p.Needs, p.MaxInputs))];
        Laws = data.Section<List<Law>>("laws");
        Uses = data.Section<List<Use>>("uses");
        var own = data.Section<OwnData>("own");
        Own = new OwnBounds(Bounds(own.Min, 0, "own.min"), Bounds(own.Max, 100, "own.max"), own.MinDistance, own.Deposit);
        StartData = data.Section<StartData>("start");

        var names = new Dictionary<string, Name>(StringComparer.Ordinal);
        foreach (var s in substances.Substances) names[s.Id] = new Name(s.Nl, s.En);
        foreach (var p in processes) names[p.Id] = new Name(p.Nl, p.En);
        foreach (var u in Uses) names[u.Id] = new Name(u.Nl, u.En);
        foreach (var t in StartData.Trades) names[t.Id] = new Name(t.Nl, t.En);
        foreach (var s in StartData.Structures) names[s.Id] = new Name(s.Nl, s.En);
        _names = names;

        _nature = new(() => new Nature(Processes, Laws));
        _start = new(ResolveStart);
    }

    /// <summary>Reads the world from its sections. Throws <see cref="InvalidDataException"/> on a section that does not read.</summary>
    public static WorldData From(DataSet data) => new(data);

    /// <summary>The properties as the data lists them.</summary>
    public IReadOnlyList<string> PropertyIds { get; }

    /// <summary>The kinds of substance with their names: raw kinds and the kinds laws make.</summary>
    public IReadOnlyDictionary<string, Name> Kinds { get; }

    /// <summary>The raw substances, in data order.</summary>
    public IReadOnlyList<Substance> Raw { get; }

    /// <summary>Every substance the world knows at the start by id: the raw ones and what the start recipes make.</summary>
    public IReadOnlyDictionary<string, Substance> Substances =>
        Raw.Concat(_start.Value.Made.Values).ToDictionary(s => s.Id, StringComparer.Ordinal);

    public IReadOnlyList<Deposit> Deposits { get; }
    public IReadOnlyList<Process> Processes { get; }
    public IReadOnlyList<Law> Laws { get; }
    public IReadOnlyList<Use> Uses { get; }
    public OwnBounds Own { get; }

    /// <summary>The laws of nature over this world's processes.</summary>
    public Nature Nature => _nature.Value;

    /// <summary>The start knowledge, its recipes made by the laws.</summary>
    public StartKnowledge Start => _start.Value.Start;

    /// <summary>The substance id behind a start recipe's label, such as <c>bread</c>.</summary>
    public IReadOnlyDictionary<string, string> Labels => _start.Value.Labels;

    /// <summary>Names of raw and start substances, processes, uses, trades and structures, by id.</summary>
    public IReadOnlyDictionary<string, Name> NamesById => _start.Value.Names;

    internal StartData StartData { get; }

    private (StartKnowledge, IReadOnlyDictionary<string, Substance>, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, Name>) ResolveStart()
    {
        var names = new Dictionary<string, Name>(_names, StringComparer.Ordinal);
        var known = Raw.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var made = new Dictionary<string, Substance>(StringComparer.Ordinal);
        var recipes = new List<Recipe>();
        var recipeOfLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in StartData.Recipes)
        {
            var inputs = r.In.Select(i =>
            {
                var id = labels.GetValueOrDefault(i.Substance, i.Substance);
                if (!known.TryGetValue(id, out var substance))
                    throw new InvalidDataException($"Start recipe '{r.Label}' takes '{i.Substance}', which is neither raw nor made before it.");
                return new Portion(substance, i.Parts);
            }).ToList();
            var result = Nature.Apply(r.Process, inputs)
                ?? throw new InvalidDataException($"Start recipe '{r.Label}': the laws make nothing of it by '{r.Process}'.");
            known[result.Id] = result;
            made[result.Id] = result;
            labels[r.Label] = result.Id;
            names[result.Id] = new Name(r.Nl, r.En);
            var recipe = new Recipe($"{r.Process}:{result.Id}", r.Process, [.. inputs.Select(i => new RecipeInput(i.Substance.Id, i.Parts))], result.Id);
            recipes.Add(recipe);
            recipeOfLabel[r.Label] = recipe.Id;
        }
        var trades = StartData.Trades.Select(t => new Trade(t.Id,
            [.. t.Recipes.Select(label => recipeOfLabel.TryGetValue(label, out var id) ? id
                : throw new InvalidDataException($"Trade '{t.Id}' works '{label}', which is no start recipe."))],
            t.Gathers)).ToList();
        return (new StartKnowledge(StartData.Processes, recipes, StartData.Uses, trades, [.. StartData.Structures.Select(s => s.Id)]), made, labels, names);
    }

    private static Profile ProfileOf(IReadOnlyDictionary<string, int> values, string where)
    {
        var profile = Profile.Zero;
        foreach (var (name, value) in values)
        {
            if (!Enum.TryParse<Property>(name, ignoreCase: true, out var property) || !Enum.IsDefined(property))
                throw new InvalidDataException($"The {where} names an unknown property '{name}'.");
            if (value is < 0 or > 100) throw new InvalidDataException($"The {where} has {name} {value}, outside 0 to 100.");
            profile = profile.With(property, value);
        }
        return profile;
    }

    private static ImmutableDictionary<Property, int> Bounds(IReadOnlyDictionary<string, int> given, int fallback, string where)
    {
        var profile = ProfileOf(given, where);
        return Enum.GetValues<Property>().ToImmutableDictionary(p => p, p =>
            given.Keys.Any(k => string.Equals(k, p.ToString(), StringComparison.OrdinalIgnoreCase)) ? profile[p] : fallback);
    }
}

// ── the shape of the data ──

internal sealed record PropertyData(string Id, string Nl, string En, string Scale);
internal sealed record KindData(string Id, string Nl, string En);
internal sealed record RawData(string Id, string Kind, string Nl, string En, Dictionary<string, int> Profile);
internal sealed record SubstancesData(List<KindData> Kinds, List<RawData> Substances, List<Deposit> Deposits);
internal sealed record ProcessData(string Id, int Heat, int MaxInputs, string Nl, string En, string? Needs = null);
internal sealed record OwnData(Dictionary<string, int> Min, Dictionary<string, int> Max, int MinDistance, Deposit Deposit);
internal sealed record StartRecipeData(string Label, string Nl, string En, string Process, List<RecipeInput> In);
internal sealed record TradeData(string Id, string Nl, string En, List<string> Recipes, List<string> Gathers);
internal sealed record StructureData(string Id, string Nl, string En);
internal sealed record StartData(List<string> Processes, List<StartRecipeData> Recipes, List<string> Uses, List<TradeData> Trades, List<StructureData> Structures);
