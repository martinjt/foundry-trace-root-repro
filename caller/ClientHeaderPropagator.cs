using System.Diagnostics;
using OpenTelemetry.Context.Propagation;

namespace Caller;

/// <summary>
/// Wraps the default W3C propagator and additionally writes the outgoing context under
/// <c>x-client-traceparent</c>, <c>x-client-tracestate</c> and <c>x-client-baggage</c>. The Foundry
/// hosted-agent runtime contract forwards headers prefixed <c>x-client-</c> unchanged to the
/// container, so the agent can read the caller's real parent even though the platform rewrites the
/// plain <c>traceparent</c> header's span id on its own hop (see the README's "Actual" section).
/// Only used when <c>CLIENT_HEADER_PROPAGATION</c> is on.
/// </summary>
internal sealed class ClientHeaderPropagator(TextMapPropagator inner) : TextMapPropagator
{
    public override ISet<string>? Fields => inner.Fields;

    public override PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>?> getter) =>
        inner.Extract(context, carrier, getter);

    public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter)
    {
        inner.Inject(context, carrier, setter);

        var activityContext = context.ActivityContext;
        if (activityContext.TraceId == default)
        {
            return;
        }

        setter(carrier, "x-client-traceparent",
            $"00-{activityContext.TraceId.ToHexString()}-{activityContext.SpanId.ToHexString()}-{(byte)activityContext.TraceFlags:x2}");

        if (!string.IsNullOrEmpty(activityContext.TraceState))
        {
            setter(carrier, "x-client-tracestate", activityContext.TraceState);
        }

        var baggage = context.Baggage.GetBaggage();
        if (baggage.Count > 0)
        {
            var encoded = string.Join(',', baggage.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            setter(carrier, "x-client-baggage", encoded);
        }
    }
}
