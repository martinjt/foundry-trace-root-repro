using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.Identity;
using OpenTelemetry.Trace;

// Plain OpenTelemetry defaults: W3C trace context goes out on the HttpClient call to the agent,
// and OTEL_EXPORTER_OTLP_* say where the spans go.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());

var app = builder.Build();

app.MapGet("/ask", AskTheAgentAsync);

app.Run();

static async Task<IResult> AskTheAgentAsync(
    IHttpClientFactory httpClients, TokenCredential credential, IConfiguration configuration, CancellationToken cancellationToken)
{
    var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), cancellationToken);
    using var request = new HttpRequestMessage(HttpMethod.Post, configuration["AGENT_RESPONSES_URL"])
    {
        Content = JsonContent.Create(new { input = "hello" }),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

    using var response = await httpClients.CreateClient().SendAsync(request, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    return Results.Text(body, "application/json", statusCode: (int)response.StatusCode);
}
