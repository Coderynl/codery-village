using System.Text.RegularExpressions;
using Codery.Village.Core.Data;
using Codery.Village.Modules.Wishes;

namespace Codery.Village.Server.Desk;

public enum ErrandState { Asking, Waiting, Budget, Delivered, Live, Cooling, Held }

/// <summary>
/// What the desk keeps of the errand for one <paramref name="Wish"/>: its <paramref name="Issue"/>, the id of the desk's
/// latest <c>/eddie</c> <paramref name="Command"/>, its <paramref name="State"/> and <paramref name="Since"/> when, the
/// <paramref name="Failures"/> so far with their <paramref name="History"/>, and whether Michael was mentioned while
/// Eddie waited for him. An <paramref name="Issue"/> or <paramref name="Command"/> of 0 is one the desk has yet to fill
/// in; see <see cref="Decision"/>. <paramref name="Reappeared"/> is when the task of a live wish came back because its
/// release went away, while the errand waits for Eddie's word on that.
/// </summary>
public sealed record ErrandRecord(string Wish, int Issue, long Command, ErrandState State, DateTimeOffset Since,
    int Failures, IReadOnlyList<Failure> History, bool OwnerMentioned, DateTimeOffset? Reappeared = null);

/// <summary>A failed attempt: a fixed <paramref name="Code"/> and a link to Eddie's result or to the issue.</summary>
public sealed record Failure(string Code, string Link, DateTimeOffset At);

/// <summary>
/// What the desk does in a round. It carries out <paramref name="Actions"/> in order, all on the errand's issue.
/// <paramref name="Errand"/> is the errand after them, or null when there is none. The desk writes what GitHub gives
/// back into the record: the number of the issue that <see cref="OpenIssue"/> opens into <see cref="ErrandRecord.Issue"/>,
/// the id of the comment that <see cref="PostCommand"/> posts into <see cref="ErrandRecord.Command"/>. It saves the record
/// after each of them, and never one whose issue is still 0. A record saved with its issue but command 0 gets the
/// command posted in the next round. <see cref="HoldWish"/> comes first in its decision; the desk saves that decision's
/// held record only once the wish is held, or a task still open for it reads as Michael releasing it.
/// </summary>
public sealed record Decision(ErrandRecord? Errand, IReadOnlyList<DeskAction> Actions);

public abstract record DeskAction;

/// <summary>Open the issue with <paramref name="Label"/>; its number becomes the errand's issue.</summary>
public sealed record OpenIssue(string Title, string Body, string Label) : DeskAction;

/// <summary>Post Eddie's command; its comment id becomes the errand's command.</summary>
public sealed record PostCommand(string Body) : DeskAction;

public sealed record PostComment(string Body) : DeskAction;

public sealed record AddLabel(string Label) : DeskAction;

/// <summary>The village stops asking for <paramref name="Wish"/> (<c>VillageHost.Hold</c>).</summary>
public sealed record HoldWish(string Wish) : DeskAction;

/// <summary>
/// What the errand takes from outside the data: Eddie's bot login, the <paramref name="Owner"/> to mention, the
/// repository for links, the wishes of the data and the ids of the releases live now.
/// </summary>
public sealed record ErrandFacts(string EddieLogin, string Owner, string Repository, IReadOnlyList<Wish> Wishes, IReadOnlyList<string> Releases);

/// <summary>A sentence of the desk in Dutch, for the issue and its comments.</summary>
public sealed record DeskLine(string Nl);

/// <summary>A sentence of the desk in English, for Eddie.</summary>
public sealed record DeskOrder(string En);

/// <summary>
/// The section <c>desk</c> of <c>wishes.json</c>. <see cref="Assignment"/> follows <c>/eddie </c>, with <c>{wish}</c>,
/// <c>{label}</c>, <c>{attempt}</c>, <c>{history}</c> (the <see cref="Attempt"/>s before, or <see cref="None"/>) and
/// <c>{releases}</c> (or <see cref="None"/>); <see cref="Attempt"/> has <c>{attempt}</c>, <c>{code}</c> and <c>{link}</c>.
/// The Dutch lines may have <c>{owner}</c>, <c>{attempt}</c> and <c>{code}</c>.
/// </summary>
public sealed record DeskTexts(DeskLine IssueNote, DeskOrder Assignment, DeskOrder Attempt, DeskOrder None, DeskLine Waiting,
    DeskLine Rejected, DeskLine NotAllowed, DeskLine Live, DeskLine Failed, DeskLine Held, DeskLine Answered)
{
    /// <summary>The desk's texts of <paramref name="data"/>; throws <see cref="InvalidDataException"/> when one is missing.</summary>
    public static DeskTexts From(DataSet data) => data.Section<WishesDesk>("wishes").Desk;

    private sealed record WishesDesk(DeskTexts Desk);
}

