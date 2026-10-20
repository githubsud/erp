using System.Collections.Concurrent;
using Erp.Modules.Ai.Providers;

namespace Erp.IntegrationTests.Infrastructure;

/// <summary>Stands in for every AI provider: records what would have been sent and answers with the test's script.</summary>
public sealed class FakeAiChatClient : IAiChatClient
{
    public ConcurrentQueue<(AiEndpoint Endpoint, AiRequest Request)> Calls { get; } = new();
    public Func<AiEndpoint, AiRequest, AiResponse> Respond { get; set; } = (_, _) => new AiResponse("OK", [], 10, 2);

    public Task<AiResponse> CompleteAsync(AiEndpoint endpoint, AiRequest request, CancellationToken ct)
    {
        Calls.Enqueue((endpoint, request));
        return Task.FromResult(Respond(endpoint, request));
    }

    public void Reset(Func<AiEndpoint, AiRequest, AiResponse>? respond = null)
    {
        Calls.Clear();
        Respond = respond ?? ((_, _) => new AiResponse("OK", [], 10, 2));
    }
}
