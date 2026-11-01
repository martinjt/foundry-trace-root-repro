using OpenTelemetry.Context.Propagation;

namespace FixedReplyAgent;

/// <summary>
/// OpenTelemetry's ASP.NET Core instrumentation does its own, second extraction of the incoming
/// context, from <c>Propagators.DefaultTextMapPropagator</c>, independently of the DI
/// <see cref="System.Diagnostics.DistributedContextPropagator"/> the hosting layer already used to
/// parent the activity (<see cref="ClientHeaderContextPropagator"/>). Confirmed by reading
/// OpenTelemetry.Instrumentation.AspNetCore's <c>HttpInListener.OnStartActivity</c>: when that second
/// extraction's TraceId, ParentSpanId or TraceState differ from the activity's own, it discards the
/// activity and starts a replacement parented to its own extraction -- and exports the replacement, not
/// the one the DI propagator correctly parented. So this applies the exact same x-client-preferring
/// rule, via the shared <see cref="ClientHeaderTraceParent"/> parser, to keep the two extractions in
/// agreement and avoid that replacement.
/// </summary>
internal sealed class ClientHeaderTextMapPropagator(TextMapPropagator inner) : TextMapPropagator
{
    public override ISet<string>? Fields => inner.Fields;

    public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter) =>
        inner.Inject(context, carrier, setter);

    public override PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>?> getter)
    {
        if (!ClientHeaderTraceParent.TryParse(getter(carrier, ClientHeaderNames.TraceParent), getter(carrier, ClientHeaderNames.TraceState), out _, out _, out var clientContext))
        {
            return inner.Extract(context, carrier, getter);
        }

        var hasClientBaggage = !string.IsNullOrEmpty(getter(carrier, ClientHeaderNames.Baggage)?.FirstOrDefault());
        var baggage = hasClientBaggage
            ? inner.Extract(default, carrier, RedirectBaggageFieldToClientBaggage(getter)).Baggage
            : inner.Extract(context, carrier, getter).Baggage;
        return new PropagationContext(clientContext, baggage);
    }

    // Lets inner's own baggage propagator decode x-client-baggage, by making it look like the request's
    // "baggage" field, instead of re-implementing baggage parsing here.
    private static Func<T, string, IEnumerable<string>?> RedirectBaggageFieldToClientBaggage<T>(Func<T, string, IEnumerable<string>?> getter) =>
        (carrier, fieldName) => getter(carrier, fieldName == "baggage" ? ClientHeaderNames.Baggage : fieldName);
}