/// <summary>
/// The errand of the desk, as a rule: given what is now, what to do. Pure: no I/O, and no clock but <c>now</c>.
/// Nothing that people or Eddie write on GitHub goes into what it writes: only the data, numbers, fixed codes and links.
/// </summary>
public static partial class Errand
{
    /// <summary>After this many failed attempts the village stops asking for the wish.</summary>
    public const int MaxFailures = 3;

    private static readonly TimeSpan Silence = TimeSpan.FromHours(12);
    private static readonly TimeSpan WaitingLimit = TimeSpan.FromDays(3);
    private static readonly TimeSpan LiveLimit = TimeSpan.FromHours(2);
    private static readonly TimeSpan Cooling = TimeSpan.FromHours(6);
    // when Eddie's budget of the day is back, in UTC
    private static readonly TimeSpan BudgetBack = new(0, 10, 0);

    private static readonly HashSet<string> Questions = ["needs_input", "answered", "spec_ready", "plan_ready"];
    private static readonly HashSet<string> Outcomes = ["done", "failed", "reverted", "budget_reached", "not_allowed", .. Questions];

    /// <summary>
    /// What to do now with <paramref name="errand"/> (null when there was none yet), given the open
    /// <paramref name="task"/> of the land, the <paramref name="comments"/> of the errand's issue (oldest first), and
    /// whether the data serves the errand's wish (<paramref name="wishLive"/>). Results count only when Eddie's login
    /// wrote them, for the desk's command or for a later <c>/eddie</c> command by the owner. An errand that waits for
    /// Eddie does not end because the task went away: Eddie's result ends it. A live errand still follows Eddie's results
    /// (a reject reverts and holds the wish), and asks again on the same issue when the task comes back: at once for a
    /// held wish (only Michael releases one), after a wait for Eddie's revert for a live one.
    /// </summary>
    public static Decision Next(ErrandRecord? errand, OpenTask? task, IReadOnlyList<IssueComment> comments, bool wishLive,
        DateTimeOffset now, DeskTexts texts, ErrandFacts facts)
    {
        if (errand is null) return task is null ? new Decision(null, []) : Begin(task, now, texts, facts);
        if (errand.State is ErrandState.Live or ErrandState.Held && task is not null && task.Wish != errand.Wish)
            return Begin(task, now, texts, facts);

        var round = new Round(errand, now, texts, facts);
        return errand.State switch
        {
            ErrandState.Live => round.AfterLive(comments, back: task is not null && !wishLive),
            ErrandState.Held when task is not null => round.Again(),
            ErrandState.Asking when errand.Command == 0 => round.Ask(),
            ErrandState.Asking or ErrandState.Waiting => round.Follow(comments),
            ErrandState.Budget => now >= NextBudget(errand.Since) ? round.Ask() : round.Stay(),
            ErrandState.Delivered when wishLive => round.Move(ErrandState.Live, new PostComment(round.Line(texts.Live))),
            ErrandState.Delivered => now - errand.Since >= LiveLimit ? round.Fail("not-live", round.IssueLink) : round.Stay(),
            ErrandState.Cooling when task?.Wish != errand.Wish => round.Move(ErrandState.Live, new PostComment(round.Line(texts.Answered))),
            ErrandState.Cooling => now - errand.Since >= Cooling ? round.Ask() : round.Stay(),
            _ => round.Stay(),
        };
    }

    private static Decision Begin(OpenTask task, DateTimeOffset now, DeskTexts texts, ErrandFacts facts)
    {
        var errand = new ErrandRecord(task.Wish, 0, 0, ErrandState.Asking, now, 0, [], false);
        var asked = new Round(errand, now, texts, facts).Ask();
        return asked with { Actions = [new OpenIssue(task.TitleNl, $"{task.BodyNl}\n\n{texts.IssueNote.Nl}", "wish"), .. asked.Actions] };
    }

    // the first 00:10 UTC after the budget ran out
    private static DateTimeOffset NextBudget(DateTimeOffset since)
    {
        var next = new DateTimeOffset(since.UtcDateTime.Date + BudgetBack, TimeSpan.Zero);
        return next > since ? next : next.AddDays(1);
    }

    private sealed class Round(ErrandRecord errand, DateTimeOffset now, DeskTexts texts, ErrandFacts facts)
    {
        public string IssueLink => $"https://github.com/{facts.Repository}/issues/{errand.Issue}";

        public Decision Stay() => new(errand, []);

        public Decision Move(ErrandState state, params DeskAction[] actions) => new(errand with { State = state, Since = now }, actions);

