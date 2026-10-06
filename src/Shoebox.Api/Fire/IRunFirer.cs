using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.Fire;

/// <summary>
/// Fires one run of a diagram. The seam between the timer and the runner, so the timer can be
/// tested without emitting anything and without waiting for a real walk.
/// </summary>
public interface IRunFirer
{
    Task<RunResult> FireAsync(string diagram, int runIndex, string shoeboxId, CancellationToken cancellationToken);
}

/// <summary>
/// The real thing: parse the diagram as it is now, and walk it once, exactly as <c>POST /run</c>
/// does. Parsing on every run rather than once at start is what makes an edit take effect on the
/// next run.
/// </summary>
public sealed class TopologyRunFirer(TopologyRunner runner) : IRunFirer
{
    public Task<RunResult> FireAsync(string diagram, int runIndex, string shoeboxId, CancellationToken cancellationToken) =>
        // Off the timer's thread. A walk is synchronous and can take a while on a large diagram,
        // and the timer has to stay free to notice that one is still going and skip a tick.
        Task.Run(() => runner.Run(MermaidParser.Parse(diagram), runIndex, shoeboxId), cancellationToken);
}
