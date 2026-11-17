namespace Codery.Village.Modules.Contracts;

/// <summary>
/// What the residents of an island wish for, as Community asks for it. Wishes (task 11) gives them; until it
/// runs, nobody wishes for anything and Community builds only homes and workshops.
/// </summary>
public interface IWishSource
{
    /// <summary>The labels the island wishes for, with their weight; a label is what a blueprint <c>serves</c>.</summary>
    IReadOnlyList<(string Label, double Weight)> Wanted(int island);
}
