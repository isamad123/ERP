using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Identity;
using MimeKit;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications.Email;

/// <summary>What the mail connector of a customer holds (no password: that stays in the secret store).</summary>
public sealed record MailConnectorView(string Provider, string Status, string? FromAddress, string? Host, int? Port, string? Security, string? Username, bool HasPassword);

/// <summary>
/// The operator's mail commands (slice S1.11b2, ruling Q10, plan point P2; docs/runbooks/mail-setup.md). A customer's mail
/// connector is the row <c>itg.connectors</c> code <c>email</c>: provider <c>relay</c> only names the sender address the
/// operator checked on the customer's own domain (SPF and DKIM), sent through the operator's relay; provider <c>smtp</c> is
/// the customer's own mail server (on-premises), whose password goes to the secret store, never into the database. Every
/// change is in the customer's audit log.
/// </summary>
public sealed class MailSetup(ControlPlaneOptions options, ISecretStore secrets, IClock clock, MigrationSet migrations)
{
    public static string PasswordReference(string tenantCode) => $"tenants/{tenantCode}/mail-smtp-password";

    public Task SetSenderAsync(string tenantCode, string fromAddress, string operatorEmail, CancellationToken ct) =>
        SaveAsync(tenantCode, "relay", new JsonObject { ["from_address"] = Address(fromAddress) }, null, operatorEmail, ct);

    public async Task SetServerAsync(string tenantCode, string host, int port, string security, string? username, string fromAddress, string? password, string operatorEmail, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentException("The port must be between 1 and 65535.", nameof(port));
        }

        if (security is not ("starttls" or "ssl" or "none"))
        {
            throw new ArgumentException("Security must be starttls, ssl or none.", nameof(security));
        }

        string? reference = null;
        if (!string.IsNullOrEmpty(password))
        {
            reference = PasswordReference(tenantCode);
            await secrets.PutAsync(reference, password, ct).ConfigureAwait(false);
        }

        var config = new JsonObject { ["host"] = host.Trim(), ["port"] = port, ["security"] = security, ["username"] = username, ["from_address"] = Address(fromAddress) };
        await SaveAsync(tenantCode, "smtp", config, reference, operatorEmail, ct).ConfigureAwait(false);
    }

    /// <summary>Switches the connector off: the customer's e-mails go through the operator's relay and its default sender again.</summary>
    public async Task<bool> ClearAsync(string tenantCode, string operatorEmail, CancellationToken ct)
    {
        await using var c = await OpenAsync(tenantCode, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var id = await IdentityDb.ScalarAsync<Guid?>(c, tx, """
            update itg.connectors set status = 'disabled', updated_at = $2, updated_by = $3, row_version = row_version + 1
            where lower(code) = $1 and status <> 'disabled' returning id
            """, ct, MailServerResolver.ConnectorCode, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
        if (id is { } done)
        {
            await AuditAsync(c, tx, done, "status", new JsonArray { new JsonObject { ["field"] = "status", ["after"] = "disabled" }, By(operatorEmail) }, now, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return id is not null;
    }

    public async Task<MailConnectorView?> ShowAsync(string tenantCode, CancellationToken ct)
    {
        await using var c = await OpenAsync(tenantCode, ct).ConfigureAwait(false);
        var row = (await NotificationConsumer.ReadAsync(c, null, "select provider, status, config::text, credentials_ref from itg.connectors where lower(code) = $1",
            r => (Provider: r.GetString(0), Status: r.GetString(1), Config: r.GetString(2), Ref: r.IsDBNull(3) ? null : r.GetString(3)), ct, MailServerResolver.ConnectorCode).ConfigureAwait(false)).FirstOrDefault();
        if (row.Provider is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(row.Config);
        var e = doc.RootElement;
        string? Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new MailConnectorView(row.Provider, row.Status, Text("from_address"), Text("host"), e.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : null,
            Text("security"), Text("username"), row.Ref is not null);
    }

    private async Task SaveAsync(string tenantCode, string provider, JsonObject config, string? credentialsRef, string operatorEmail, CancellationToken ct)
    {
        await using var c = await OpenAsync(tenantCode, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var id = await IdentityDb.ScalarAsync<Guid>(c, tx, """
            insert into itg.connectors as t (id, code, name_i18n, kind, provider, config, credentials_ref, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"E-mail","ur":"ای میل"}'::jsonb, 'email', $3, $4, $5, 'active', $6, $7, $6, $7)
            on conflict (lower(code)) do update set kind = 'email', provider = excluded.provider, config = excluded.config,
                credentials_ref = coalesce(excluded.credentials_ref, case when excluded.provider = 'smtp' then t.credentials_ref end),
                status = 'active', updated_at = excluded.updated_at, updated_by = excluded.updated_by, row_version = t.row_version + 1
            returning id
            """, ct, Guid.CreateVersion7(now), MailServerResolver.ConnectorCode, provider, new JsonPayload(config.ToJsonString()), credentialsRef, now, BaselineSeed.SystemUserId).ConfigureAwait(false);
        var changes = new JsonArray { new JsonObject { ["field"] = "provider", ["after"] = provider } };
        foreach (var (name, value) in config)
        {
            changes.Add(new JsonObject { ["field"] = "config." + name, ["after"] = value?.DeepClone() });
        }

        changes.Add(new JsonObject { ["field"] = "password_stored", ["after"] = credentialsRef is not null });
        changes.Add(By(operatorEmail));
        await AuditAsync(c, tx, id, "update", changes, now, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static JsonObject By(string operatorEmail) => new() { ["field"] = "operator", ["after"] = operatorEmail };

    private static Task<int> AuditAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, string action, JsonArray changes, DateTimeOffset now, CancellationToken ct) =>
        AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, null, ActorKinds.System, "itg.connectors", id, null, action, changes, null, null, null, AuditSources.System,
            BaselineSeed.SystemUserId)], ct);

    private static string Address(string value) =>
        MailboxAddress.TryParse(value?.Trim(), out var parsed) && parsed.Address.Contains('@', StringComparison.Ordinal) ? parsed.Address
            : throw new ArgumentException($"'{value}' is not an e-mail address.", nameof(value));

    private async Task<NpgsqlConnection> OpenAsync(string tenantCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantCode);
        var host = await TenantHosts.PrimaryAsync(options, tenantCode, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Customer {tenantCode} is not registered.");
        var tenant = await new TenantDirectory(options).FindByHostAsync(host, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Customer {tenantCode} has no database yet.");
        return await new TenantProvisioner(options, secrets, clock, migrations).OpenTenantAsync(tenant, TenantLogins.Owner, ct).ConfigureAwait(false);
    }
}
