using Erp.BuildingBlocks.Infrastructure.Time;

namespace Erp.UnitTests.Time;

public sealed class ClockTests
{
    [Fact]
    public void SystemClock_returns_utc()
    {
        var clock = new SystemClock();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }

    [Fact]
    public void FakeClock_is_fixed_until_advanced_and_normalises_to_utc()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.FromHours(5)));

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 5, 0, 0, TimeSpan.Zero), clock.UtcNow);
        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);

        clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 5, 30, 0, TimeSpan.Zero), clock.UtcNow);
    }
}
