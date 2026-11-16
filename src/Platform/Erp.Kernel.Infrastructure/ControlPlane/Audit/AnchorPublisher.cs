using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Audit;

/// <summary>What one customer's anchor copy run did (ruling P5).</summary>
public sealed record AnchorPublishResult(string TenantCode, int Copied, int AlreadyCopied, IReadOnlyList<string> Mismatches);

/// <summary>
/// Copies the daily audit-chain anchors of finished days (before today, UTC) of every customer into
/// <c>cp.audit_anchors</c> (CR-027, ruling P5), so a customer-side database owner cannot rewrite the chain unnoticed. A copy
/// is written once and never changed; an anchor that differs from its earlier copy is a critical finding, recorded as a
/// security event in the customer's database and in the platform audit. Run by the migrator command
/// <c>audit anchors-publish</c>; scheduling it daily comes with jobs (S1.11).
/// </summary>
public sealed class AnchorPublisher(ControlPlaneOptions options, ISecretStore secrets, IClock clock, MigrationSet migrations)
{
    public const string MismatchEvent = "aud.anchor_copy_mismatch";

    public async Task<IReadOnlyList<AnchorPublishResult>> PublishAsync(PlatformActor actor, string? tenantCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var codes = tenantCode is null ? await CodesAsync(cancellationToken).ConfigureAwait(false) : [tenantCode];
        var results = new List<AnchorPublishResult>();
        foreach (var code in codes)
        {
            results.Add(await PublishOneAsync(actor, code, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<List<string>> CodesAsync(CancellationToken cancellationToken)
    {
        await using var cp = new NpgsqlConnection(options.ConnectionString);
        await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            select t.code from cp.tenants t
            where t.status in ('active', 'suspended') and exists (select 1 from cp.tenant_databases d where d.tenant_id = t.id and d.status = 'active')
            order by t.code
            """, cp);
        var codes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    private async Task<AnchorPublishResult> PublishOneAsync(PlatformActor actor, string code, CancellationToken cancellationToken)
    {
        var host = await TenantHosts.PrimaryAsync(options, code, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {code} is not registered.");
        var tenant = await new TenantDirectory(options, clock).FindByHostAsync(host, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {code} has no database yet.");
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var db = await new TenantProvisioner(options, secrets, clock, migrations).OpenTenantAsync(tenant, TenantLogins.ReadWrite, cancellationToken).ConfigureAwait(false);
        var anchors = new List<(DateOnly Day, byte[] Hash)>();
        await using (var command = new NpgsqlCommand("select day, last_hash from aud.chain_anchors where day < $1 order by day", db))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = today });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                anchors.Add((reader.GetFieldValue<DateOnly>(0), (byte[])reader.GetValue(1)));
            }
        }

        int copied = 0, already = 0;
        var mismatches = new List<string>();
        await using var cp = new NpgsqlConnection(options.ConnectionString);
        await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (day, hash) in anchors)
        {
            await using var insert = new NpgsqlCommand("""
                insert into cp.audit_anchors (id, tenant_id, day, last_hash, recorded_at, created_at, created_by)
                values ($1, $2, $3, $4, $5, $5, $6) on conflict (tenant_id, day) do nothing
                """, cp);
            foreach (var value in new object[] { Guid.CreateVersion7(now), tenant.TenantId, day, hash, now, actor.Id })
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = value });
            }

            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                copied++;
                continue;
            }

            already++;
            await using var existing = new NpgsqlCommand("select last_hash from cp.audit_anchors where tenant_id = $1 and day = $2", cp);
            existing.Parameters.Add(new NpgsqlParameter { Value = tenant.TenantId });
            existing.Parameters.Add(new NpgsqlParameter { Value = day });
            var copy = (byte[])(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (!copy.AsSpan().SequenceEqual(hash))
            {
                mismatches.Add(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (mismatches.Count > 0)
        {
            var details = new JsonObject { ["days"] = new JsonArray([.. mismatches.Select(d => (JsonNode)d)]), ["platform_user"] = actor.Email };
            await AuditDb.AppendSecurityEventAsync(db, null, new SecurityEventRecord(now, MismatchEvent, "platform:" + actor.Id, "aud.chain_anchors", Severities.Critical, details, BaselineSeed.SystemUserId), cancellationToken).ConfigureAwait(false);
        }

        await PlatformAudit.WriteAsync(cp, null, clock, actor, mismatches.Count > 0 ? "audit.anchor_mismatch" : "audit.anchors_publish", tenant.TenantId,
            new { code, copied, already_copied = already, mismatches }, cancellationToken).ConfigureAwait(false);
        return new AnchorPublishResult(code, copied, already, mismatches);
    }
}

/// <summary>Reads the control-plane copies of a customer's audit-chain anchors (CR-027) for the verifier.</summary>
public static class AuditAnchorCopies
{
    /// <summary>The control-plane copies of one customer's anchors for a range of days (CR-027).</summary>
    public static async Task<IReadOnlyDictionary<DateOnly, byte[]>> ReadAsync(ControlPlaneOptions options, string tenantCode, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select a.day, a.last_hash from cp.audit_anchors a join cp.tenants t on t.id = a.tenant_id where t.code = $1 and a.day between $2 and $3", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = tenantCode });
        command.Parameters.Add(new NpgsqlParameter { Value = firstDay });
        command.Parameters.Add(new NpgsqlParameter { Value = lastDay });
        var copies = new Dictionary<DateOnly, byte[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            copies[reader.GetFieldValue<DateOnly>(0)] = (byte[])reader.GetValue(1);
        }

        return copies;
    }
}
