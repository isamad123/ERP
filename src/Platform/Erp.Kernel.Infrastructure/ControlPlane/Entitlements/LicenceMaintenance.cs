using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Entitlements;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Entitlements;

/// <summary>What one licence refresh did for one customer.</summary>
public sealed record LicenceRefreshOutcome(string Code, string State, string? PreviousState, bool Changed, int? Activated, int Readings, int LimitEventsCopied, string? Error);

/// <summary>
/// The licence loop (FR-KRN-ENT-04, FR-KRN-ENT-06; slice S1.09 ruling Q6): run hourly by the worker host and on demand by
/// <c>licence refresh</c>. For each customer it records what the dates have already decided, once:
/// <list type="number">
/// <item>a scheduled entitlement whose start day has come is put in force (the earlier ones are superseded);</item>
/// <item>a state change is recorded in cp.entitlements and cp.license_events, and in the customer database as the copy's
/// state, a security event and the outbox event <c>kernel.licence.state_changed</c> (for the e-mails of S1.11);</item>
/// <item>the day's usage readings go to cp.usage_meters (ruling Q8);</item>
/// <item>limit warnings and refusals recorded by the customer database are copied to cp.license_events, each once (CR-039).</item>
/// </list>
/// Each side compares with its own recorded state, so running again, or after a failure half way, records nothing twice.
/// Requests never wait for this loop: they work the state out from the dates themselves.
/// </summary>
public sealed class LicenceMaintenance(ControlPlaneOptions options, ISecretStore secrets, IClock clock, IActiveEmployeeCounter? employees = null)
{
    public async Task<IReadOnlyList<LicenceRefreshOutcome>> RunAsync(string? onlyTenantCode, CancellationToken cancellationToken)
    {
        var codes = new List<string>();
        await using (var cp = new NpgsqlConnection(options.ConnectionString))
        {
            await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Sql.Command(cp, null, """
                select t.code from cp.tenants t join cp.tenant_databases d on d.tenant_id = t.id and d.env_type = 'production'
                where t.status in ('active', 'suspended') and d.status in ('ready', 'migrating') and ($1::text is null or t.code = $1)
                order by t.code
                """, [onlyTenantCode]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                codes.Add(reader.GetString(0));
            }
        }

        var outcomes = new List<LicenceRefreshOutcome>();
        foreach (var code in codes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                outcomes.Add(await RefreshAsync(code, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new LicenceRefreshOutcome(code, "-", null, false, null, 0, 0, ex.Message));
            }
        }

        return outcomes;
    }

