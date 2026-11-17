using System.Text.Json.Serialization;
using Codery.Village.Core.Map;

namespace Codery.Village.Core.Data;

/// <summary>What can be made, built and picked up: the section <c>catalog</c>. A release adds to these lists.</summary>
public sealed record Catalog(
    IReadOnlyList<Material> Materials,
    IReadOnlyList<Role> Roles,
    IReadOnlyList<Blueprint> Blueprints,
    IReadOnlyList<ThingKind> Things);

/// <summary>A material in the island's stock. <see cref="Satisfies"/> names the needs it meets, such as <c>food</c>.</summary>
public sealed record Material(string Id, string Nl, string En, IReadOnlyList<string>? Satisfies = null);

/// <summary>A job a resident can have, named as a visitor reads it; <see cref="Job.Role"/> refers to it.</summary>
public sealed record Role(string Id, string Nl, string En);

/// <summary>A kind of loose object a visitor can pick up and move, such as a bench.</summary>
public sealed record ThingKind(string Id, string Nl, string En);

/// <summary>
/// Something the village can build. It stands on tiles of the grounds in <see cref="On"/>, covering
/// <see cref="Shape"/> (axial offsets <c>[row,col]</c> from the anchor; one tile when absent), and goes up in
/// <see cref="Phases"/>. A dwelling has <see cref="Homes"/>, a workplace <see cref="Jobs"/> and perhaps a
/// <see cref="Recipe"/>; a building that fulfils a wish <see cref="Serves"/> its label within <see cref="Range"/>.
/// </summary>
public sealed record Blueprint(
    string Id,
    string Nl,
    string En,
    IReadOnlyList<Ground> On,
    IReadOnlyList<Phase> Phases,
    Look Look,
    IReadOnlyList<int[]>? Shape = null,
    int? Homes = null,
    IReadOnlyList<Job>? Jobs = null,
    Recipe? Recipe = null,
    string? Serves = null,
    int? Range = null)
{
    /// <summary>The tiles it covers relative to its anchor, as the map turns and places them.</summary>
    [JsonIgnore]
    public Shape Footprint => Shape is null
        ? Map.Shape.Single
        : new Shape([.. Shape.Select(point => new Cell(point[0], point[1]))]);
}

/// <summary>One step of building: it starts when the stock holds its <see cref="Cost"/> and takes <see cref="Ticks"/> with a full crew.</summary>
public sealed record Phase(string Id, int Ticks, IReadOnlyDictionary<string, int> Cost);

/// <summary>Places to work at a building: <see cref="Count"/> people in the role, in one shift, inside or out.</summary>
public sealed record Job(string Role, int Count, Shift Shift, bool Outdoor);

/// <summary>When a job is worked. <see cref="Early"/> runs from 04:00 to 12:00.</summary>
public enum Shift { Day, Early, Night }

/// <summary>What a workplace makes: <see cref="In"/> from the stock becomes <see cref="Out"/> after <see cref="Ticks"/>. Nature has no <see cref="In"/>.</summary>
public sealed record Recipe(IReadOnlyDictionary<string, int> In, IReadOnlyDictionary<string, int> Out, int Ticks);
