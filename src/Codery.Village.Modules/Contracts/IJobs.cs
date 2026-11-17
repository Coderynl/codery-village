using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Contracts;

/// <summary>
/// Who works where, as Day asks for it. Work gives the jobs; until it runs, nobody has a job and the adults
/// spend the day as they like.
/// </summary>
public interface IJobs
{
    /// <summary>The job the resident has now, or null.</summary>
    Job? JobOf(int resident);
}

/// <summary>
/// A job in a building: where it is done, the role, the shift ("day", "early" or "night", as the catalog
/// names it) and whether it is done outdoors.
/// </summary>
public sealed record Job(int Building, Cell Place, string Role, string Shift, bool Outdoor);