    private async Task<LicenceRefreshOutcome> RefreshAsync(string code, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var cp = new NpgsqlConnection(options.ConnectionString);
        await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
        var tenant = (await TenantDirectory.FindByCodeAsync(cp, code, cancellationToken).ConfigureAwait(false))!;
        await using var customer = await TenantProvisioner.OpenTenantAsync(secrets, tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false);
        var timeZone = await CustomerLicence.TimeZoneAsync(customer, null, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken).ConfigureAwait(false);

        // 1 and 2, control-plane side: under the customer's row locks, with the rows read again inside the transaction.
        string? previous = null;
        int? activated = null;
        LicenceSnapshot licence;
        await using (var tx = await cp.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await Sql.ExecuteAsync(cp, tx, "select id from cp.entitlements where tenant_id = $1 and status <> 'superseded' for update", cancellationToken, tenant.TenantId).ConfigureAwait(false);
            tenant = (await TenantDirectory.FindByCodeAsync(cp, code, cancellationToken).ConfigureAwait(false))!;
            licence = LicenceSnapshots.Compute(tenant.Entitlements, timeZone, now, LicenceSnapshots.TryParse);
            if (licence.Entitlement is { } inForce)
            {
                var recorded = inForce.Status;
                if (inForce.IsScheduled)
                {
                    activated = inForce.Version;
                    recorded = tenant.Entitlements.Where(e => !e.IsScheduled).Select(e => e.Status).LastOrDefault();
                    await Sql.ExecuteAsync(cp, tx, """
                        update cp.entitlements set status = 'superseded', state_since = $2, updated_at = $2, updated_by = $3, row_version = row_version + 1
                        where tenant_id = $1 and status <> 'superseded' and version < $4
                        """, cancellationToken, tenant.TenantId, now, BaselineSeed.SystemUserId, inForce.Version).ConfigureAwait(false);
                    await LicenceEventsTable.WriteAsync(cp, tx, tenant.TenantId, "entitlement.activated", recorded, licence.State, now, BaselineSeed.SystemUserId,
                        new JsonObject { ["version"] = inForce.Version, ["effective_from"] = EntitlementApplier.Day(inForce.EffectiveFrom) }, null, cancellationToken).ConfigureAwait(false);
                }

                if (inForce.IsScheduled || inForce.Status != licence.State)
                {
                    await Sql.ExecuteAsync(cp, tx, """
                        update cp.entitlements set status = $2, state_since = $3, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1
                        """, cancellationToken, inForce.Id, licence.State, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
                }

                if (recorded != licence.State)
                {
                    previous = recorded;
                    await LicenceEventsTable.WriteAsync(cp, tx, tenant.TenantId, "state.changed", recorded, licence.State, now, BaselineSeed.SystemUserId, StateDetails(inForce, licence), null, cancellationToken).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // 1 and 2, customer side: the copy follows the entitlement in force; a state it has not recorded yet is announced once.
        if (licence.Entitlement is { } current)
        {
            await using var tx = await customer.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var copyVersion = await CustomerLicence.CopyVersionAsync(customer, tx, cancellationToken).ConfigureAwait(false);
            var copyState = await Erp.Kernel.Infrastructure.Identity.IdentityDb.ScalarAsync<string>(customer, tx, "select state from meta.local_entitlement order by created_at desc limit 1", cancellationToken).ConfigureAwait(false);
            if (copyVersion != current.Version)
            {
                var (signature, keyId) = await SignatureAsync(cp, current.Id, cancellationToken).ConfigureAwait(false);
                var payload = new EntitlementPayload(tenant.Code, current.Version, current.Edition, current.EffectiveFrom, current.ExpiresAt,
                    JsonNode.Parse(current.ClaimsJson)!.AsObject());
                await CustomerLicence.ReplaceCopyAsync(customer, tx, payload, keyId, signature, licence.State, now, cancellationToken).ConfigureAwait(false);
                await CustomerLicence.RecordAsync(customer, tx, LicenceEvents.Applied, Erp.Kernel.Infrastructure.Audit.Severities.Info,
                    new JsonObject { ["version"] = current.Version, ["edition"] = current.Edition, ["state"] = licence.State, ["effective_from"] = EntitlementApplier.Day(current.EffectiveFrom) },
                    BaselineSeed.SystemUserId, now, cancellationToken).ConfigureAwait(false);
            }

            if (copyState != licence.State)
            {
                await CustomerLicence.SetCopyStateAsync(customer, tx, licence.State, now, cancellationToken).ConfigureAwait(false);
                var details = StateDetails(current, licence);
                details["from"] = copyState;
                await CustomerLicence.RecordAsync(customer, tx, LicenceEvents.StateChanged, CustomerLicence.SeverityOf(LicenceEvents.StateChanged, licence.State), details,
                    BaselineSeed.SystemUserId, now, cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3: usage readings of the customer's day.
        var readings = new List<UsageReading>
        {
            new(UsageMeters.ActiveUsers, await LimitSql.CountAsync(customer, null, LimitSql.NamedUsers, cancellationToken).ConfigureAwait(false)),
            new(UsageMeters.Companies, await LimitSql.CountAsync(customer, null, LimitSql.Companies, cancellationToken).ConfigureAwait(false)),
            new(UsageMeters.StorageBytes, await LimitSql.CountAsync(customer, null, LimitSql.StorageBytes, cancellationToken).ConfigureAwait(false)),
        };
        if (employees is not null && licence.HasModule("hrm") && tenant.InstalledModules.Contains("hrm"))
        {
            readings.Add(new(UsageMeters.ActiveEmployees, await employees.CountAsync(customer, cancellationToken).ConfigureAwait(false)));
        }

        await UsageMeters.RecordAsync(cp, null, tenant.TenantId, UsageMeters.Period(licence.Today), readings, now, BaselineSeed.SystemUserId, cancellationToken).ConfigureAwait(false);

        // 4: limit events recorded by the customer database, copied once each (keyed by their security-event id).
        var copied = await CopyLimitEventsAsync(cp, customer, tenant.TenantId, cancellationToken).ConfigureAwait(false);
        return new LicenceRefreshOutcome(code, licence.State, previous, previous is not null, activated, readings.Count, copied, null);
    }

    private static JsonObject StateDetails(EntitlementRecord entitlement, LicenceSnapshot licence) => new()
    {
        ["version"] = entitlement.Version,
        ["to"] = licence.State,
        ["expires_at"] = entitlement.ExpiresAt is { } e ? EntitlementApplier.Day(e) : null,
        ["next_state"] = licence.NextState,
        ["next_change"] = licence.NextChange is { } n ? EntitlementApplier.Day(n) : null,
        ["day"] = EntitlementApplier.Day(licence.Today),
        ["time_zone"] = licence.TimeZone,
    };

    private static async Task<(byte[] Signature, string KeyId)> SignatureAsync(NpgsqlConnection cp, Guid entitlementId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(cp, null, "select signature, signing_key_id from cp.entitlements where id = $1", [entitlementId]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetFieldValue<byte[]>(0), reader.GetString(1))
            : throw new InvalidOperationException("The entitlement row disappeared.");
    }

    private static async Task<int> CopyLimitEventsAsync(NpgsqlConnection cp, NpgsqlConnection customer, Guid tenantId, CancellationToken cancellationToken)
    {
        // Only events after the last copied one are read (a margin of a day covers events committed late).
        var since = await Sql.ScalarAsync<DateTimeOffset?>(cp, null,
            "select max(occurred_at) from cp.license_events where tenant_id = $1 and source_event_id is not null", cancellationToken, tenantId).ConfigureAwait(false);
        var rows = new List<(Guid Id, string Type, DateTimeOffset At, string Details)>();
        await using (var command = new NpgsqlCommand("""
            select id, event_type, occurred_at, details::text from aud.security_events
            where event_type in ($1, $2) and occurred_at >= $3 order by occurred_at
            """, customer))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = LicenceEvents.LimitWarning });
            command.Parameters.Add(new NpgsqlParameter { Value = LicenceEvents.LimitReached });
            command.Parameters.Add(new NpgsqlParameter { Value = (since ?? DateTimeOffset.MinValue.AddYears(1)).AddDays(-1) });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3)));
            }
        }

        var copied = 0;
        foreach (var (id, type, at, details) in rows)
        {
            var eventType = type == LicenceEvents.LimitWarning ? "limit.warning" : "limit.reached";
            if (await LicenceEventsTable.WriteAsync(cp, null, tenantId, eventType, null, null, at, BaselineSeed.SystemUserId, JsonNode.Parse(details)!.AsObject(), id, cancellationToken).ConfigureAwait(false))
            {
                copied++;
            }
        }

        return copied;
    }
}
