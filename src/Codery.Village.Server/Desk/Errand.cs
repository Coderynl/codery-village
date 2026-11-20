using System.Text.RegularExpressions;
using Codery.Village.Core.Data;
using Codery.Village.Modules.Wishes;

namespace Codery.Village.Server.Desk;

public enum ErrandState { Asking, Waiting, Budget, Delivered, Live, Cooling, Held, Closed }

/// <summary>
/// What the desk keeps of the errand for one <paramref name="Wish"/>: its <paramref name="Issue"/>, the id of the desk's
/// latest <c>/eddie</c> <paramref name="Command"/>, its <paramref name="State"/> and <paramref name="Since"/> when, the
/// <paramref name="Failures"/> so far with their <paramref name="History"/>, and whether Michael was mentioned while
/// Eddie waited for him. An <paramref name="Issue"/> or <paramref name="Command"/> of 0 is one the desk has yet to fill
/// in; see <see cref="Decision"/>. <paramref name="Reappeared"/> is when the task of a live wish came back because its
/// release went away; it stays until the wish is live again.
/// </summary>
public sealed record ErrandRecord(string Wish, int Issue, long Command, ErrandState State, DateTimeOffset Since,
    int Failures, IReadOnlyList<Failure> History, bool OwnerMentioned, DateTimeOffset? Reappeared = null);

/// <summary>A failed attempt: a fixed <paramref name="Code"/> and a link to Eddie's result or to the issue.</summary>
public sealed record Failure(string Code, string Link, DateTimeOffset At);

