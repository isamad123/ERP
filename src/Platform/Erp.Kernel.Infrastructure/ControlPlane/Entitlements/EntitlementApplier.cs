using System.Globalization;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Domain.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Entitlements;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.ControlPlane.Entitlements;

/// <summary>The outcome of <c>entitlement apply</c>: the version, whether it is in force or waits for its start date, and the modules installed.</summary>
public sealed record EntitlementApplyResult(string Tenant, int Version, string Status, DateOnly EffectiveFrom, IReadOnlyList<string> ModulesInstalled, int? Superseded);

/// <summary>One row of <c>entitlement history</c> (FR-ADM-PLT-03 "history, effective dates").</summary>
public sealed record EntitlementHistoryRow(int Version, string Edition, string Status, DateOnly EffectiveFrom, DateOnly? ExpiresAt, DateTimeOffset StateSince, DateTimeOffset CreatedAt, string AppliedBy, IReadOnlyList<string> Modules);

/// <summary>
/// Applies a newly signed entitlement to an existing customer (FR-ADM-PLT-03, the "apply entitlements" part of
/// FR-KRN-TEN-02; slice S1.09 ruling Q3), without downtime: API hosts read the control plane again within their host-cache
/// time. Checks, in order: the signing key and signature, the customer code, a version higher than every stored one, the
/// claims, and an end date not already passed (ruling B4). Newly licensed modules get their schema, permissions, numbering
/// and settings first; if that fails nothing is stored and running the command again resumes. A start date of today or
/// earlier (the customer's day) puts the entitlement in force at once and supersedes the earlier ones; a future start
/// date stores it as scheduled, and the licence loop or the next request puts it in force on that day. A module no longer
/// licensed keeps its schema and data. The person who signs (off-line private key) is not the operator who applies (Q3).
/// </summary>
public sealed class EntitlementApplier(ControlPlaneOptions options, ISecretStore secrets, IClock clock, MigrationSet migrations)
{
    private readonly TenantProvisioner _provisioner = new(options, secrets, clock, migrations);

