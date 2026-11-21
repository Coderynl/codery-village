using System.Diagnostics;

namespace Codery.Village.Checks;

/// <summary>Reads a pull request from git and hands it to <see cref="ReleasePaths"/>.</summary>
public static class GitPaths
{
    /// <summary>The problems with what the pull request <paramref name="head"/> changes since it branched off <paramref name="baseSha"/>.</summary>
    public static IReadOnlyList<string> Problems(string baseSha, string head, string bot, string? directory = null)
    {
        // three dots: since the merge base, so what landed on main after Eddie branched is not his
        var changes = ParseRaw(Git(directory, "diff", "--raw", "-z", $"{baseSha}...{head}"));
        return ReleasePaths.Problems(changes, path => AddedBy(directory, baseSha, path, bot), bot);
    }

    /// <summary>
    /// The bot when every commit that touched the path on the base is the bot's (and there is one), so a file Michael
    /// ever edited stays his; otherwise the first author who is not the bot, or null when the base has no such file.
    /// </summary>
    private static string? AddedBy(string? directory, string baseSha, string path, string bot)
    {
        var authors = Git(directory, "log", "--format=%an", baseSha, "--", path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (authors.Length == 0)
            return null;
        return authors.FirstOrDefault(author => author != bot) ?? bot;
    }

    /// <summary>The output of <c>git diff --raw -z</c>: <c>:oldmode newmode oldsha newsha status NUL path NUL</c>, with two paths for a rename or copy.</summary>
    public static IReadOnlyList<Change> ParseRaw(string output)
    {
        var tokens = output.Split('\0');
        var changes = new List<Change>();
        for (var i = 0; i < tokens.Length && tokens[i].StartsWith(':'); )
        {
            var fields = tokens[i][1..].Split(' ');
            var kind = fields[4][0];
            var twoPaths = kind is 'R' or 'C';
            var path = tokens[i + (twoPaths ? 2 : 1)];
            // a file, not a symlink (120000) or a submodule (160000); for a delete the file that was there
            var mode = kind == 'D' ? fields[0] : fields[1];
            changes.Add(new Change(kind == 'C' ? 'R' : kind, path, mode is "100644" or "100755"));
            i += twoPaths ? 3 : 2;
        }
        return changes;
    }

    private static string Git(string? directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        if (directory is not null)
            start.WorkingDirectory = directory;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}).");
        return output;
    }
}
