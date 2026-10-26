using System.Diagnostics;

namespace FixedReplyAgent;

/// <summary>
/// The propagator ASP.NET Core's hosting layer uses to parent each server span: a valid
/// x-client-traceparent wins, falling back to <paramref name="inner"/> (the platform default)
/// otherwise. Registered as a DI singleton, not assigned to <see cref="DistributedContextPropagator.Current"/>
/// and not installed via <c>Sdk.SetDefaultTextMapPropagator</c>: checking the hosting library's source
/// showed its server-span creation resolves the propagator from DI
/// (<c>services.TryAddSingleton(DistributedContextPropagator.Current)</c>, captured once while
/// <c>AgentHost.CreateBuilder</c> sets up its <c>WebApplication</c> builder, before application code
/// runs), not by re-reading <c>.Current</c> per request.
///
/// On its own this parents the activity correctly but does not join the trace: OpenTelemetry's ASP.NET
/// Core instrumentation performs a second, independent extraction via
/// <c>Propagators.DefaultTextMapPropagator</c> (see <see cref="ClientHeaderTextMapPropagator"/>), and
/// discards this activity in favour of a replacement if that second extraction disagrees with it. Both
/// overrides have to apply the same rule -- via the shared <see cref="ClientHeaderTraceParent"/> parser
/// -- for either to actually take effect.
/// </summary>
internal sealed class ClientHeaderContextPropagator(DistributedContextPropagator inner) : DistributedContextPropagator
{
    public override IReadOnlyCollection<string> Fields => inner.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        inner.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(
        object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        if (getter is not null &&
            ClientHeaderTraceParent.TryParse(TraceParentValues(carrier, getter), TraceStateValue(carrier, getter), out traceId, out traceState, out _))
        {
            return;
        }

        inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier, PropagatorGetterCallback? getter)
    {
        if (getter is null)
        {
            return inner.ExtractBaggage(carrier, getter);
        }

        getter(carrier, ClientHeaderNames.Baggage, out var clientBaggage, out _);
        return string.IsNullOrEmpty(clientBaggage) ? inner.ExtractBaggage(carrier, getter) : ParseBaggage(clientBaggage);
    }

    // ASP.NET Core's getter joins repeated header lines with a comma; traceparent never legitimately
    // contains one, so splitting it back apart recovers whether the caller actually sent it twice.
    private static IEnumerable<string>? TraceParentValues(object? carrier, PropagatorGetterCallback getter)
    {
        getter(carrier, ClientHeaderNames.TraceParent, out var fieldValue, out var fieldValues);
        return fieldValues ?? fieldValue?.Split(',');
    }

    // Tracestate is already exactly what W3C combining wants by the time the getter joins repeated
    // lines with a comma, so it is taken as-is rather than split apart.
    private static IEnumerable<string>? TraceStateValue(object? carrier, PropagatorGetterCallback getter)
    {
        getter(carrier, ClientHeaderNames.TraceState, out var fieldValue, out var fieldValues);
        return fieldValues ?? (fieldValue is null ? null : [fieldValue]);
    }

    // Mirrors the hosting package's own baggage parsing, applied to x-client-baggage instead of baggage.
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
