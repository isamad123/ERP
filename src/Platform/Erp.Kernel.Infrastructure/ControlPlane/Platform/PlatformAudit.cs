using System.Text.Json;
using Erp.BuildingBlocks.Domain.Time;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.ControlPlane.Platform;

/// <summary>The vendor staff member an operator action is attributed to (cp.platform_users, FR-ADM-PLT-10).</summary>
public sealed record PlatformActor(Guid Id, string Email, string Role);

/// <summary>Writes cp.platform_audit, the append-only record of every vendor-side action (FR-ADM-PLT-10, FR-ADM-PLT-11).</summary>
public static class PlatformAudit
{
    public static async Task WriteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IClock clock, PlatformActor actor, string action, Guid? tenantId, object details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(actor);
        var now = clock.UtcNow;
        await using var command = Sql.Command(connection, transaction,
            "insert into cp.platform_audit (id, platform_user_id, action, tenant_id, occurred_at, details, created_at, created_by) values ($1, $2, $3, $4, $5, $6, $5, $2)",
            [Guid.CreateVersion7(now), actor.Id, action, tenantId, now, null]);
        command.Parameters[5] = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(details) };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