        /// <summary>Eddie's command for the next attempt; the desk fills in its id.</summary>
        public Decision Ask()
        {
            var wish = facts.Wishes.FirstOrDefault(wish => wish.Id == errand.Wish);
            var history = errand.History.Select((failure, index) => Fill(texts.Attempt.En,
                ("attempt", $"{index + 1}"), ("code", failure.Code), ("link", failure.Link)));
            var assignment = Fill(texts.Assignment.En,
                ("wish", wish?.En ?? errand.Wish),
                ("label", wish?.Label ?? errand.Wish),
                ("attempt", $"{errand.Failures + 1}"),
                ("history", errand.History.Count == 0 ? texts.None.En : string.Join("; ", history)),
                ("releases", facts.Releases.Count == 0 ? texts.None.En : string.Join(", ", facts.Releases)));
            return new Decision(errand with { State = ErrandState.Asking, Command = 0, Since = now }, [new PostCommand($"/eddie {assignment}")]);
        }

        /// <summary>Asking or waiting: Eddie's latest result that counts, or the time it takes.</summary>
        public Decision Follow(IReadOnlyList<IssueComment> comments)
        {
            var waiting = errand.State == ErrandState.Waiting;
            if (Latest(comments) is { } result && !(waiting && Questions.Contains(result.Outcome)))
            {
                return result.Outcome switch
                {
                    "done" => Move(ErrandState.Delivered),
                    "failed" => Fail("eddie-failed", $"{IssueLink}#issuecomment-{result.Id}"),
                    "reverted" => Hold(texts.Rejected, label: true),
                    "budget_reached" => Move(ErrandState.Budget),
                    "not_allowed" => Hold(texts.NotAllowed, label: false),
                    _ when errand.OwnerMentioned => new Decision(errand with { State = ErrandState.Waiting, Since = now }, []),
                    _ => new Decision(errand with { State = ErrandState.Waiting, Since = now, OwnerMentioned = true },
                        [new PostComment(Line(texts.Waiting))]),
                };
            }
            return now - errand.Since >= (waiting ? WaitingLimit : Silence)
                ? Fail(waiting ? "waiting-too-long" : "timeout", IssueLink)
                : Stay();
        }

        /// <summary>A live wish: a revert by Eddie holds it; a task back without one asks again after <see cref="Cooling"/>.</summary>
        public Decision AfterLive(IReadOnlyList<IssueComment> comments, bool back)
        {
            if (Latest(comments) is { Outcome: "reverted" }) return Hold(texts.Rejected, label: true);
            if (!back) return errand.Reappeared is null ? Stay() : new Decision(errand with { Reappeared = null }, []);
            if (errand.Reappeared is not { } since) return new Decision(errand with { Reappeared = now }, []);
            return now - since >= Cooling ? Again(new Failure("release-gone", IssueLink, now)) : Stay();
        }

        /// <summary>A new round on the same issue, with <paramref name="history"/> as all that went before.</summary>
        public Decision Again(params Failure[] history) =>
            new Round(errand with { Failures = history.Length, History = history, OwnerMentioned = false, Reappeared = null }, now, texts, facts).Ask();

        public Decision Fail(string code, string link)
        {
            var failures = errand.Failures + 1;
            var failed = errand with { Failures = failures, History = [.. errand.History, new Failure(code, link, now)] };
            if (failures >= MaxFailures) return new Round(failed, now, texts, facts).Hold(texts.Held, label: true);
            return new Decision(failed with { State = ErrandState.Cooling, Since = now },
                [new PostComment(Line(texts.Failed, ("attempt", $"{failures}"), ("code", code)))]);
        }

        private Decision Hold(DeskLine line, bool label)
        {
            List<DeskAction> actions = [new HoldWish(errand.Wish), new PostComment(Line(line))];
            if (label) actions.Add(new AddLabel("on-hold"));
            return Move(ErrandState.Held, [.. actions]);
        }

        public string Line(DeskLine line, params (string Name, string Value)[] values) => Fill(line.Nl, [("owner", facts.Owner), .. values]);

        // the latest result by Eddie's login with an outcome the errand knows, for the desk's command or a later one by the owner
        private (long Id, string Outcome)? Latest(IReadOnlyList<IssueComment> comments)
        {
            var owners = comments
                .Where(comment => comment.Id > errand.Command && string.Equals(comment.Author, facts.Owner, StringComparison.OrdinalIgnoreCase)
                    && comment.Body.StartsWith("/eddie ", StringComparison.Ordinal))
                .Select(comment => comment.Id)
                .ToHashSet();
            foreach (var comment in comments.OrderByDescending(comment => comment.Id))
            {
                if (EddieResult.Parse(comment, facts.EddieLogin) is { } result
                    && (result.Command == errand.Command || owners.Contains(result.Command)) && Outcomes.Contains(result.Outcome))
                    return (comment.Id, result.Outcome);
            }
            return null;
        }
    }

    // each {name} once, by its value; a value is never read for placeholders itself
    private static string Fill(string template, params (string Name, string Value)[] values) =>
        Placeholder().Replace(template, match => values.FirstOrDefault(value => value.Name == match.Groups[1].Value) is { Name: not null } found
            ? found.Value
            : match.Value);

    [GeneratedRegex(@"\{([a-z]+)\}")]
    private static partial Regex Placeholder();
}
