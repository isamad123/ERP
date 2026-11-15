using Erp.ArchitectureTests.Rules;

namespace Erp.ArchitectureTests;

/// <summary>Business code reads time only through the injected IClock (CLAUDE.md "Time").</summary>
public sealed class ClockTests
{
    [Fact]
    public void Product_code_never_reads_the_system_clock_outside_an_IClock_implementation()
    {
        var violations = ProductAssemblies.Product
            .SelectMany(p => AssemblyScanner.FindSystemClockCalls(ProductAssemblies.PathOf(p)))
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Rule_detects_system_clock_calls_and_exempts_IClock_implementations()
    {
        var violations = AssemblyScanner.FindSystemClockCalls(typeof(ClockTests).Assembly.Location);

        Assert.Contains(violations, v => v.Contains("ClockViolationSample.ReadsUtcNow calls DateTime.UtcNow", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("calls DateTimeOffset.Now", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("PermittedSampleClock", StringComparison.Ordinal));
    }
}
