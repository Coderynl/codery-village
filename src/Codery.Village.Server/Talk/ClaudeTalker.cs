using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Codery.Village.Core.Data;
using Codery.Village.Core.Save;
using Codery.Village.Modules.Talk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codery.Village.Server.Talk;

/// <summary>
/// Has the small model write a conversation, in Dutch and English in one call, as JSON, and has it checked by
/// <see cref="Decency"/>. Before the first call it reserves what the conversation and the check can cost at most:
/// the bytes of the prompts plus <c>extraInputTokens</c> as input, <c>max_tokens</c> as output, at the prices in
/// <c>talk.json</c>. Afterwards it books what the answers say they used. Null, for a template, without a key, when
/// the budget does not reach, and on a refusal, unreadable or too long lines, or a refused check.
/// </summary>
public sealed class ClaudeTalker : ITalker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TalkServerSettings _settings;
    private readonly TalkBudget _budget;
    private readonly Decency _decency;
    private readonly AnthropicClient? _client;
    private readonly ILogger _log;

    public ClaudeTalker(DataSet data, TalkOptions options, TalkBudget budget, HttpClient? http = null, ILogger<ClaudeTalker>? log = null)
    {
        _settings = data.Section<TalkServerSettings>("talk");
        // a model without a price stops the start, not the first conversation
        _settings.PriceOf(_settings.Models.Conversation);
        _settings.PriceOf(_settings.Models.Check);
        _budget = budget;
        _decency = new Decency(_settings);
        _log = log ?? NullLogger<ClaudeTalker>.Instance;
        if (options.ApiKey is { } key)
            _client = http is null ? new AnthropicClient { ApiKey = key } : new AnthropicClient { ApiKey = key, HttpClient = http };
    }

    public async Task<TalkLines?> Write(TalkRequest request, CancellationToken cancel)
    {
        if (_client is null) return null;

        var conversation = new ModelCall(
            _settings.Models.Conversation,
            _settings.Prompt.Conversation,
            JsonSerializer.Serialize(request.Facts, SaveJson.Options),
            _settings.MaxTokens.Conversation,
            LinesSchema);
        var talking = Estimate(conversation);
        var checking = CheckEstimate();
        if (!_budget.TryReserve(talking + checking, out var reservation))
        {
            _log.LogInformation("Conversation {Id}: the budget of today is spent.", request.Id);
            return null;
        }

        var spent = 0m;
        // what a call that started and did not finish may have cost: its estimate
        var unbooked = 0m;
        try
        {
            unbooked = talking;
            var (text, cost) = await Ask(conversation, cancel);
            (spent, unbooked) = (spent + cost, 0m);
            if (Read(text) is not { } lines)
            {
                _log.LogInformation("Conversation {Id}: the model gave no usable lines.", request.Id);
                return null;
            }

            var verdict = await _decency.Check(lines, async (call, token) =>
            {
                unbooked = checking;
                var (judged, price) = await Ask(call, token);
                (spent, unbooked) = (spent + price, 0m);
                return judged;
            }, cancel);
            if (verdict.Ok) return lines;
            _log.LogInformation("Conversation {Id} refused: {Reason}.", request.Id, verdict.Reason);
            return null;
        }
        finally
        {
            _budget.Settle(reservation, spent + unbooked);
        }
    }

    private async Task<(string? Text, decimal Cost)> Ask(ModelCall call, CancellationToken cancel)
    {
        var message = await _client!.Messages.Create(new MessageCreateParams
        {
            Model = call.Model,
            MaxTokens = call.MaxTokens,
            System = call.System,
            Messages = [new MessageParam { Role = Anthropic.Models.Messages.Role.User, Content = call.User }],
            Thinking = new ThinkingConfigDisabled(),
            OutputConfig = new OutputConfig
            {
                Effort = Effort.Low,
                Format = new JsonOutputFormat { Schema = call.Schema.ToDictionary() },
            },
        }, cancel);

        var cost = _settings.PriceOf(call.Model).Cost(message.Usage.InputTokens, message.Usage.OutputTokens);
        // a refusal, or cut off at max_tokens
        if (message.StopReason != StopReason.EndTurn) return (null, cost);
        return (string.Concat(message.Content.Select(block => block.Value).OfType<TextBlock>().Select(block => block.Text)), cost);
    }

    // two to four lines per language, each short and without braces
    private TalkLines? Read(string? text)
    {
        if (text is null) return null;
        Said? said;
        try
        {
            said = JsonSerializer.Deserialize<Said>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (said?.Nl is not { } nl || said.En is not { } en) return null;
        if (!Fits(nl) || !Fits(en)) return null;
        return new TalkLines([.. nl], [.. en], FromTemplate: false);
    }

    private bool Fits(IReadOnlyList<string?> lines) =>
        lines.Count is >= 2 and <= 4
        && lines.All(line => !string.IsNullOrWhiteSpace(line) && line.Length <= _settings.MaxLineChars && !line.Contains('{') && !line.Contains('}'));

    private decimal Estimate(ModelCall call) =>
        _settings.PriceOf(call.Model).Cost(Bytes(call.System) + Bytes(call.User) + Bytes(JsonSerializer.Serialize(call.Schema)) + _settings.ExtraInputTokens, call.MaxTokens);

    // the check reads what the conversation wrote: at most its max_tokens, counted twice for the quotes and escapes
    private decimal CheckEstimate()
    {
        var call = _decency.CheckCall(new TalkLines([], [], false));
        return Estimate(call) + _settings.PriceOf(call.Model).Cost(2L * _settings.MaxTokens.Conversation, 0);
    }

    private static long Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    private static readonly IReadOnlyDictionary<string, JsonElement> LinesSchema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            nl = new { type = "array", items = new { type = "string" } },
            en = new { type = "array", items = new { type = "string" } },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "nl", "en" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    private sealed record Said(IReadOnlyList<string>? Nl, IReadOnlyList<string>? En);
}
