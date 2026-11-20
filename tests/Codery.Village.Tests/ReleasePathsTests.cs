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
