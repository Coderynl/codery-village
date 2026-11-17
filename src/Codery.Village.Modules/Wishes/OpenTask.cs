namespace Codery.Village.Modules.Wishes;

/// <summary>
/// The one task of the whole land: a <paramref name="Wish"/> the data cannot fulfil, for Eddie to build. Written on
/// tick <paramref name="Since"/> for the <paramref name="Island"/> that wishes it most; <paramref name="Weight"/> is
/// what all islands together wish it. The text is the template of the data with the numbers of that moment.
/// </summary>
public sealed record OpenTask(string Wish, int Island, long Since, double Weight, string TitleNl, string TitleEn, string BodyNl);
