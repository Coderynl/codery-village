using Codery.Village.Core.Modules;
using Codery.Village.Modules.People;

namespace Codery.Village.Modules.Talk;

/// <summary>
/// A moment for a conversation, for the host to have written. <paramref name="Topic"/> is the kind and the id,
/// such as <c>wish:cafe</c> or <c>work:fisher</c>. When the topic is work, <paramref name="A"/> is the one who does it.
/// </summary>
public sealed record TalkRequest(long Id, int Island, int A, int B, string Topic, TalkFacts Facts);

/// <summary>Everything the writer gets to know: the two residents and the topic. Nothing about visitors.</summary>
public sealed record TalkFacts(TalkPerson A, TalkPerson B, TalkTopic Topic);

/// <summary>A resident as the writer sees him: name, phase, work, and the traits that stand out, such as <c>high openness</c>.</summary>
public sealed record TalkPerson(string Name, LifePhase Phase, IReadOnlyList<string> Traits, TalkName? Work = null);

/// <summary>What they talk about: <paramref name="Kind"/> is <c>wish</c>, <c>work</c> or <c>interest</c>.</summary>
public sealed record TalkTopic(string Kind, TalkName Name);

/// <summary>A name in both languages, as the data gives it.</summary>
public sealed record TalkName(string Nl, string En);

/// <summary>A conversation in Dutch and in English, line by line; <paramref name="FromTemplate"/> when no model wrote it.</summary>
public sealed record TalkLines(IReadOnlyList<string> Nl, IReadOnlyList<string> En, bool FromTemplate);

/// <summary>A conversation that was held, as the module keeps it.</summary>
public sealed record Conversation(long Id, long Tick, int Island, int A, int B, string Topic, TalkLines Lines);

/// <summary>Two residents talked. The lines change nothing in the village; the hero shows them.</summary>
public sealed record Talked(long Tick, int A, int B, TalkLines Lines) : VillageEvent(Tick);
