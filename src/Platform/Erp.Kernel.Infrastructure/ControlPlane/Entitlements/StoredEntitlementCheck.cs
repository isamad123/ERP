using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.Entitlements;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Entitlements;

/// <summary>One stored entitlement that the running release can no longer read.</summary>
public sealed record UnreadableEntitlement(string TenantCode, int Version, string Status, string Reason);

/// <summary>
/// Stops a release that cannot read the customers' stored entitlements (S1.09b review finding 2, ruling A; CR-057). Requests and
/// the licence job treat claims that no longer pass the key catalogue as no licence (Locked, fail closed), so a catalogue or
/// claims-rule change that breaks a valid signed entitlement would lock its customer at once. <c>migrate</c> runs this first,
/// with the new release's catalogue, and refuses to change anything while a current or scheduled entitlement is unreadable.
/// </summary>
public static class StoredEntitlementCheck
{
    /// <summary>The current and scheduled entitlements of served customers (only <paramref name="onlyTenantCode"/> when given) that do not parse.</summary>
    public static async Task<IReadOnlyList<UnreadableEntitlement>> UnreadableAsync(NpgsqlConnection controlPlane, string? onlyTenantCode, CancellationToken cancellationToken)
    {
        var rows = new List<(string Code, int Version, string Status, string Claims)>();
        await using (var command = Sql.Command(controlPlane, null, """
            select t.code, e.version, e.status, e.claims::text from cp.entitlements e join cp.tenants t on t.id = e.tenant_id
            where e.status <> 'superseded' and t.status in ('active', 'suspended') and ($1::text is null or t.code = $1)
            order by t.code, e.version
            """, [onlyTenantCode]))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        var problems = new List<UnreadableEntitlement>();
        foreach (var (code, version, status, claims) in rows)
        {
            try
            {
                _ = EntitlementCatalogs.Parse(claims);
            }
            catch (InvalidDataException ex)
            {
                problems.Add(new UnreadableEntitlement(code, version, status, ex.Message));
            }
        }

        return problems;
    }

    /// <summary>Throws, naming every customer, version and reason, when any stored entitlement is unreadable.</summary>
    public static async Task EnsureReadableAsync(NpgsqlConnection controlPlane, string? onlyTenantCode, CancellationToken cancellationToken)
    {
        var problems = await UnreadableAsync(controlPlane, onlyTenantCode, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "This release cannot read the stored entitlement of " + problems.Count + " customer licence(s); nothing was migrated, so no customer is locked by it. "
                + "Fix the entitlement key catalogue or claims rules (or issue new entitlements) first: "
                + string.Join("; ", problems.Select(p => $"{p.TenantCode} v{p.Version} ({p.Status}): {p.Reason}")));
        }
    }
}