/// <summary>
/// What the desk does in a round for one errand. <paramref name="Errand"/> is the record after the actions, or null
/// when there is none. The desk:
/// <list type="bullet">
/// <item>carries out <paramref name="Actions"/> in order, all on the errand's issue, each followed by saving the
/// record, without the stopping token, so a stop never falls between an action and its save;</item>
/// <item>writes what GitHub gives back into the record: the number of the issue <see cref="OpenIssue"/> opens into
/// <see cref="ErrandRecord.Issue"/>, the id of the comment <see cref="PostCommand"/> posts into
/// <see cref="ErrandRecord.Command"/>; it never saves a record whose issue is still 0;</item>
/// <item>stops at the first action that fails, and saves nothing more: the next round decides again from the stored
/// record. A command that was posted although its answer got lost is then adopted, not posted again;</item>
/// <item>before <see cref="OpenIssue"/>, looks for an open issue by the App, labelled as the action says, with its
/// title, and takes that number instead of opening a second one;</item>
/// <item>finds <see cref="HoldWish"/> first in its decision, so a held record is saved only once the wish is held.</item>
/// </list>
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
/// What the errand takes from outside the data: Eddie's bot login, the App's own login (whose <c>/eddie</c> comments
/// are the desk's commands), the <paramref name="Owner"/> to mention and whose later commands count, the repository
/// for links, the wishes of the data, the ids of the releases live now, and the wishes on hold.
/// </summary>
public sealed record ErrandFacts(string EddieLogin, string AppLogin, string Owner, string Repository, IReadOnlyList<Wish> Wishes,
    IReadOnlyList<string> Releases, IReadOnlySet<string> Held);

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
    // a live errand also hears Michael's final approve
    private static readonly HashSet<string> LiveOutcomes = [.. Outcomes, "approved"];

    /// <summary>
    /// What to do now with one record: <paramref name="errand"/> for its own wish, or null to begin one for
    /// <paramref name="task"/>. The desk calls it once per round for each record that is neither Held nor Closed, and for
    /// the open task's wish: its record when that is not Closed, otherwise null. <paramref name="task"/> is the open task
    /// of the land, which may be for another wish; <paramref name="comments"/> are those of the record's issue (oldest
    /// first, none without an issue), and <paramref name="wishLive"/> says whether the data serves the record's wish.
    /// <para>
    /// Results count only when Eddie's login wrote them, for the desk's command or for a later <c>/eddie</c> command by
    /// the owner. An errand that waits for Eddie does not end because the task went away: Eddie's result ends it. A live
    /// errand follows Eddie's results until Michael's approve closes it (a reject holds the wish), and asks again on the
    /// same issue when its task comes back without the wish being live. A held wish is asked again on its issue once
    /// Michael released it and the land wishes it again. A record is never replaced by one for another wish.
    /// </para>
    /// </summary>
    public static Decision Next(ErrandRecord? errand, OpenTask? task, IReadOnlyList<IssueComment> comments, bool wishLive,
        DateTimeOffset now, DeskTexts texts, ErrandFacts facts)
    {
        if (errand is null) return task is null ? new Decision(null, []) : Begin(task, now, texts, facts);

        // the land still wishes what this errand is about
        var wished = task?.Wish == errand.Wish;
        var round = new Round(errand, comments, now, texts, facts);
        return errand.State switch
        {
            ErrandState.Closed => round.Stay(),
            ErrandState.Held => wished && !facts.Held.Contains(errand.Wish) ? round.Again() : round.Stay(),
            ErrandState.Live => round.AfterLive(wished, wishLive),
            ErrandState.Asking when errand.Command == 0 => round.Ask(),
            ErrandState.Asking or ErrandState.Waiting => round.Follow(),
            ErrandState.Budget => now >= NextBudget(errand.Since) ? round.Ask() : round.Stay(),
            ErrandState.Delivered => round.Deliver(wishLive),
            ErrandState.Cooling => round.Cool(wished),
            _ => round.Stay(),
        };
    }

    private static Decision Begin(OpenTask task, DateTimeOffset now, DeskTexts texts, ErrandFacts facts)
    {
        var errand = new ErrandRecord(task.Wish, 0, 0, ErrandState.Asking, now, 0, [], false);
        var asked = new Round(errand, [], now, texts, facts).Ask();
        return asked with { Actions = [new OpenIssue(task.TitleNl, $"{task.BodyNl}\n\n{texts.IssueNote.Nl}", "wish"), .. asked.Actions] };
    }

    // the first 00:10 UTC after the budget ran out
    private static DateTimeOffset NextBudget(DateTimeOffset since)
    {
        var next = new DateTimeOffset(since.UtcDateTime.Date + BudgetBack, TimeSpan.Zero);
        return next > since ? next : next.AddDays(1);
    }

    private sealed class Round(ErrandRecord errand, IReadOnlyList<IssueComment> comments, DateTimeOffset now, DeskTexts texts, ErrandFacts facts)
    {
        private string IssueLink => $"https://github.com/{facts.Repository}/issues/{errand.Issue}";

        public Decision Stay() => new(errand, []);

        private Decision Move(ErrandState state, params DeskAction[] actions) => new(errand with { State = state, Since = now }, actions);

        private Round With(ErrandRecord changed) => new(changed, comments, now, texts, facts);

        /// <summary>
        /// Eddie's command for the next attempt. The record keeps the previous command until the desk fills in the new
        /// one; a command by the App after the previous one was posted already, and is adopted instead.
        /// </summary>
        public Decision Ask()
        {
            var asking = errand with { State = ErrandState.Asking, Since = now };
            var posted = comments
                .Where(comment => comment.Id > errand.Command && Is(comment.Author, facts.AppLogin) && IsCommand(comment))
                .MaxBy(comment => comment.Id);
            if (posted is not null) return new Decision(asking with { Command = posted.Id }, []);

            var wish = facts.Wishes.FirstOrDefault(wish => wish.Id == errand.Wish);
            var history = errand.History.Select((failure, index) => Fill(texts.Attempt.En,
                ("attempt", $"{index + 1}"), ("code", failure.Code), ("link", failure.Link)));
            var releases = facts.Releases.Where(id => ReleaseId().IsMatch(id)).ToList();
            var assignment = Fill(texts.Assignment.En,
                ("wish", wish?.En ?? errand.Wish),
                ("label", wish?.Label ?? errand.Wish),
                ("attempt", $"{errand.Failures + 1}"),
                ("history", errand.History.Count == 0 ? texts.None.En : string.Join("; ", history)),
                ("releases", releases.Count == 0 ? texts.None.En : string.Join(", ", releases)));
            return new Decision(asking, [new PostCommand($"/eddie {assignment}")]);
        }

        /// <summary>Asking or waiting: Eddie's latest result that counts, or the time it takes.</summary>
        public Decision Follow()
        {
            var waiting = errand.State == ErrandState.Waiting;
            if (Latest(Outcomes) is { } result && !(waiting && Questions.Contains(result.Outcome)))
            {
                return result.Outcome switch
                {
                    "done" => Move(ErrandState.Delivered),
                    "failed" => Fail("eddie-failed", $"{IssueLink}#issuecomment-{result.Id}"),
                    "reverted" => Rejected(),
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

        /// <summary>Eddie called it done: a reject holds the wish, the data serving it makes it live, else it is not live in time.</summary>
        public Decision Deliver(bool wishLive)
        {
            if (Latest(Outcomes) is { Outcome: "reverted" }) return Rejected();
            if (wishLive) return Move(ErrandState.Live, new PostComment(Line(texts.Live)));
            return now - errand.Since >= LiveLimit ? Fail("not-live", IssueLink) : Stay();
        }

        /// <summary>
        /// After a failure: a late result of Eddie's still counts, a task gone means another release answered the wish,
        /// and after <see cref="Cooling"/> the village asks again.
        /// </summary>
        public Decision Cool(bool wished)
        {
            var latest = Latest(Outcomes);
            if (latest is { Outcome: "reverted" }) return Rejected();
            // a done that already ran out of time to go live is not delivered again
            if (latest is { Outcome: "done" } && errand.History is not [.., { Code: "not-live" }]) return Move(ErrandState.Delivered);
            if (!wished) return Move(ErrandState.Closed, new PostComment(Line(texts.Answered)));
            return now - errand.Since >= Cooling ? Ask() : Stay();
        }

        /// <summary>
        /// A live wish: Michael's approve closes the errand, a reject holds the wish. When the wish is no longer live and
        /// its task is back, the errand waits <see cref="Cooling"/> for the revert, then counts a failure and asks again.
        /// </summary>
        public Decision AfterLive(bool wished, bool wishLive)
        {
            var latest = Latest(LiveOutcomes);
            if (latest is { Outcome: "reverted" }) return Rejected();
            if (latest is { Outcome: "approved" }) return Move(ErrandState.Closed);
            if (wishLive) return errand.Reappeared is null ? Stay() : new Decision(errand with { Reappeared = null }, []);
            if (!wished) return Stay();
            if (errand.Reappeared is not { } since) return new Decision(errand with { Reappeared = now }, []);
            return now - since >= Cooling ? Fail("release-gone", IssueLink, askAtOnce: true) : Stay();
        }

        /// <summary>A new round on the same issue: the count starts over, as Michael released the wish.</summary>
        public Decision Again() => With(errand with { Failures = 0, History = [], OwnerMentioned = false, Reappeared = null }).Ask();

        private Decision Fail(string code, string link, bool askAtOnce = false)
        {
            var failures = errand.Failures + 1;
            var failed = With(errand with { Failures = failures, History = [.. errand.History, new Failure(code, link, now)], Reappeared = null });
            if (failures >= MaxFailures) return failed.Hold(texts.Held, label: true);
            if (askAtOnce) return failed.Ask();
            return failed.Move(ErrandState.Cooling, new PostComment(Line(texts.Failed, ("attempt", $"{failures}"), ("code", code))));
        }

        private Decision Rejected() => Hold(texts.Rejected, label: true);

        private Decision Hold(DeskLine line, bool label)
        {
            List<DeskAction> actions = [new HoldWish(errand.Wish), new PostComment(Line(line))];
            if (label) actions.Add(new AddLabel("on-hold"));
            return Move(ErrandState.Held, [.. actions]);
        }

        private string Line(DeskLine line, params (string Name, string Value)[] values) => Fill(line.Nl, [("owner", facts.Owner), .. values]);

        // the latest result by Eddie's login with one of these outcomes, for the desk's command or a later one by the owner
        private (long Id, string Outcome)? Latest(IReadOnlySet<string> outcomes)
        {
            var owners = comments
                .Where(comment => comment.Id > errand.Command && Is(comment.Author, facts.Owner) && IsCommand(comment))
                .Select(comment => comment.Id)
                .ToHashSet();
            foreach (var comment in comments.OrderByDescending(comment => comment.Id))
            {
                if (EddieResult.Parse(comment, facts.EddieLogin) is { } result
                    && (result.Command == errand.Command || owners.Contains(result.Command)) && outcomes.Contains(result.Outcome))
                    return (comment.Id, result.Outcome);
            }
            return null;
        }

        private static bool Is(string author, string login) => login.Length > 0 && string.Equals(author, login, StringComparison.OrdinalIgnoreCase);

        private static bool IsCommand(IssueComment comment) => comment.Body.StartsWith("/eddie ", StringComparison.Ordinal);
    }

    // each {name} once, by its value; a value is never read for placeholders itself
    private static string Fill(string template, params (string Name, string Value)[] values) =>
        Placeholder().Replace(template, match => values.FirstOrDefault(value => value.Name == match.Groups[1].Value) is { Name: not null } found
            ? found.Value
            : match.Value);

    [GeneratedRegex(@"\{([a-z]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex ReleaseId();
}
