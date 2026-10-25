using Azure.AI.AgentServer.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;

// The smallest hosted agent the Responses protocol allows, with the SDK's default telemetry and nothing
// changed about how it picks up the incoming trace context.
var builder = AgentHost.CreateBuilder(args);

builder.Services.AddFoundryResponses(new ChatClientAgent(new FixedReplyChatClient(), name: "trace-repro"));
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

builder.Build().Run();

/// <summary>Always answers the same text, so the repro needs no model deployment.</summary>
internal sealed class FixedReplyChatClient : IChatClient
{
    private const string Reply = "Fixed reply from the trace repro agent.";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, Reply) { MessageId = Guid.NewGuid().ToString("N") };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
