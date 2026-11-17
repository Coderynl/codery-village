using Codery.Village.Modules.Talk;

namespace Codery.Village.Server.Talk;

/// <summary>Writes a conversation for a request; null when it cannot, and the worker then takes a template.</summary>
public interface ITalker
{
    Task<TalkLines?> Write(TalkRequest request, CancellationToken cancel);
}
