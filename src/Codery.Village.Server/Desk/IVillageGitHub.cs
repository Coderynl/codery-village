namespace Codery.Village.Server.Desk;

/// <summary>One comment under an issue.</summary>
public sealed record IssueComment(long Id, string Author, string Body, DateTimeOffset At);

/// <summary>An open issue as a list shows it: its number, title and who opened it.</summary>
public sealed record IssueSummary(int Number, string Title, string Author);

/// <summary>What the village does on GitHub: open an issue, comment, read the comments, label, and list the open issues.</summary>
public interface IVillageGitHub
{
    /// <returns>The number of the new issue.</returns>
    Task<int> OpenIssue(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellation);

    /// <returns>The id of the new comment.</returns>
    Task<long> Comment(int issue, string body, CancellationToken cancellation);

    /// <summary>All comments, oldest first.</summary>
    Task<IReadOnlyList<IssueComment>> Comments(int issue, CancellationToken cancellation);

    Task Label(int issue, string label, CancellationToken cancellation);

    /// <summary>The open issues with <paramref name="label"/>, without pull requests.</summary>
    Task<IReadOnlyList<IssueSummary>> OpenIssues(string label, CancellationToken cancellation);
}
