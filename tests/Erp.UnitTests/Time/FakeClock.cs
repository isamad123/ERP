using Erp.BuildingBlocks.Domain.Time;

namespace Erp.UnitTests.Time;

/// <summary>Test clock with a fixed, settable instant. Business tests use this instead of the system clock.</summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start.ToUniversalTime();

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
