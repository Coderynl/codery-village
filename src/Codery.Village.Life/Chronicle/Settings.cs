using Codery.Village.Laws;

namespace Codery.Village.Life.Chronicle;

/// <summary>
/// The section "chronicle" of the world: when something is worth telling, and the sentences that tell it, nl and en side by
/// side, with <c>{placeholders}</c>.
/// </summary>
/// <param name="ShortageDays">A shortage is told once it has lasted this many days in a row.</param>
/// <param name="IllnessWave">An illness wave is told once this many people are ill at once.</param>
/// <param name="HeavyWish">Each life year, a line for every use this many people want.</param>
/// <param name="TopWishes">A year in which no use is wanted that much tells the this-many most wanted.</param>
/// <param name="Numbers">Numbers in words, from zero; larger numbers stay digits.</param>
/// <param name="Months">The names of the months, January first.</param>
/// <param name="Structures">What a structure is called in a sentence, with its article.</param>
/// <param name="Sentences">Every sentence and phrase, by key.</param>
public sealed record ChronicleRules(
    int ShortageDays,
    int IllnessWave,
    int HeavyWish,
    int TopWishes,
    Words Numbers,
    Words Months,
    IReadOnlyDictionary<string, Name> Structures,
    IReadOnlyDictionary<string, Name> Sentences)
{
    /// <summary>The sentence <paramref name="key"/> in <paramref name="language"/>, its placeholders filled.</summary>
    public string Say(string key, string language, params (string Name, string Value)[] values)
    {
        if (!Sentences.TryGetValue(key, out var sentence)) throw new InvalidDataException($"The chronicle has no sentence '{key}'.");
        var text = language == "en" ? sentence.En : sentence.Nl;
        foreach (var (name, value) in values) text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return text;
    }

    /// <summary><paramref name="n"/> in words where the world has a word for it.</summary>
    public string Number(int n, string language)
    {
        var words = Numbers.In(language);
        return n >= 0 && n < words.Count ? words[n] : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public string List(IReadOnlyList<string> items, string language) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} {Say("and", language)} {items[^1]}",
    };

    /// <summary>"14 april 2027".</summary>
    public string Date(DateOnly date, string language) => $"{date.Day} {Months.In(language)[date.Month - 1]} {date.Year}";

    /// <summary>"14–20 april 2027", "28 april – 4 mei 2027", "29 december 2027 – 4 januari 2028".</summary>
    public string Dates(DateOnly first, DateOnly last, string language)
    {
        var months = Months.In(language);
        if (first.Year != last.Year) return $"{Date(first, language)} – {Date(last, language)}";
        if (first.Month != last.Month) return $"{first.Day} {months[first.Month - 1]} – {Date(last, language)}";
        return first.Day == last.Day ? Date(last, language) : $"{first.Day}–{Date(last, language)}";
    }
}

/// <summary>A list of words in both languages.</summary>
public sealed record Words(IReadOnlyList<string> Nl, IReadOnlyList<string> En)
{
    public IReadOnlyList<string> In(string language) => language == "en" ? En : Nl;
}
