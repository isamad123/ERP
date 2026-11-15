namespace Erp.BuildingBlocks.Domain.Time;

/// <summary>
/// The only source of the current time for business code. Inject it; never read the system clock directly.
/// Architecture tests fail the build on any direct call to <c>DateTime.Now</c>, <c>DateTime.UtcNow</c>,
/// <c>DateTime.Today</c>, <c>DateTimeOffset.Now</c> or <c>DateTimeOffset.UtcNow</c> outside an implementation of this interface.
/// </summary>
public interface IClock
{
    /// <summary>The current instant in UTC (offset zero).</summary>
    DateTimeOffset UtcNow { get; }
}
