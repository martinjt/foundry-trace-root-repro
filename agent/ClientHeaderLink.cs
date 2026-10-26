using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace FixedReplyAgent;

/// <summary>
/// Whenever <c>x-client-traceparent</c> differs from the plain <c>traceparent</c> header, adds a span
/// link to what the platform actually sent, so it stays visible in the trace regardless of which one
/// this agent's own extraction ends up using. An <see cref="ActivityListener"/> is used rather than an
/// OpenTelemetry span processor: it fires synchronously inside <c>ActivitySource.StartActivity</c>,
/// before the span has any chance to be exported, and needs no extra configuration on the
/// TracerProviderBuilder. Only registered when <c>CLIENT_HEADER_PROPAGATION</c> is on.
/// </summary>
internal static class ClientHeaderLink
{
    // Confirmed by running the agent locally with a catch-all ActivityListener: the server span later
    // exported as "POST /responses" is ASP.NET Core's own request activity, from the built-in
    // "Microsoft.AspNetCore" source (there is no explicit AddAspNetCoreInstrumentation() call in the
    // hosting package; its telemetry setup still ends up listening to this source for the distro's
    // automatic HTTP instrumentation). It is not a custom Azure.AI.AgentServer.* activity source.
    private const string AspNetCoreSourceName = "Microsoft.AspNetCore";

    public static void Register(IHttpContextAccessor httpContextAccessor) =>
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == AspNetCoreSourceName,
            // PropagationData, not AllDataAndRecorded: this listener only needs ActivityStarted to add a
            // link, and must not force every Microsoft.AspNetCore activity to be recorded -- that
            // decision belongs to the distro's own sampler, via its own ActivityListener on this source.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
            ActivityStarted = activity => AddPlatformLinkIfDifferent(activity, httpContextAccessor.HttpContext),
        });

    private static void AddPlatformLinkIfDifferent(Activity activity, HttpContext? httpContext)
    {
        var headers = httpContext?.Request.Headers;
        if (headers is null)
        {
            return;
        }

        // Evidence that the platform forwards x-client-* headers unchanged (names only, never values):
        // inspect this tag in the dashboard to confirm what arrived, without needing container logs.
        var clientHeaderNames = string.Join(',', headers.Keys.Where(k => k.StartsWith("x-client-", StringComparison.OrdinalIgnoreCase)));
        activity.SetTag("repro.x_client_headers_received", clientHeaderNames.Length == 0 ? "(none)" : clientHeaderNames);

        var clientTraceParent = headers.TryGetValue("x-client-traceparent", out var client) ? client.ToString() : null;
        var platformTraceParent = headers.TryGetValue("traceparent", out var platform) ? platform.ToString() : null;
        if (string.IsNullOrEmpty(clientTraceParent) || string.IsNullOrEmpty(platformTraceParent) || clientTraceParent == platformTraceParent)
        {
            return;
        }

        if (ActivityContext.TryParse(platformTraceParent, null, isRemote: true, out var platformContext))
        {
            activity.AddLink(new ActivityLink(platformContext, new ActivityTagsCollection
            {
                ["description"] = "What the platform sent in traceparent, which differs from x-client-traceparent",
            }));
        }
    }
}
