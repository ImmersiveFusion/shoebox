using OpenTelemetry.Trace;

namespace Shoebox.Api.Emit;

/// <summary>
/// Records and samples every span, as the default sampler already did, and says so
/// on the wire.
///
/// Without a declaration a backend cannot tell a complete trace stream from a
/// sampled one, so a span that is missing on purpose (a consumer that never ran)
/// reads the same as a span that was sampled away. Writing <c>ot=th:0</c> into the
/// tracestate states that the threshold is zero: nothing was dropped, and anything
/// absent is genuinely absent.
/// </summary>
public sealed class DeclaredFullSampler : Sampler
{
    public DeclaredFullSampler()
    {
        Description = nameof(DeclaredFullSampler);
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var traceState = TraceStateOt.WithFullThreshold(samplingParameters.ParentContext.TraceState);
        return new SamplingResult(SamplingDecision.RecordAndSample, traceState);
    }
}
