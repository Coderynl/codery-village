using System.Text;
using Codery.Village.Life.Folk;
using FeelingsModule = Codery.Village.Life.Feelings.Feelings;
using FolkModule = Codery.Village.Life.Folk.Folk;

namespace Codery.Village.Life.Chronicle;

/// <summary>
/// The chronicle as a Markdown document in one language: a title with the seed and the dates; per life year a heading with
/// its days and its lines; a cast of everyone who lived; and the numbers of every year.
/// </summary>
public static class Markdown
{
    /// <summary>The chronicle <paramref name="c"/> in <paramref name="language"/> (<c>nl</c> or <c>en</c>), its people from <paramref name="f"/>.</summary>
    public static string Write(Chronicle c, FolkModule f, FeelingsModule feel, string language)
    {
        var rules = c.Rules;
        var open = c.Open;
        var entries = c.Entries.ToLookup(e => e.LifeYear);
        var years = c.Years.Select(y => (y.Year, y.First, y.Last, Numbers: y.Numbers, Running: false)).ToList();
        if (entries[open.Year].Any()) years.Add((open.Year, open.First, open.Last, c.NumbersNow(f, feel), true));

        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');

        Line(rules.Say("title", language));
        Line();
        var from = years.Count > 0 ? years[0].First : open.First;
        var to = years.Count > 0 ? years[^1].Last : open.Last;
        Line(rules.Say("subtitle", language, ("seed", c.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("from", rules.Date(from, language)), ("to", rules.Date(to, language)), ("years", Digits(c.Years.Count))));

        foreach (var year in years)
        {
            Line();
            Line(rules.Say("year", language, ("year", Digits(year.Year)), ("dates", rules.Dates(year.First, year.Last, language))));
            Line();
            var lines = entries[year.Year].ToList();
            if (lines.Count == 0) Line(rules.Say("quiet", language));
            foreach (var e in lines) Line($"- {(language == "en" ? e.En : e.Nl)}");
        }

        Line();
        Line(rules.Say("cast", language));
        Line();
        for (var id = 0; f.Get(id) is { } p; id++) Line(Cast(c, f, p, language));

        Line();
        Line(rules.Say("numbers", language));
        Line();
        var header = rules.Say("numbers.header", language);
        Line(header);
        Line(string.Concat(Enumerable.Repeat("|---", header.Count(ch => ch == '|') - 1)) + "|");
        foreach (var (year, _, _, n, running) in years)
            Line($"| {year}{(running ? "*" : "")} | {n.People} | {n.Born} | {n.Died} | {n.HappinessMedian} | {n.HappinessSpread} | {n.Discoveries} | {n.Known} | {n.Tried} | {n.Made} | {n.Learned} |");
        Line();
        Line(rules.Say("numbers.note", language));
        return text.ToString();
    }

    // - **Anna** — born in year 3, daughter of Eva and Jan · baker · found hard clay · died in year 17 of an illness, aged 14
    private static string Cast(Chronicle c, FolkModule f, Person p, string language)
    {
        var rules = c.Rules;
        var parts = new List<string>();
        if (p.BornTick < 0) parts.Add(rules.Say("cast.start", language, ("age", c.Age(p.Id, 0, language))));
        else
        {
            var born = rules.Say("cast.born", language, ("year", Digits(p.BornTick / c.TicksPerYear + 1)));
            if (p.Mother is { } mother && p.Father is { } father)
                born += ", " + rules.Say($"cast.parents.{p.Sex.ToString().ToLowerInvariant()}", language,
                    ("mother", f.Get(mother)?.Name ?? "?"), ("father", f.Get(father)?.Name ?? "?"));
            parts.Add(born);
        }
        if (p.Trade is { } trade) parts.Add(c.Named(trade, language));
        var found = c.FoundBy(p.Id);
        if (found.Count > 0) parts.Add(rules.Say("cast.found", language, ("what", rules.List([.. found.Select(n => language == "en" ? n.En : n.Nl)], language))));
        if (p.DiedTick is { } died)
        {
            (string, string)[] values = [("year", Digits(died / c.TicksPerYear + 1)), ("age", c.Age(p.Id, died, language))];
            parts.Add(c.CauseOfDeath(p.Id) is { } why
                ? rules.Say("cast.died", language, [.. values, ("cause", rules.Say($"cause.{why}", language))])
                : rules.Say("cast.died.unknown", language, values));
        }
        else parts.Add(rules.Say("cast.alive", language, ("age", c.Age(p.Id, c.Tick, language))));
        return $"- **{p.Name}** — {string.Join(" · ", parts)}";
    }

    private static string Digits(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
