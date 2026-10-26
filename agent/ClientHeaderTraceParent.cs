using System.Diagnostics;

namespace FixedReplyAgent;

/// <summary>
/// One shared rule for both <see cref="ClientHeaderContextPropagator"/> and
/// <see cref="ClientHeaderTextMapPropagator"/>, so the context each extracts for the same request
/// agrees. If they disagreed, OpenTelemetry's ASP.NET Core instrumentation would see its own extraction
/// (via <see cref="ClientHeaderTextMapPropagator"/>) differ from the parent the hosting layer already
/// gave the activity (via <see cref="ClientHeaderContextPropagator"/>) and would discard that activity,
/// starting a replacement server span parented to its own extraction instead -- see the README's
/// "Workaround" section for why that is what actually broke the join the first time around.
/// </summary>
internal static class ClientHeaderTraceParent
{
    public static bool TryParse(
        IEnumerable<string>? traceParentValues,
        IEnumerable<string>? traceStateValues,
        out string? traceParent,
        out string? traceState,
        out ActivityContext context)
    {
        var parent = SingleValueOrNull(traceParentValues);
        var state = JoinedOrNull(traceStateValues);
        if (parent is not null && ActivityContext.TryParse(parent, state, isRemote: true, out context))
        {
            traceParent = parent;
            traceState = state;
            return true;
        }

        traceParent = null;
        traceState = null;
        context = default;
        return false;
    }

    // A real x-client-traceparent is sent once; if a getter instead reports it as several values (the
    // DistributedContextPropagator getter joins repeated header lines with a comma, so more than one
    // value here means the caller genuinely sent the header twice), that is rejected rather than guessed at.
    private static string? SingleValueOrNull(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        using var value = values.GetEnumerator();
        if (!value.MoveNext())
        {
            return null;
        }

        var only = value.Current;
        return value.MoveNext() ? null : only;
    }

    // W3C tracestate is itself a comma-separated list, and multiple header lines combine by joining
    // with a comma, so values here are only ever joined, never rejected for being more than one.
    private static string? JoinedOrNull(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var joined = string.Join(",", values);
        return joined.Length == 0 ? null : joined;
    }
}
