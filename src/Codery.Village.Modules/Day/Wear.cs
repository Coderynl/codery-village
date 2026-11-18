using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Day;

/// <summary>
/// The tiles that walking wears into paths. Every step on a tile counts on the calendar day it was set. A tile with
/// more than <c>threshold</c> steps in the last <c>windowDays</c> days becomes a path, and stays one while it has
/// more than <c>keep</c>; a path nobody walks grows over once its steps have left the window.
/// </summary>
internal sealed class Wear
{
    // per tile, the steps set on it per calendar day, only the days within the window
    private readonly Dictionary<Cell, SortedDictionary<DateOnly, int>> _steps = [];
    private readonly HashSet<Cell> _paths = [];
    private DateOnly _today;

    public IReadOnlyCollection<Cell> Paths => _paths;

    public bool IsPath(Cell cell) => _paths.Contains(cell);

    /// <summary>A step on the tile on the given day; the window moves on with the day first.</summary>
    public void Step(Cell cell, DateOnly day, int windowDays, int threshold, int keep)
    {
        Turn(day, windowDays, keep);
        if (!_steps.TryGetValue(cell, out var days)) _steps[cell] = days = [];
        days[day] = days.GetValueOrDefault(day) + 1;
        if (Sum(days) > threshold) _paths.Add(cell);
    }

    /// <summary>
    /// Moves the window to the given day: the steps of days that have left it are forgotten, and a path with too
    /// few steps left grows over.
    /// </summary>
    public void Turn(DateOnly day, int windowDays, int keep)
    {
        if (day == _today) return;
        _today = day;
        var first = day.AddDays(1 - windowDays);
        foreach (var (cell, days) in _steps.ToList())
        {
            foreach (var old in days.Keys.Where(date => date < first).ToList()) days.Remove(old);
            if (days.Count == 0) _steps.Remove(cell);
        }
        foreach (var cell in _paths.ToList())
            if (!_steps.TryGetValue(cell, out var days) || Sum(days) <= keep) _paths.Remove(cell);
    }

    private static int Sum(SortedDictionary<DateOnly, int> days) => days.Values.Sum();

    // ── saving ──

    public SavedWear Save() => new(
        _today,
        [.. _steps.OrderBy(item => item.Key.Row).ThenBy(item => item.Key.Col)
            .Select(item => new SavedSteps(item.Key, [.. item.Value.Select(day => new SavedDay(day.Key, day.Value))]))],
        [.. _paths.OrderBy(cell => cell.Row).ThenBy(cell => cell.Col)]);

    public void Load(SavedWear? saved)
    {
        _steps.Clear();
        _paths.Clear();
        _today = default;
        if (saved is null) return;
        _today = saved.Today;
        foreach (var tile in saved.Steps) _steps[tile.Cell] = new SortedDictionary<DateOnly, int>(tile.Days.ToDictionary(day => day.Day, day => day.Steps));
        _paths.UnionWith(saved.Paths);
    }

    internal sealed record SavedWear(DateOnly Today, IReadOnlyList<SavedSteps> Steps, IReadOnlyList<Cell> Paths);

    internal sealed record SavedSteps(Cell Cell, IReadOnlyList<SavedDay> Days);

    internal sealed record SavedDay(DateOnly Day, int Steps);
}
