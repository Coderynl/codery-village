using System.Diagnostics;
using Codery.Village.Checks;

// Commands: paths <base> <head> <bot>  (what a pull request of Eddie's may change)
return args switch
{
    ["paths", var baseSha, var head, var bot] => Paths(baseSha, head, bot),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: paths <base> <head> <bot>");
    return 2;
}

static int Paths(string baseSha, string head, string bot)
{
    var changes = Git("diff", "--name-status", baseSha, head)
        .Select(line => line.Split('\t'))
        .Select(parts => new Change(parts[0][0], parts[^1]))
        .ToList();
    var problems = ReleasePaths.Problems(changes, path => Git("log", "--diff-filter=A", "--format=%an", head, "--", path).FirstOrDefault(), bot);
    foreach (var problem in problems)
        Console.Error.WriteLine(problem);
    Console.WriteLine(problems.Count == 0 ? $"{changes.Count} changed paths, all allowed." : $"{problems.Count} problem(s).");
    return problems.Count == 0 ? 0 : 1;
}

static List<string> Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var lines = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}).");
    return lines;
}
