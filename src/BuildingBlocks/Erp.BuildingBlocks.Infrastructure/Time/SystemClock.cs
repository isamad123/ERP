using Erp.BuildingBlocks.Domain.Time;

namespace Erp.BuildingBlocks.Infrastructure.Time;

/// <summary>Production clock. The single permitted reader of the system clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
