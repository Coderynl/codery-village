namespace Codery.Village.Laws;

/// <summary>
/// What a resident can do to substances. <see cref="Heat"/> is in degrees Celsius, 0 for none; a process that
/// <see cref="Needs"/> a structure (a kiln) can only be done where one stands. At most <see cref="MaxInputs"/>
/// different substances go in at once.
/// </summary>
public sealed record Process(string Id, int Heat, string? Needs, int MaxInputs);