    public async Task<EntitlementApplyResult> ApplyAsync(PlatformActor actor, string tenantCode, string signedJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var entitlement = SignedEntitlement.Parse(signedJson);
        var payload = entitlement.Payload;
        var now = clock.UtcNow;
        await using var cp = new NpgsqlConnection(options.ConnectionString);
        await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
        var tenant = await TenantDirectory.FindByCodeAsync(cp, tenantCode, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {tenantCode} is not registered.");
        if (tenant.Status is not ("active" or "suspended"))
        {
            throw new InvalidOperationException($"Customer {tenantCode} has status {tenant.Status}; entitlements are applied to active or suspended customers only.");
        }

        if (!string.Equals(payload.Tenant, tenant.Code, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The entitlement is issued to '{payload.Tenant}', not to '{tenant.Code}'.");
        }

        var claims = await EntitlementVerifier.VerifyAsync(cp, null, entitlement, now, cancellationToken).ConfigureAwait(false);
        var highest = await Sql.ScalarAsync<int?>(cp, null, "select max(version) from cp.entitlements where tenant_id = $1", cancellationToken, tenant.TenantId).ConfigureAwait(false) ?? 0;
        if (payload.Version <= highest)
        {
            throw new InvalidOperationException($"The entitlement version {payload.Version} is not higher than the last one applied ({highest}).");
        }

        string timeZone;
        await using (var owner = await _provisioner.OpenTenantAsync(tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false))
        {
            timeZone = await CustomerLicence.TimeZoneAsync(owner, null, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken).ConfigureAwait(false);
        }

        var today = LicenceStates.Today(now, LicenceStates.Zone(timeZone));
        RefuseExpired(payload, today);

        // Newly licensed modules first (ruling Q3): schema, then the access, numbering and settings registries.
        var newModules = claims.Modules.Where(m => !tenant.InstalledModules.Contains(m)).ToList();
        if (newModules.Count > 0)
        {
            var modules = tenant.InstalledModules.Concat(claims.Modules).Distinct(StringComparer.Ordinal).ToList();
            SchemaMigrationResult result;
            await using (var owner = await _provisioner.OpenTenantAsync(tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false))
            {
                result = await TenantSchemaMigrator.ApplyAsync(owner, migrations.TenantScripts(modules), clock, BaselineSeed.SystemUserId, cancellationToken).ConfigureAwait(false);
                await Identity.IdentitySetup.EnsureAsync(owner, secrets, tenant.Code, migrations.AccessFolders(modules), cancellationToken).ConfigureAwait(false);
                await Access.AccessSetup.EnsureAsync(owner, null, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);
                await Numbering.NumberingSetup.EnsureAsync(owner, null, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);
                await Workflow.WorkflowSetup.EnsureAsync(owner, null, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);
                await Jobs.JobSetup.EnsureAsync(owner, null, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);
                await Notifications.NotificationSetup.EnsureAsync(owner, null, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);
                await Itg.ImportSetup.EnsureAsync(owner, null, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);
                await Setup.SetupTaskSetup.EnsureAsync(owner, null, migrations.AccessFolders(modules), now, cancellationToken).ConfigureAwait(false);
            }

            await _provisioner.RecordVersionsAsync(actor, tenant, result, cancellationToken).ConfigureAwait(false);
        }

        var scheduled = payload.EffectiveFrom > today;
        var state = LicenceStates.Decide(payload.ExpiresAt, claims.Term, today);
        int? superseded;
        await using (var transaction = await cp.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            // One apply at a time per customer: the rows are locked and the version checked again.
            await Sql.ExecuteAsync(cp, transaction, "select id from cp.entitlements where tenant_id = $1 for update", cancellationToken, tenant.TenantId).ConfigureAwait(false);
            if ((await Sql.ScalarAsync<int?>(cp, transaction, "select max(version) from cp.entitlements where tenant_id = $1", cancellationToken, tenant.TenantId).ConfigureAwait(false) ?? 0) >= payload.Version)
            {
                throw new InvalidOperationException("Another entitlement with this or a higher version was applied meanwhile.");
            }

            superseded = null;
            if (!scheduled)
            {
                superseded = await Sql.ScalarAsync<int?>(cp, transaction,
                    "select max(version) from cp.entitlements where tenant_id = $1 and status not in ('superseded', 'scheduled')", cancellationToken, tenant.TenantId).ConfigureAwait(false);
                await Sql.ExecuteAsync(cp, transaction, """
                    update cp.entitlements set status = 'superseded', state_since = $2, updated_at = $2, updated_by = $3, row_version = row_version + 1
                    where tenant_id = $1 and status <> 'superseded' and version < $4
                    """, cancellationToken, tenant.TenantId, now, actor.Id, payload.Version).ConfigureAwait(false);
            }

            await InsertAsync(cp, transaction, tenant.TenantId, entitlement, scheduled ? EntitlementRecord.Scheduled : state, now, actor.Id, cancellationToken).ConfigureAwait(false);
            await LicenceEventsTable.WriteAsync(cp, transaction, tenant.TenantId, scheduled ? "entitlement.scheduled" : "entitlement.applied", null, scheduled ? null : state, now, actor.Id,
                new JsonObject
                {
                    ["version"] = payload.Version,
                    ["edition"] = payload.Edition,
                    ["effective_from"] = Day(payload.EffectiveFrom),
                    ["expires_at"] = payload.ExpiresAt is { } e ? Day(e) : null,
                    ["superseded_version"] = superseded,
                    ["modules_installed"] = new JsonArray([.. newModules.Select(m => (JsonNode)m)]),
                    ["applied_by"] = actor.Email,
                }, null, cancellationToken).ConfigureAwait(false);
            await PlatformAudit.WriteAsync(cp, transaction, clock, actor, scheduled ? "entitlement.schedule" : "entitlement.apply", tenant.TenantId,
                new { code = tenant.Code, version = payload.Version, effective_from = Day(payload.EffectiveFrom), modules_installed = newModules }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // The customer's copy and security event (KRN-AUD-02). Should this fail, the licence loop brings the copy in step.
        await using (var owner = await _provisioner.OpenTenantAsync(tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false))
        await using (var tx = await owner.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            // One writer of the customer's licence copy at a time (S1.09b review finding 3): if the licence job already put this
            // version in the copy (it read the control plane after the commit above), it has recorded it; nothing is recorded twice.
            await CustomerLicence.LockCopyAsync(owner, tx, cancellationToken).ConfigureAwait(false);
            var recordedByJob = !scheduled && await CustomerLicence.CopyVersionAsync(owner, tx, cancellationToken).ConfigureAwait(false) >= payload.Version;
            if (!scheduled && !recordedByJob)
            {
                await CustomerLicence.ReplaceCopyAsync(owner, tx, payload, entitlement.KeyId, entitlement.Signature, state, now, cancellationToken).ConfigureAwait(false);
            }

            if (!recordedByJob)
            {
                await CustomerLicence.RecordAsync(owner, tx, scheduled ? LicenceEvents.Scheduled : LicenceEvents.Applied, Erp.Kernel.Infrastructure.Audit.Severities.Info, new JsonObject
                {
                    ["version"] = payload.Version,
                    ["edition"] = payload.Edition,
                    ["effective_from"] = Day(payload.EffectiveFrom),
                    ["expires_at"] = payload.ExpiresAt is { } e ? Day(e) : null,
                    ["state"] = scheduled ? null : state,
                    ["modules"] = new JsonArray([.. claims.Modules.Select(m => (JsonNode)m)]),
                    ["applied_by"] = actor.Email,
                }, BaselineSeed.SystemUserId, now, cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new EntitlementApplyResult(tenant.Code, payload.Version, scheduled ? EntitlementRecord.Scheduled : state, payload.EffectiveFrom, newModules, superseded);
    }

    /// <summary>Ruling B4: an entitlement whose end date has already passed (the customer's day) is refused.</summary>
    public static void RefuseExpired(EntitlementPayload payload, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.ExpiresAt is { } end && end < today)
        {
            throw new InvalidOperationException($"This entitlement has already expired (end date {Day(end)}).");
        }
    }

    /// <summary>Stores a verified entitlement row (provisioning and apply).</summary>
    internal static async Task InsertAsync(NpgsqlConnection cp, NpgsqlTransaction? transaction, Guid tenantId, SignedEntitlement entitlement, string status, DateTimeOffset now, Guid actor, CancellationToken cancellationToken)
    {
        var payload = entitlement.Payload;
        await using var command = Sql.Command(cp, transaction, """
            insert into cp.entitlements (id, tenant_id, version, edition, claims, signature, signing_key_id, effective_from, expires_at, state_since, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $10, $12, $10, $12)
            on conflict (tenant_id, version) do nothing
            """, [Guid.CreateVersion7(now), tenantId, payload.Version, payload.Edition, null, entitlement.Signature, entitlement.KeyId, payload.EffectiveFrom, payload.ExpiresAt, now, status, actor]);
        command.Parameters[4] = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = payload.Claims.ToJsonString() };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An unsigned payload for the next version, pre-filled from an edition template (ruling Q2): every value is written out
    /// in full, so changing a template later never changes what a customer already has. The issuer reviews and signs it.
    /// </summary>
    public async Task<JsonObject> DraftAsync(string tenantCode, JsonObject template, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        await using var cp = new NpgsqlConnection(options.ConnectionString);
        await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
        var tenantId = await Sql.ScalarAsync<Guid?>(cp, null, "select id from cp.tenants where code = $1", cancellationToken, tenantCode).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {tenantCode} is not registered.");
        var highest = await Sql.ScalarAsync<int?>(cp, null, "select max(version) from cp.entitlements where tenant_id = $1", cancellationToken, tenantId).ConfigureAwait(false) ?? 0;
        var claims = template["claims"]?.DeepClone() as JsonObject ?? throw new InvalidDataException("The template has no claims.");
        var edition = template["edition"]?.GetValue<string>() ?? throw new InvalidDataException("The template has no edition.");
        var start = from ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var payload = EntitlementPayload.FromJson(new JsonObject
        {
            ["tenant"] = tenantCode,
            ["version"] = highest + 1,
            ["edition"] = edition,
            ["effective_from"] = Day(start),
            ["expires_at"] = to is { } end ? Day(end) : null,
            ["claims"] = claims,
        });
        _ = EntitlementCatalogs.Parse(payload.Claims); // a template that breaks the key catalogue is refused here, not at apply
        return payload.ToJson();
    }

    /// <summary>Every stored entitlement of a customer, newest first, with who applied it (platform audit).</summary>
    public async Task<IReadOnlyList<EntitlementHistoryRow>> HistoryAsync(string tenantCode, CancellationToken cancellationToken)
    {
        await using var cp = new NpgsqlConnection(options.ConnectionString);
        await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Sql.Command(cp, null, """
            select e.version, e.edition, e.status, e.effective_from, e.expires_at, e.state_since, e.created_at, coalesce(u.email, '-'),
                   coalesce((select array_agg(m order by m) from jsonb_array_elements_text(e.claims -> 'modules') m), '{}')
            from cp.entitlements e join cp.tenants t on t.id = e.tenant_id left join cp.platform_users u on u.id = e.created_by
            where t.code = $1 order by e.version desc
            """, [tenantCode]);
        var rows = new List<EntitlementHistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new EntitlementHistoryRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateOnly>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetString(7), reader.GetFieldValue<string[]>(8)));
        }

        return rows;
    }

    internal static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Writes cp.license_events (append-only): state changes, applied entitlements and copied limit events.</summary>
internal static class LicenceEventsTable
{
    /// <summary>Returns false when the row was a copy already made (<paramref name="sourceEventId"/> seen before, CR-039).</summary>
    public static async Task<bool> WriteAsync(NpgsqlConnection cp, NpgsqlTransaction? transaction, Guid tenantId, string eventType, string? fromState, string? toState, DateTimeOffset occurredAt, Guid actor, JsonObject details, Guid? sourceEventId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(cp, transaction, """
            insert into cp.license_events (id, tenant_id, event_type, from_state, to_state, occurred_at, details, source_event_id, created_at, created_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            on conflict (source_event_id) where source_event_id is not null do nothing
            """, [Guid.CreateVersion7(occurredAt), tenantId, eventType, fromState, toState, occurredAt, null, sourceEventId, occurredAt, actor]);
        command.Parameters[6] = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = details.ToJsonString() };
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
