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
    var problems = GitPaths.Problems(baseSha, head, bot);
    foreach (var problem in problems)
        Console.Error.WriteLine(problem);
    Console.WriteLine(problems.Count == 0 ? "All changed paths are allowed." : $"{problems.Count} problem(s).");
    return problems.Count == 0 ? 0 : 1;
}
