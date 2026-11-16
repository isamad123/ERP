
namespace Erp.BuildingBlocks.Application.Audit;

/// <summary>
/// What the server knows about the current request for the audit log (slice S1.07): the correlation id it created, the
/// client address and the source. Set once per request by the correlation middleware; hand-written services that write
/// SQL directly (identity, access) read it here, because their helpers are static.
/// </summary>
public sealed record AuditRequestInfo(Guid CorrelationId, string? IpAddress, string Source);

/// <summary>The current request's <see cref="AuditRequestInfo"/>, flowing with the async call (null outside a request).</summary>
public static class AuditAmbient
{
    private static readonly AsyncLocal<AuditRequestInfo?> CurrentValue = new();

    public static AuditRequestInfo? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }

    /// <summary>The source when no request is running: the migrator and other operator processes act as the system.</summary>
    public static string Source => Current?.Source ?? AuditSources.System;
}
