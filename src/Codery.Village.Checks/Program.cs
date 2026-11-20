namespace Codery.Village.Checks;

// A class in a namespace, not top-level statements: with ASP.NET referenced (for the village setup), those would
// make a second public global Program next to the server's.
internal static class Program
{
    // Commands: paths <base> <head> <bot>          (what a pull request of Eddie's may change)
    //           production <url> <ticks> [--eddie] (the checked-out data on the production state)
    private static async Task<int> Main(string[] args) => args switch
    {
        ["paths", var baseSha, var head, var bot] => Paths(baseSha, head, bot),
        ["production", var url, var ticks] when int.TryParse(ticks, out var count) => await Production(url, count, eddie: false),
        ["production", var url, var ticks, "--eddie"] when int.TryParse(ticks, out var count) => await Production(url, count, eddie: true),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: paths <base> <head> <bot>");
        Console.Error.WriteLine("       production <url> <ticks> [--eddie]");
        return 2;
    }

    private static int Paths(string baseSha, string head, string bot)
    {
        var problems = GitPaths.Problems(baseSha, head, bot);
        foreach (var problem in problems)
            Console.Error.WriteLine(problem);
        Console.WriteLine(problems.Count == 0 ? "All changed paths are allowed." : $"{problems.Count} problem(s).");
        return problems.Count == 0 ? 0 : 1;
    }

    private static async Task<int> Production(string url, int ticks, bool eddie)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = ProductionTrial.MaxAnswerBytes };
        return await ProductionTrial.Check(http, url, ticks, eddie, Console.Out);
    }
}
