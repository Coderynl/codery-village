namespace Codery.Village.Server.Desk;

/// <summary>One comment under an issue.</summary>
public sealed record IssueComment(long Id, string Author, string Body, DateTimeOffset At);

/// <summary>What the village does on GitHub: open an issue, comment, read the comments, label.</summary>
public interface IVillageGitHub
{
    /// <returns>The number of the new issue.</returns>
    Task<int> OpenIssue(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellation);

    /// <returns>The id of the new comment.</returns>
    Task<long> Comment(int issue, string body, CancellationToken cancellation);

    /// <summary>All comments, oldest first.</summary>
    Task<IReadOnlyList<IssueComment>> Comments(int issue, CancellationToken cancellation);

    Task Label(int issue, string label, CancellationToken cancellation);
}
