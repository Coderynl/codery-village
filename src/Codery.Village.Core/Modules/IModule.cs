using System.Text.Json.Nodes;

namespace Codery.Village.Core.Modules;

/// <summary>
/// One part of the village: people, the day, work, building. The engine starts it once, steps it every tick
/// and keeps its part of the save. A module reaches the world and the other modules only through its
/// <see cref="ModuleContext"/>.
/// </summary>
public interface IModule
{
    /// <summary>Fixed, short, English: "people", "day". Names its part of the save and its random stream.</summary>
    string Name { get; }

    /// <summary>The version of its part of the save.</summary>
    int Version { get; }

    /// <summary>A new village.</summary>
    void Start(ModuleContext village);

    /// <summary>One tick.</summary>
    void Step(ModuleContext village);

    JsonNode Save();

    /// <summary>Takes up a saved part; migrates it itself when <paramref name="version"/> is older.</summary>
    void Load(ModuleContext village, JsonNode data, int version);
}
