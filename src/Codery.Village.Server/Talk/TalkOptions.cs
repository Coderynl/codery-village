using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Codery.Village.Server.Talk;

/// <summary>
/// The settings of the host for conversations, under <c>Village:Talk</c>: the API key (<c>ApiKey</c>; without one
/// every conversation is a template), the budget per Dutch calendar day in dollars (<c>DailyUsd</c>, 5) and how
/// long a conversation may take (<c>TimeoutSeconds</c>, 20).
/// </summary>
public sealed record TalkOptions(string? ApiKey = null, decimal DailyUsd = 5, double TimeoutSeconds = 20)
{
    public static TalkOptions From(IConfiguration configuration)
    {
        var talk = configuration.GetSection("Village:Talk");
        var defaults = new TalkOptions();
        return new TalkOptions(
            string.IsNullOrWhiteSpace(talk["ApiKey"]) ? null : talk["ApiKey"],
            talk.GetValue("DailyUsd", defaults.DailyUsd),
            talk.GetValue("TimeoutSeconds", defaults.TimeoutSeconds));
    }

    // the key never shows up in a log or an error: only whether there is one
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"ApiKey = {(ApiKey is null ? "none" : "set")}, DailyUsd = {DailyUsd}, TimeoutSeconds = {TimeoutSeconds}");
        return true;
    }
}

/// <summary>The part of the section "talk" of the data that the server reads; the module reads the rest.</summary>
public sealed record TalkServerSettings(
    // the longest line the model may write
    int MaxLineChars,
    TalkModels Models,
    TalkMaxTokens MaxTokens,
    // added to the bytes of a prompt when the cost is estimated before a call: what the API adds itself
    int ExtraInputTokens,
    // per model id
    IReadOnlyDictionary<string, ModelPrice> Prices,
    TalkPrompts Prompt,
    TalkWords Words)
{
    public ModelPrice PriceOf(string model) =>
        Prices.TryGetValue(model, out var price) ? price : throw new InvalidDataException($"The data section 'talk' has no price for the model '{model}'.");
}

public sealed record TalkModels(string Conversation, string Check);

public sealed record TalkMaxTokens(int Conversation, int Check);

/// <summary>Dollars per million tokens.</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion)
{
    public decimal Cost(long input, long output) => (input * InputPerMillion + output * OutputPerMillion) / 1_000_000m;
}

/// <summary>The system prompts of the conversation and of the check.</summary>
public sealed record TalkPrompts(string Conversation, string Check);

/// <summary>Words that make a line a template, per language. A word ending in <c>*</c> also covers every word it begins.</summary>
public sealed record TalkWords(IReadOnlyList<string> Nl, IReadOnlyList<string> En);
