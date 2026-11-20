using Codery.Village.Core.Data;
using Codery.Village.Modules.Wishes;

namespace Codery.Village.Server.Desk;

/// <summary>
/// The desk: once a minute the village asks Eddie on GitHub for what it wishes, and follows him until his release is
/// live and judged. A round first holds, in the village, the wish of every held errand: the errands are the truth about
/// the desk's holds. Then it decides with <see cref="Errand.Next"/> for the open task's wish and for every errand that
/// is not closed, and carries out each decision as <see cref="Decision"/> says.
/// <para>
/// Off unless <c>Village:Tasks:Enabled</c>: then it touches nothing, not even GitHub. Switched on without the App's
/// settings it names the missing ones once, never their values, and stays idle. A round that fails is logged and tried
/// again a minute later from what the store holds; the server never stops for it. A round runs only under the store's
/// round lock, so of two servers side by side (during a deploy) only one acts.
/// </para>
/// </summary>
public sealed class EddieDesk(VillageHost village, IVillageGitHub github, IErrandStore store, DeskOptions options, DataSet data,
    IReadOnlyList<string> releases, TimeProvider time, ILogger<EddieDesk> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<string> _missing = Missing(options);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        if (_missing.Count > 0)
        {
            logger.LogWarning("The desk is switched on, but {Missing} is not set; it stays idle.", string.Join(", ", _missing));
            return;
        }
        try
        {
            await village.Ready.WaitAsync(stoppingToken);
            using var timer = new PeriodicTimer(Every, time);
            do await Round(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One round of the desk. It does nothing while switched off, lacking a setting, or before the village is there.</summary>
    internal async Task Round(CancellationToken stoppingToken)
    {
        if (!options.Enabled || _missing.Count > 0 || !village.Ready.IsCompleted) return;
        try
        {
            // during a deploy the old and the new server run side by side: one of them has the round
            await using var round = await store.TryLockRound(stoppingToken);
            if (round is null)
            {
                logger.LogDebug("Another server has the desk's round.");
                return;
            }
            await Work(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (GitHubException exception)
        {
            logger.LogWarning("GitHub answered {Status} on {Path}; the desk tries again in a minute.", exception.Status, exception.Path);
        }
        catch (Exception exception)
        {
            logger.LogWarning("The desk's round failed ({Kind}: {Reason}); it tries again in a minute.", exception.GetType().Name, exception.Message);
            logger.LogDebug(exception, "The desk's round failed.");
        }
    }

    private async Task Work(CancellationToken stoppingToken)
    {
        var texts = DeskTexts.From(data);
        var facts = new ErrandFacts(options.EddieLogin, options.AppLogin, options.Owner, options.Repository,
            data.Section<WishSettings>("wishes").Wishes, releases);
        var errands = await store.All(stoppingToken);
        // a hold of the desk lives in its errand: a village that forgot it (another server saved over it) holds it again
        foreach (var errand in errands.Where(errand => errand.State == ErrandState.Held)) village.Hold(errand.Wish);
        // what the village says now, read under its lock, before GitHub is asked anything
        var task = village.OpenTask();

        // the open task's wish with its record, unless that is closed; then every other errand that is not closed
        List<(string Wish, ErrandRecord? Errand)> round = [];
        if (task is not null)
            round.Add((task.Wish, errands.FirstOrDefault(errand => errand.Wish == task.Wish && errand.State != ErrandState.Closed)));
        round.AddRange(errands
            .Where(errand => errand.Wish != task?.Wish && errand.State != ErrandState.Closed)
            .Select(errand => (errand.Wish, (ErrandRecord?)errand)));
        var live = round.ToDictionary(item => item.Wish, item => village.Live(item.Wish));

        foreach (var (wish, errand) in round)
        {
            if (stoppingToken.IsCancellationRequested) return;
            await Carry(errand, task, live[wish], texts, facts, stoppingToken);
        }
    }

    /// <summary>
    /// One errand: decides, then carries out every action of the decision, each followed by saving the record, without
    /// the stopping token: a stop never falls between an action and its save, and what was decided is done. A decision
    /// that holds the wish is saved only after its last action, so a hold that was not told yet is decided again.
    /// </summary>
    private async Task Carry(ErrandRecord? errand, OpenTask? task, bool wishLive, DeskTexts texts, ErrandFacts facts, CancellationToken stoppingToken)
    {
        IReadOnlyList<IssueComment> comments = errand is { Issue: > 0 } ? await github.Comments(errand.Issue, stoppingToken) : [];
        var decision = Errand.Next(errand, task, comments, wishLive, time.GetUtcNow(), texts, facts);
        if (decision.Errand is not { } record) return;
        if (decision.Actions.Count == 0)
        {
            if (record != errand) await Keep(record);
            return;
        }
        var holding = decision.Actions.Any(action => action is HoldWish);
        for (var i = 0; i < decision.Actions.Count; i++)
        {
            record = await Do(decision.Actions[i], record);
            if (!holding || i == decision.Actions.Count - 1) await Keep(record);
        }
    }

    private async Task<ErrandRecord> Do(DeskAction action, ErrandRecord errand)
    {
        var none = CancellationToken.None;
        switch (action)
        {
            case OpenIssue open:
                return await Opened(open, errand);
            case PostCommand command:
                return errand with { Command = await github.Comment(errand.Issue, command.Body, none) };
            case PostComment comment:
                await github.Comment(errand.Issue, comment.Body, none);
                return errand;
            case AddLabel label:
                await github.Label(errand.Issue, label.Label, none);
                return errand;
            case HoldWish hold:
                village.Hold(hold.Wish);
                return errand;
            case ReleaseWish release:
                village.Release(release.Wish);
                return errand;
            default:
                throw new InvalidOperationException($"The desk does not know the action {action.GetType().Name}.");
        }
    }

    /// <summary>
    /// The issue of a new errand: the open one the App opened already with the same title and label, when its number got
    /// lost or an earlier errand for the wish used it; else a new one. On an issue used again, the App's last command is
    /// the baseline: nothing up to it counts for this errand.
    /// </summary>
    private async Task<ErrandRecord> Opened(OpenIssue open, ErrandRecord errand)
    {
        var none = CancellationToken.None;
        var found = (await github.OpenIssues(open.Label, none))
            .Where(issue => IsApp(issue.Author) && issue.Title == open.Title)
            .MinBy(issue => issue.Number);
        if (found is null) return errand with { Issue = await github.OpenIssue(open.Title, open.Body, [open.Label], none) };
        var commands = (await github.Comments(found.Number, none)).Where(IsAppCommand).Select(comment => comment.Id);
        return errand with { Issue = found.Number, Baseline = commands.DefaultIfEmpty(0).Max() };
    }

    // a record is kept only once it has its issue
    private Task Keep(ErrandRecord errand) => errand.Issue == 0 ? Task.CompletedTask : store.Save(errand, CancellationToken.None);

    private bool IsApp(string author) => string.Equals(author, options.AppLogin, StringComparison.OrdinalIgnoreCase);

    private bool IsAppCommand(IssueComment comment) => IsApp(comment.Author) && comment.Body.StartsWith("/eddie ", StringComparison.Ordinal);

    private static IReadOnlyList<string> Missing(DeskOptions options) =>
        [.. new (string Name, string Value)[] { ("AppId", options.AppId), ("PrivateKey", options.PrivateKey), ("AppLogin", options.AppLogin), ("EddieLogin", options.EddieLogin), ("Owner", options.Owner) }
            .Where(setting => string.IsNullOrWhiteSpace(setting.Value))
            .Select(setting => $"Village:Desk:{setting.Name}")];
}
