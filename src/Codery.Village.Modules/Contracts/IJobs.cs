using Codery.Village.Core.Data;
using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Contracts;

/// <summary>
/// Who works where, as Day asks for it. Work gives the jobs; until it runs, nobody has a job and the adults
/// spend the day as they like.
/// </summary>
public interface IJobs
{
    /// <summary>The job the resident has now, or null.</summary>
    JobPlace? JobOf(int resident);
}

/// <summary>
/// A resident's job in a building: where it is done, the role, the shift of the catalog's job and whether it
/// is done outdoors. Not the catalog's <see cref="Job"/>, which says what a blueprint offers.
/// </summary>
public sealed record JobPlace(int Building, Cell Place, string Role, Shift Shift, bool Outdoor);
