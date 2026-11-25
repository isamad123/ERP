using Erp.Hrm.Application.Leave;

namespace Erp.UnitTests.Leave;

/// <summary>The team coverage rules of slice S1.33a (FR-HRM-LVE-05, -15): the share away against the limit, and a manager's team.</summary>
public sealed class LeaveCoverageUnitTests
{
    [Theory]
    [InlineData(3, 10, 30, true)] // exactly at the limit warns, with no rounding
    [InlineData(2, 10, 30, false)]
    [InlineData(1, 3, 30, true)] // 33.3 percent
    [InlineData(0, 10, 30, false)]
    [InlineData(5, 10, 0, false)] // the limit 0 switches the warning off
    [InlineData(1, 0, 30, false)] // an empty team never warns
    [InlineData(10, 10, 100, true)]
    public void FR_HRM_LVE_05_the_warning_is_raised_when_the_share_away_reaches_the_limit(int away, int size, int limit, bool expected) =>
        Assert.Equal(expected, LeaveCoverageRules.Warns(away, size, limit));

    [Fact]
    public void FR_HRM_LVE_15_the_share_is_in_percent_with_one_decimal() =>
        Assert.Equal([33.3m, 50m, 0m], [LeaveCoverageRules.Percent(1, 3), LeaveCoverageRules.Percent(2, 4), LeaveCoverageRules.Percent(1, 0)]);

    [Fact]
    public void FR_HRM_LVE_05_a_managers_team_is_the_direct_and_indirect_reports_and_ends_on_a_cycle()
    {
        Guid m = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), outsider = Guid.NewGuid(), boss = Guid.NewGuid();
        var managerOf = new Dictionary<Guid, Guid?> { [m] = boss, [a] = m, [b] = m, [c] = a, [outsider] = boss, [boss] = null };
        Assert.Equal(new HashSet<Guid> { a, b, c }, LeaveCoverageRules.Subtree(m, managerOf));
        Assert.Empty(LeaveCoverageRules.Subtree(c, managerOf));
        var cycle = new Dictionary<Guid, Guid?> { [a] = b, [b] = a };
        Assert.Equal(new HashSet<Guid> { b }, LeaveCoverageRules.Subtree(a, cycle)); // never loops, never lists the manager
    }
}
