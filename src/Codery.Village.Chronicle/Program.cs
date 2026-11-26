using System.Diagnostics;
using System.Globalization;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Time;
using Codery.Village.Life;
using ChronicleModule = Codery.Village.Life.Chronicle.Chronicle;
using FeelingsModule = Codery.Village.Life.Feelings.Feelings;
using FolkModule = Codery.Village.Life.Folk.Folk;
using InventingModule = Codery.Village.Life.Inventing.Inventing;
using Markdown = Codery.Village.Life.Chronicle.Markdown;

// dotnet run --project src/Codery.Village.Chronicle -c Release -- --seed 1 --years 26 [--start 2027-03-01] [--lang nl] --out chronicle-1.md
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2) options[args[i]] = args[i + 1];
if (!options.TryGetValue("--seed", out var seedText) || !options.TryGetValue("--years", out var yearsText) || !options.TryGetValue("--out", out var output))
{
    Console.Error.WriteLine("usage: --seed <n> --years <n> [--start yyyy-MM-dd] [--lang nl|en] --out <file.md>");
    return 2;
}
var seed = ulong.Parse(seedText, CultureInfo.InvariantCulture);
var years = int.Parse(yearsText, CultureInfo.InvariantCulture);
var language = options.GetValueOrDefault("--lang", "nl");
var day = DateOnly.ParseExact(options.GetValueOrDefault("--start", "2027-03-01"), "yyyy-MM-dd", CultureInfo.InvariantCulture);

var root = Root(Directory.GetCurrentDirectory());
var world = new DataSet(Directory.EnumerateFiles(Path.Combine(root, "world"), "*.json")
    .ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText));
var map = MapText.Parse(File.ReadAllText(Path.Combine(root, "data", "map.txt")));
var zone = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Amsterdam", out var z) ? z : TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
var midnight = day.ToDateTime(TimeOnly.MinValue);
var start = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));

var clock = Stopwatch.StartNew();
var engine = LifeVillage.Start(seed, world, map, start);
var ticks = years * world.Section<LifeSettings>("life").TicksPerLifeYear;
for (long t = 0; t < ticks; t++) engine.Step();

T Module<T>() => engine.Running.OfType<T>().Single();
var folk = Module<FolkModule>();
File.WriteAllText(output, Markdown.Write(Module<ChronicleModule>(), folk, Module<FeelingsModule>(), language));
foreach (var (module, error) in engine.Stopped) Console.Error.WriteLine($"stopped: {module}: {error}");
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{clock.Elapsed.TotalSeconds:F1} s, {folk.Living.Count} alive, {Module<InventingModule>().Discoveries.Count} discoveries"));
return engine.Stopped.Count > 0 ? 1 : 0;

// the folder that holds the solution, from here upwards
static string Root(string from)
{
    for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Codery.Village.slnx"))) return dir.FullName;
    throw new DirectoryNotFoundException($"No Codery.Village.slnx above {from}.");
}
