using System.Collections.Concurrent;
using System.Threading.Channels;
using Codery.Village.Core.Data;
using Codery.Village.Modules.Talk;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codery.Village.Server.Talk;

/// <summary>
/// Has the conversations written, one after another, away from the tick. The host offers the requests it took
/// from the module (<see cref="Offer"/>) and hands the answers it takes (<see cref="TakeAnswers"/>) to the module.
/// Every request gets an answer: what the talker writes, or a template from <c>talk.json</c> when it gives none,
/// throws, or takes longer than <c>TimeoutSeconds</c>.
/// </summary>
public sealed class TalkWorker : BackgroundService
{
    private readonly Channel<TalkRequest> _requests = Channel.CreateUnbounded<TalkRequest>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<(long Id, TalkLines Lines)> _answers = new();
    private readonly ITalker _talker;
    private readonly TalkTemplates _templates;
    private readonly TimeSpan _timeout;
    private readonly ILogger _log;

    public TalkWorker(ITalker talker, DataSet data, TalkOptions options, ILogger<TalkWorker>? log = null)
    {
        _talker = talker;
        _templates = data.Section<TalkSettings>("talk").Templates;
        _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        _log = log ?? NullLogger<TalkWorker>.Instance;
    }

    /// <summary>A request to have written; returns at once.</summary>
    public void Offer(TalkRequest request) => _requests.Writer.TryWrite(request);

    /// <summary>The answers that are ready, each once.</summary>
    public IReadOnlyList<(long Id, TalkLines Lines)> TakeAnswers()
    {
        var answers = new List<(long Id, TalkLines Lines)>();
        while (_answers.TryDequeue(out var answer)) answers.Add(answer);
        return answers;
    }

    /// <summary>The conversation for one request: the talker's, or the template.</summary>
    public async Task<TalkLines> Answer(TalkRequest request, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(_timeout);
        try
        {
            // also when the talker does not listen to the token
            if (await _talker.Write(request, limit.Token).WaitAsync(limit.Token) is { } lines) return lines;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            _log.LogInformation("Conversation {Id} took longer than {Timeout}.", request.Id, _timeout);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Conversation {Id} failed.", request.Id);
        }
        return _templates.Fill(request);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _requests.Reader.ReadAllAsync(stoppingToken))
            _answers.Enqueue((request.Id, await Answer(request, stoppingToken)));
    }
}
