using Codery.Village.Core.Modules;

namespace Codery.Village.Modules.Wishes;

/// <summary>The village wrote a task for Eddie.</summary>
public sealed record TaskOpened(long Tick, OpenTask Task) : VillageEvent(Tick);

/// <summary>The data now knows what fulfils the wish of the task: the task is done.</summary>
public sealed record TaskClosed(long Tick, string Wish) : VillageEvent(Tick);
