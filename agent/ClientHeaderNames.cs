namespace FixedReplyAgent;

/// <summary>
/// The caller's own call carries its real W3C context under these names; the Foundry gateway forwards
/// any x-client-* header unchanged, unlike the plain traceparent header, which it rewrites on its hop.
/// Shared by both propagator overrides below, so a typo in one cannot make them disagree.
/// </summary>
internal static class ClientHeaderNames
{
    public const string TraceParent = "x-client-traceparent";
    public const string TraceState = "x-client-tracestate";
    public const string Baggage = "x-client-baggage";
}
