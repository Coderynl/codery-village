using Codery.Village.Modules.People;

namespace Codery.Village.Modules.Talk;

/// <summary>The part of the section "talk" of the data that the module reads; the server reads the rest.</summary>
public sealed record TalkSettings(
    // per island at most one moment per this many calendar minutes
    int MinMinutesBetween,
    // a request without an answer after this many calendar minutes gets a template
    int AnswerWithinMinutes,
    // how many conversations the save keeps
    int Keep,
    TalkTemplates Templates,
    IReadOnlyDictionary<string, Sentence> Log);

/// <summary>
/// The conversations to fall back on, per kind of topic. The placeholders are <c>{a}</c>, <c>{b}</c> (the names)
/// and <c>{topic}</c> (the name of the topic in the language of the line). With a work topic, <c>{a}</c> does the work.
/// </summary>
public sealed record TalkTemplates(
    IReadOnlyList<TalkTemplate> Wish,
    IReadOnlyList<TalkTemplate> Work,
    IReadOnlyList<TalkTemplate> Interest)
{
    public IReadOnlyList<TalkTemplate> Of(string kind) => kind switch
    {
        "wish" => Wish,
        "work" => Work,
        _ => Interest,
    };

    /// <summary>The template for the request, filled in. Which one is fixed by the id of the request.</summary>
    public TalkLines Fill(TalkRequest request)
    {
        var all = Of(request.Facts.Topic.Kind);
        var template = all[(int)(request.Id % all.Count)];
        var facts = request.Facts;
        return new TalkLines(
            [.. template.Nl.Select(line => Fill(line, facts, facts.Topic.Name.Nl))],
            [.. template.En.Select(line => Fill(line, facts, facts.Topic.Name.En))],
            FromTemplate: true);
    }

    private static string Fill(string line, TalkFacts facts, string topic) =>
        line.Replace("{a}", facts.A.Name).Replace("{b}", facts.B.Name).Replace("{topic}", topic);
}

/// <summary>A conversation of two to four lines, in both languages.</summary>
public sealed record TalkTemplate(IReadOnlyList<string> Nl, IReadOnlyList<string> En);
