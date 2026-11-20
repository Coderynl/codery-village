using System.Text.RegularExpressions;

namespace Codery.Village.Server.Desk;

/// <summary>
/// Eddie's result under an issue: the marker <c>eddie:result cmd=… run=… outcome=… sig=…</c> that ends each of his runs.
/// The signature cannot be checked without Eddie's key, so the author is what counts: anyone can type the marker.
/// </summary>
public static partial class EddieResult
{
    /// <returns>
    /// The id of the <c>/eddie</c> command the run answers and its outcome; null unless <paramref name="comment"/> is by
    /// <paramref name="eddieLogin"/> (in any case) and its last marker is a result with an outcome.
    /// </returns>
    public static (long Command, string Outcome)? Parse(IssueComment comment, string eddieLogin)
    {
        if (eddieLogin.Length == 0 || !string.Equals(comment.Author, eddieLogin, StringComparison.OrdinalIgnoreCase)) return null;
        // the last marker, as Eddie reads his own: a comment may quote an older one
        var markers = Marker().Matches(comment.Body);
        if (markers.Count == 0) return null;
        var match = markers[^1];
        if (match.Groups["kind"].Value != "result" || !match.Groups["outcome"].Success) return null;
        return long.TryParse(match.Groups["cmd"].Value, out var command) ? (command, match.Groups["outcome"].Value) : null;
    }

    // as Eddie writes it
    [GeneratedRegex(@"eddie:(?<kind>status|result) cmd=(?<cmd>\d{1,19}) run=([A-Za-z0-9\-]{1,64})(?: outcome=(?<outcome>[a-z_]{1,32}))? sig=([0-9a-f]{16})")]
    private static partial Regex Marker();
}
