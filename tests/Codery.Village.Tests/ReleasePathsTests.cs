using Codery.Village.Checks;

namespace Codery.Village.Tests;

// Risk: Eddie's pull request touches more than a release and merges unreviewed.
public class ReleasePathsTests
{
    private const string Bot = "eddie[bot]";

    private static IReadOnlyList<string> Check(string? addedBy, params Change[] changes) =>
        ReleasePaths.Problems(changes, _ => addedBy, Bot);

    [Fact]
    public void A_new_release_and_a_line_in_the_readme_pass()
    {
        var problems = Check(null, new Change('A', "data/releases/wine.json"), new Change('M', "data/releases/README.md"));

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData('M', "src/x.cs")]
    [InlineData('A', "data/wishes.json")]
    [InlineData('M', ".github/workflows/ci.yml")]
    [InlineData('R', "data/releases/wine.json")]
    [InlineData('M', "data/releases/mill.json")]
    [InlineData('A', "data/releases/Wine_2.json")]
    [InlineData('A', "data/releases/sub/wine.json")]
    public void Anything_outside_the_releases_fails(char kind, string path)
    {
        var problems = Check(Bot, new Change(kind, path));

        Assert.Single(problems);
        Assert.Contains(path, problems[0]);
    }

    [Fact]
    public void Eddie_may_delete_only_what_his_bot_added()
    {
        var problems = ReleasePaths.Problems(
            [new Change('D', "data/releases/bakery.json"), new Change('D', "data/releases/wine.json")],
            path => path.EndsWith("wine.json") ? Bot : "Codery-nl",
            Bot);

        Assert.Single(problems);
        Assert.Contains("bakery.json", problems[0]);
    }
}

// Risk: the lookup in git lets Eddie delete a release that is not his, or blames him for what landed on main.
public sealed class GitPathsTests : IDisposable
{
    private const string Bot = "eddie[bot]";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "paths-" + Guid.NewGuid().ToString("N"));

    public GitPathsTests()
    {
        Directory.CreateDirectory(_dir);
        Git("init", "-q", "-b", "main");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, true);
    }

    private string Git(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardOutput = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private string Commit(string author, string message, params (string Path, string? Text)[] files)
    {
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(_dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (text is null) File.Delete(full); else File.WriteAllText(full, text);
        }
        Git("add", "-A");
        Git("-c", $"user.name={author}", "-c", "user.email=x@example.com", "commit", "-q", "--allow-empty", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    private IReadOnlyList<string> Problems(string baseSha) => GitPaths.Problems(baseSha, "HEAD", Bot, _dir);

    [Fact]
    public void Eddie_cannot_launder_a_release_by_deleting_it_adding_it_again_and_deleting_it_again()
    {
        var baseSha = Commit("Michael", "base", ("data/releases/bakery.json", "{}"));
        Git("checkout", "-q", "-b", "eddie/1-x");
        Commit(Bot, "delete", ("data/releases/bakery.json", null));
        Commit(Bot, "re-add", ("data/releases/bakery.json", "{}"));
        Commit(Bot, "delete again", ("data/releases/bakery.json", null));

        var problems = Problems(baseSha);

        Assert.Single(problems);
        Assert.Contains("bakery.json", problems[0]);
    }

    [Fact]
    public void Eddie_may_delete_a_release_only_his_bot_ever_touched()
    {
        Commit("Michael", "base", ("data/releases/README.md", "x"));
        var baseSha = Commit(Bot, "wine", ("data/releases/wine.json", "{}"));
        Git("checkout", "-q", "-b", "eddie/2-revert");
        Commit(Bot, "revert", ("data/releases/wine.json", null));
        Assert.Empty(Problems(baseSha));

        // Michael edited it on main afterwards: it is his now
        Git("checkout", "-q", "main");
        Commit("Michael", "tweak", ("data/releases/wine.json", "{ }"));
        var tweaked = Git("rev-parse", "HEAD");
        Git("checkout", "-q", "-b", "eddie/3-revert");
        Commit(Bot, "revert", ("data/releases/wine.json", null));
        Assert.Single(Problems(tweaked));
    }

    [Fact]
    public void What_landed_on_main_after_Eddie_branched_is_not_his()
    {
        var branchPoint = Commit("Michael", "base", ("data/releases/README.md", "x"));
        Git("checkout", "-q", "-b", "eddie/4-wine");
        Commit(Bot, "wine", ("data/releases/wine.json", "{}"));
        var eddie = Git("rev-parse", "HEAD");
        Git("checkout", "-q", "main");
        var main = Commit("Michael", "code", ("src/x.cs", "class X {}"));

        // the pull request's base is the tip of main, its head is Eddie's branch
        Assert.Empty(GitPaths.Problems(main, eddie, Bot, _dir));
        Assert.NotEqual(branchPoint, main);
    }

    [Fact]
    public void A_symlink_is_not_a_release()
    {
        var baseSha = Commit("Michael", "base", ("data/releases/README.md", "x"));
        Git("checkout", "-q", "-b", "eddie/5-link");
        File.WriteAllText(Path.Combine(_dir, "target.txt"), "../../src/Program.cs");
        var blob = Git("hash-object", "-w", "target.txt");
        Git("update-index", "--add", "--cacheinfo", $"120000,{blob},data/releases/evil.json");
        Git("-c", $"user.name={Bot}", "-c", "user.email=x@example.com", "commit", "-q", "-m", "link");

        var problems = Problems(baseSha);

        Assert.Contains(problems, problem => problem.Contains("evil.json"));
    }
}
