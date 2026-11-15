using Erp.BuildingBlocks.Domain.Time;

// Deliberate violations. The rule tests scan THIS test assembly and must find exactly these,
// which proves each rule can fail. Product assemblies are scanned separately and must be clean.
namespace Erp.ArchitectureTests.Samples
{
    public static class ClockViolationSample
    {
        public static DateTime ReadsUtcNow() => DateTime.UtcNow;

        public static Func<DateTimeOffset> ReadsNowInLambda() => () => DateTimeOffset.Now;
    }

    /// <summary>An IClock implementation may read the system clock.</summary>
    public sealed class PermittedSampleClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public sealed class TenantIdViolationSample
    {
        public Guid TenantId { get; init; }
    }
}

namespace Erp.ArchitectureTests.Samples.ControlPlane
{
    /// <summary>Control-plane types (schema cp) legitimately reference tenants.</summary>
    public sealed class PermittedTenantRegistrySample
    {
        public Guid TenantId { get; init; }
    }
}
