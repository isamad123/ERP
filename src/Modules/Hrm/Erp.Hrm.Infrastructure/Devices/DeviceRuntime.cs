using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>
/// What a device's services share (slice S1.27b): the drivers by provider code, the secret store reference of a device and the connection a driver
/// is given. The secret is read from the store for one call and never kept, logged or returned (CLAUDE.md "no secrets"; ATT-02).
/// </summary>
internal sealed class DeviceRuntime(IEnumerable<IAttendanceDeviceDriver> drivers, ISecretStore secrets, ITenantContext tenant, ISettings settings, HrmDbContext db)
{
    private readonly Dictionary<string, IAttendanceDeviceDriver> _drivers = drivers.ToDictionary(d => d.Provider, StringComparer.Ordinal);

    public IAttendanceDeviceDriver? Driver(string? provider) => provider is not null && _drivers.TryGetValue(provider, out var d) ? d : null;

    public static string SecretReference(string tenantCode, Guid deviceId) => $"hrm.device.{tenantCode}.{deviceId:N}";

    public string NewReference(Guid deviceId) => SecretReference(tenant.TenantCode, deviceId);

    public Task SaveSecretAsync(string reference, string value, CancellationToken ct) => secrets.PutAsync(reference, value, ct);

    /// <summary>The provider of each connector id (<c>itg.connectors.provider</c>).</summary>
    public async Task<Dictionary<Guid, string>> ProvidersAsync(IEnumerable<Guid> connectorIds, CancellationToken ct)
    {
        var ids = connectorIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.Database.SqlQuery<ConnectorRow>($"select id as \"Id\", provider as \"Provider\" from itg.connectors where id = any({ids})").ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => r.Provider);
    }

    /// <summary>Finds or creates the connector row of a provider (kind <c>biometric</c>) that devices of that driver point to.</summary>
    public async Task<Guid> ConnectorAsync(string provider, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        var code = ("biometric_" + provider).ToLowerInvariant();
        var name = JsonSerializer.Serialize(new Dictionary<string, string> { ["en"] = "Biometric devices (" + provider + ")", ["ur"] = "بایومیٹرک آلات (" + provider + ")" });
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into itg.connectors (id, code, name_i18n, kind, provider, config, status, is_active, created_at, created_by, updated_at, updated_by, row_version)
            values ({Guid.CreateVersion7(now)}, {code}, {name}::jsonb, 'biometric', {provider}, {"{}"}::jsonb, 'active', true, {now}, {actorId}, {now}, {actorId}, 1)
            on conflict (lower(code)) do nothing
            """, ct).ConfigureAwait(false);
        return (await db.Database.SqlQuery<Guid>($"select id as \"Value\" from itg.connectors where lower(code) = {code}").ToListAsync(ct).ConfigureAwait(false)).First();
    }

    public async Task<TimeZoneInfo> ZoneAsync(Guid companyId, CancellationToken ct)
    {
        var value = (await settings.GetAsync("org.time_zone", new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.String && TimeZoneInfo.TryFindSystemTimeZoneById(value.GetString()!, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    /// <summary>The driver's view of a device; <paramref name="withSecret"/> reads the stored secret (null when none is stored or it cannot be read).</summary>
    public async Task<DeviceConnection> ConnectionAsync(AttendanceDevice device, bool withSecret, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        var root = device.Connection.RootElement;
        var host = root.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()! : string.Empty;
        var port = root.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : 0;
        var protocol = root.TryGetProperty("protocol", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "push";
        string? secret = null;
        if (withSecret && device.CredentialsRef is { Length: > 0 } reference)
        {
            secret = await secrets.GetAsync(reference, ct).ConfigureAwait(false);
        }

        return new DeviceConnection(device.Id, device.SerialNo, host, port, protocol, secret, await ZoneAsync(device.CompanyId, ct).ConfigureAwait(false));
    }

    /// <summary>The switch <c>hrm.att.biometric_enabled</c> of a company.</summary>
    public async Task<bool> BiometricEnabledAsync(Guid companyId, CancellationToken ct) =>
        (await settings.GetAsync(HrmSettingKeys.HrmAttBiometricEnabled, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value.ValueKind == JsonValueKind.True;

    private sealed record ConnectorRow(Guid Id, string Provider);
}
