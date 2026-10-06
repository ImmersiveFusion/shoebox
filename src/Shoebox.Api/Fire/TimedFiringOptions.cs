namespace Shoebox.Api.Fire;

/// <summary>
/// The limits on timed firing, read from the <c>TimedFiring</c> configuration section.
/// </summary>
/// <remarks>
/// Everything here is an operator decision except the ceiling on duration, which is not
/// configurable at all (<see cref="TimedFiringLimits.MaxDuration"/>). A deployment can make
/// timed firing smaller or switch it off; it cannot make it open-ended.
/// </remarks>
public sealed class TimedFiringOptions
{
    public const string SectionName = "TimedFiring";

    /// <summary>
    /// How many shoeboxes may be firing on a timer at once, across the whole process. Zero
    /// switches timed firing off. Kept small on purpose: every active timer is a steady stream
    /// of spans into whatever backend this instance exports to.
    /// </summary>
    public int MaxActive { get; set; } = 4;

    /// <summary>
    /// How many of those one source address may hold. Without this, one visitor minting
    /// shoeboxes could take every slot.
    /// </summary>
    public int MaxPerSource { get; set; } = 1;

    /// <summary>The gap between runs when the caller does not ask for one.</summary>
    public TimeSpan DefaultInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The shortest gap between runs a caller may ask for.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest gap between runs a caller may ask for.</summary>
    public TimeSpan MaxInterval { get; set; } = TimeSpan.FromSeconds(60);

    internal void Validate()
    {
        if (MaxActive < 0) throw new InvalidOperationException("TimedFiring:MaxActive cannot be negative.");
        if (MaxPerSource < 1) throw new InvalidOperationException("TimedFiring:MaxPerSource must be at least 1.");
        if (MinInterval <= TimeSpan.Zero) throw new InvalidOperationException("TimedFiring:MinInterval must be positive.");
        if (MaxInterval < MinInterval) throw new InvalidOperationException("TimedFiring:MaxInterval is below MinInterval.");
        if (DefaultInterval < MinInterval || DefaultInterval > MaxInterval)
        {
            throw new InvalidOperationException("TimedFiring:DefaultInterval must lie between MinInterval and MaxInterval.");
        }
    }
}

public static class TimedFiringLimits
{
    /// <summary>
    /// The hard ceiling on how long a timer may have left to run, at start and after any
    /// extension. Not configuration: there is no setting that makes timed firing open-ended.
    /// </summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(2);
}
