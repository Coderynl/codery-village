using System.Text.Json;
using System.Text.RegularExpressions;
using Codery.Village.Modules.Talk;

namespace Codery.Village.Server.Talk;

/// <summary>
/// The check on what the model wrote: first the word lists of <c>talk.json</c>, each on its own language, then the
/// judgement of a second call to the small model, which answers <c>ok</c> or <c>refuse</c> with a reason. What is
/// not clearly <c>ok</c> is refused.
/// </summary>
public sealed partial class Decency(TalkServerSettings settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Words _nl = new(settings.Words.Nl);
    private readonly Words _en = new(settings.Words.En);

    /// <summary>The first listed word in the lines, or null when there is none.</summary>
    public string? Forbidden(TalkLines lines) => Forbidden(string.Join(' ', lines.Nl), string.Join(' ', lines.En));

    /// <summary>The first listed word in a Dutch and an English text, or null when there is none; an empty text has none.</summary>
    public string? Forbidden(string nl, string en) => _nl.FirstIn([nl]) ?? _en.FirstIn([en]);

    /// <summary>The call that asks the model for its judgement on the lines.</summary>
    public ModelCall CheckCall(TalkLines lines) => new(
        settings.Models.Check,
        settings.Prompt.Check,
        JsonSerializer.Serialize(new { nl = lines.Nl, en = lines.En }, Json),
        settings.MaxTokens.Check,
        VerdictSchema);

    /// <summary>
    /// The word lists, then the model through <paramref name="ask"/>, which returns its text or null. The model
    /// is not asked when a word already refuses the lines.
    /// </summary>
    public async Task<Verdict> Check(TalkLines lines, Func<ModelCall, CancellationToken, Task<string?>> ask, CancellationToken cancel)
    {
        if (Forbidden(lines) is { } word) return new Verdict(false, $"the word '{word}'");
        var answer = await ask(CheckCall(lines), cancel);
        if (answer is null) return new Verdict(false, "no judgement");
        try
        {
            var verdict = JsonSerializer.Deserialize<Judged>(answer, Json);
            return verdict?.Verdict == "ok"
                ? new Verdict(true, verdict.Reason ?? "")
                : new Verdict(false, verdict?.Reason ?? "refused");
        }
        catch (JsonException)
        {
            return new Verdict(false, "an unreadable judgement");
        }
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> VerdictSchema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            verdict = new { type = "string", @enum = new[] { "ok", "refuse" } },
            reason = new { type = "string" },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "verdict", "reason" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    private sealed record Judged(string? Verdict, string? Reason);

    /// <summary>A word list: whole words, and words that begin with a listed word ending in <c>*</c>; case does not matter.</summary>
    private sealed class Words(IReadOnlyList<string> listed)
    {
        private readonly HashSet<string> _whole = [.. listed.Where(word => !word.EndsWith('*')).Select(word => word.ToLowerInvariant())];
        private readonly List<string> _starts = [.. listed.Where(word => word.EndsWith('*')).Select(word => word.TrimEnd('*').ToLowerInvariant())];

        public string? FirstIn(IEnumerable<string> lines)
        {
            foreach (var line in lines)
                foreach (Match match in Letters().Matches(line.ToLowerInvariant()))
                {
                    var word = match.Value;
                    if (_whole.Contains(word) || _starts.Any(word.StartsWith)) return word;
                }
            return null;
        }
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Letters();
}

/// <summary>The judgement on a conversation.</summary>
public sealed record Verdict(bool Ok, string Reason);

/// <summary>One call to the Messages API: the model, the system prompt, the user text, the cap and the JSON schema of the answer.</summary>
public sealed record ModelCall(string Model, string System, string User, int MaxTokens, IReadOnlyDictionary<string, JsonElement> Schema);
