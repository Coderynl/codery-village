namespace Codery.Village.Modules.Contracts;

/// <summary>
/// The homes of the village, as People asks for them. Building gives the homes; until it runs, People counts
/// every household as housed and lets nobody new in.
/// </summary>
public interface IHousing
{
    /// <summary>A home on the island that nobody lives in, or null.</summary>
    int? FreeHome(int island);

    /// <summary>The household lives in the home from now on.</summary>
    void MoveIn(int household, int home);

    bool HasHome(int household);
}
