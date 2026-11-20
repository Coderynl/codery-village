using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Wishes;
using Codery.Village.Server;
using Codery.Village.Server.Feed;

namespace Codery.Village.Checks;

/// <summary>
/// What a trial found: <paramref name="Problems"/> (one line each), the wish of the task that was open in the save, whether
/// the trial's data answered it (the village closed the task), and the mean time per tick on this machine.
/// </summary>
public sealed record TrialResult(IReadOnlyList<string> Problems, string? OpenWish, bool OpenWishAnswered, double MeanTickMs);

/// <summary>
/// Tries data on the village as it stands in production: takes up its save with every module of the village, steps it,
/// and saves it again, as the server would after a deploy.
/// </summary>
public static class ProductionTrial
{
    /// <summary>
    /// Above this mean time per tick a trial is a problem: three times the mean on the live data and a production-like
    /// save (a village of 14 days), rounded up.
    /// </summary>
    public const double TickLimitMs = 2;

    /// <summary>
    /// Takes up <paramref name="save"/> on <paramref name="data"/> and <paramref name="map"/>, runs <paramref name="ticks"/>
    /// ticks and saves. A problem is an exception, a module that stopped, a module that went to sleep (a
    /// <c>module-dormant</c> line), or a mean tick time above <see cref="TickLimitMs"/>.
    /// </summary>
    public static TrialResult Run(SavedVillage save, DataSet data, IslandMap map, int ticks)
    {
        var problems = new List<string>();
        string? openWish = null;
        var answered = false;
        var mean = 0.0;
        try
        {
            var modules = VillageSetup.Modules();
            var engine = Engine.Load(save, modules, data, map, new SimulatedTime(save.Calendar));
            openWish = modules.OfType<Wishes>().Single().Task?.Wish;

            // the first tick is not timed: it pays for compiling the code once, which production paid long ago
            if (ticks > 0) engine.Step();
            var watch = Stopwatch.StartNew();
            for (var tick = 1; tick < ticks; tick++) engine.Step();
            mean = ticks > 1 ? watch.Elapsed.TotalMilliseconds / (ticks - 1) : 0;
            engine.Save();

            problems.AddRange(engine.Stopped.Select(stopped => $"module-stopped: {stopped.Module}: {stopped.Error}"));
            // the lines since the save, the load's own among them; the trial never forgets lines
            foreach (var (_, line) in engine.LogFrom(save.LogStart + save.Log.Count))
            {
                if (line.Kind == "module-dormant")
                    problems.Add($"module-dormant: {line.Subject}{(line.Detail is { } detail ? $": {detail}" : "")}");
                // the line the village writes when it publishes TaskClosed because the data now fulfils the wish
                if (line.Kind == "task-closed" && line.Subject == openWish)
                    answered = true;
            }
            if (mean > TickLimitMs)
                problems.Add($"tick time: a mean of {Ms(mean)} ms per tick is above the limit of {Ms(TickLimitMs)} ms.");
        }
        catch (Exception e)
        {
            problems.Add($"exception: {e.GetType().Name}: {e.Message}");
        }
        return new TrialResult(problems, openWish, answered, mean);
    }

    /// <summary>
    /// The command <c>production</c>: fetches <c>&lt;url&gt;/save</c>, tries the checked-out data on it and writes the
    /// result. Exits 1 when the trial has problems, or for Eddie's pull request (<paramref name="eddie"/>) when it adds a
    /// release production does not run (by <c>/version</c>) and does not answer the open wish; his revert adds none.
    /// When production cannot be read, Eddie's pull request fails; another one passes with a warning.
    /// </summary>
    public static async Task<int> Check(HttpClient http, string url, int ticks, bool eddie, TextWriter output)
    {
        var reading = $"{url.TrimEnd('/')}/save";
        SavedVillage save;
        IReadOnlyList<string>? running = null;
        try
        {
            save = await http.GetFromJsonAsync<SavedVillage>(reading, Protocol.Json) ?? throw new JsonException("The answer is empty.");
            if (eddie)
            {
                reading = $"{url.TrimEnd('/')}/version";
                running = (await http.GetFromJsonAsync<ProductionVersion>(reading, Protocol.Json))?.Releases
                    ?? throw new JsonException("The answer is empty.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            var line = $"Production cannot be read at {reading}: {e.Message}";
            if (eddie)
            {
                output.WriteLine($"{line} Eddie's pull request cannot be tried, so it does not pass.");
                return 1;
            }
            output.WriteLine($"::warning::{line} This pull request was not tried on the production state.");
            return 0;
        }

        var result = Run(save, VillageSetup.Live(), VillageSetup.Map(), ticks);
        output.WriteLine($"Production is at tick {save.Tick}; the open wish: {result.OpenWish ?? "none"}.");
        output.WriteLine($"{ticks} ticks on the checked-out data: a mean of {Ms(result.MeanTickMs)} ms per tick (limit {Ms(TickLimitMs)} ms).");
        foreach (var problem in result.Problems) output.WriteLine(problem);

        // a release in the checked-out data that production does not run: Eddie's new one
        var added = running is null ? [] : VillageSetup.Releases().Except(running, StringComparer.Ordinal).ToList();
        var unanswered = added.Count > 0 && !result.OpenWishAnswered;
        if (added.Count > 0)
            output.WriteLine($"The new release ({string.Join(", ", added)}) {(unanswered ? "does not answer" : "answers")} the open wish: {result.OpenWish ?? "none"}.");

        var failed = result.Problems.Count > 0 || unanswered;
        output.WriteLine(failed ? "The checked-out data does not pass on the production state." : "The checked-out data passes on the production state.");
        return failed ? 1 : 0;
    }

    private static string Ms(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed record ProductionVersion(string Commit, IReadOnlyList<string> Releases);
}
