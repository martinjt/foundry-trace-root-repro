using System.Diagnostics;

namespace FixedReplyAgent;

/// <summary>
/// Prefers <c>x-client-traceparent</c> / <c>x-client-tracestate</c> / <c>x-client-baggage</c> over the
/// plain W3C headers when extracting the incoming request's trace context, falling back to
/// <paramref name="inner"/> (the platform default) when they are absent or invalid. This is the
/// extension point AgentHost's own telemetry setup uses for ASP.NET Core's server-span creation:
/// <c>Azure.AI.AgentServer.Core.Internal.W3CBaggagePropagator</c> (shipped with the hosting package)
/// reads <c>Activity.Current</c> set up from exactly this propagator, not from
/// <c>Sdk.SetDefaultTextMapPropagator</c>, which the agent never touches. Only installed when
/// <c>CLIENT_HEADER_PROPAGATION</c> is on.
/// </summary>
internal sealed class ClientHeaderContextPropagator(DistributedContextPropagator inner) : DistributedContextPropagator
{
    public override IReadOnlyCollection<string> Fields => inner.Fields;

    public override void Inject(Activity? activity, object? carrier, DistributedContextPropagator.PropagatorSetterCallback? setter) =>
        inner.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(
        object? carrier, DistributedContextPropagator.PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        if (getter is null)
        {
            inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);
            return;
        }

        getter(carrier, "x-client-traceparent", out var clientTraceParent, out _);
        if (!string.IsNullOrEmpty(clientTraceParent) && ActivityContext.TryParse(clientTraceParent, null, isRemote: true, out _))
        {
            traceId = clientTraceParent;
            getter(carrier, "x-client-tracestate", out traceState, out _);
            return;
        }

        inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier, DistributedContextPropagator.PropagatorGetterCallback? getter)
    {
        if (getter is null)
        {
            return inner.ExtractBaggage(carrier, getter);
        }

        getter(carrier, "x-client-baggage", out var clientBaggage, out _);
        return string.IsNullOrEmpty(clientBaggage) ? inner.ExtractBaggage(carrier, getter) : ParseBaggage(clientBaggage);
    }

    // Mirrors the hosting package's own W3CBaggagePropagator parsing, applied to x-client-baggage instead of baggage.
    private static IEnumerable<KeyValuePair<string, string?>> ParseBaggage(string header)
    {
        foreach (var pair in header.Split(','))
        {
            var trimmed = pair.Trim();
            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..];
            var propertySeparator = value.IndexOf(';');
            if (propertySeparator >= 0)
            {
                value = value[..propertySeparator];
            }

            if (key.Length > 0)
            {
                yield return new KeyValuePair<string, string?>(key, Uri.UnescapeDataString(value.Trim()));
            }
        }
    }
}
