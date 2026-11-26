namespace Codery.Village.Life.Living;

/// <summary>What a household has for a <paramref name="Use"/>: something of <paramref name="Substance"/>, since <paramref name="SinceTick"/>.</summary>
public sealed record Possession(string Use, string Substance, long SinceTick);
