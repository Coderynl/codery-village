using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Contracts;

/// <summary>
/// The buildings of the village, as Work asks for them. Building gives them; until it runs, there are no
/// buildings and no sites, so the only jobs are the builders', who wait at the gathering place.
/// </summary>
public interface IStructures
{
    /// <summary>The buildings that are done, by id.</summary>
    IReadOnlyList<StructureView> Standing();

    /// <summary>The buildings that are going up, by id.</summary>
    IReadOnlyList<StructureView> Sites();
}

/// <summary>A building as another module sees it: its blueprint, its island and the tile it is anchored on.</summary>
public sealed record StructureView(int Id, string Blueprint, int Island, Cell Place);
