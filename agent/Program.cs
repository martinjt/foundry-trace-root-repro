using System.Diagnostics;
using Azure.AI.AgentServer.Core;
using FixedReplyAgent;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;

// The smallest hosted agent the Responses protocol allows, with the SDK's default telemetry and nothing
// changed about how it picks up the incoming trace context, unless the workaround switch below is on.
var builder = AgentHost.CreateBuilder(args);

// Workaround switch (default off, see README "Workaround: x-client-* headers"): prefer
// x-client-traceparent/x-client-tracestate/x-client-baggage, which the Foundry gateway forwards
// unchanged, over the plain headers it rewrites. Needs both overrides below, or they disagree and
// OpenTelemetry's ASP.NET Core instrumentation starts a second server span (see the README).
var clientHeaderPropagation = bool.TryParse(builder.Configuration["CLIENT_HEADER_PROPAGATION"], out var enabled) && enabled;
if (clientHeaderPropagation)
{
    // ASP.NET Core's hosting layer resolves the propagator for its own server-span creation from DI
    // (captured once via services.TryAddSingleton(DistributedContextPropagator.Current) while
    // WebApplication.CreateSlimBuilder sets up defaults, before this line runs), not by re-reading
    // DistributedContextPropagator.Current per request. Setting .Current here would be silently
    // ignored for inbound requests, so the override has to go through the service collection instead,
    // adding a later registration that wins over the earlier TryAddSingleton one.
    builder.Services.AddSingleton<DistributedContextPropagator>(new ClientHeaderContextPropagator(DistributedContextPropagator.Current));
    builder.Services.AddHttpContextAccessor();

    // That alone parents the activity correctly but does not join the trace: OpenTelemetry's ASP.NET
    // Core instrumentation performs its own, second extraction from Propagators.DefaultTextMapPropagator,
    // and starts a replacement server span -- the one actually exported -- if it disagrees with the
    // activity the DI propagator above already parented. AgentHost sets its own default propagator
    // while it builds, earlier than this; deferring via ConfigureOpenTelemetryTracerProvider runs this
    // once the TracerProviderBuilder is actually built, after any of that setup, so this registration wins.
    builder.Services.ConfigureOpenTelemetryTracerProvider((_, _) =>
    {
        if (Propagators.DefaultTextMapPropagator is not ClientHeaderTextMapPropagator)
        {
            Sdk.SetDefaultTextMapPropagator(new ClientHeaderTextMapPropagator(Propagators.DefaultTextMapPropagator));
        }
    });
}

builder.Services.AddFoundryResponses(new ChatClientAgent(new FixedReplyChatClient(), name: "trace-repro"));
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
if (clientHeaderPropagation)
{
    ClientHeaderLink.Register(app.App.Services.GetRequiredService<IHttpContextAccessor>());
}

app.Run();

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
