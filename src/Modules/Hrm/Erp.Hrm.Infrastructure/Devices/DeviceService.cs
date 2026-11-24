using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Attendance;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>
/// The device registry and the console's reads (slice S1.27b Build items 6 and 9; FR-HRM-ATT-02): register a device at a location with its driver,
/// connection (host, port, protocol only) and credentials, change or retire it, ask for a sync now, list devices with status, last sync and last
/// error, and search the punch log. The credentials go to the secret store under a reference kept on the row and are never returned, logged or audited
/// (the audit records the reference, which is not the secret). Devices exist only for a company that switched <c>hrm.att.biometric_enabled</c> on:
/// registering is the entity's decision to use biometric capture (ATT-20). The generated writes of the table are closed.
/// </summary>
internal sealed class DeviceService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    ICallerAccess caller,
    IDataScopeFilter scope,
    DeviceRuntime runtime,
    AttendanceSettingReader settings) : IDeviceService
{
    public const string SerialRequired = "errors.hrm.device.serial_required";
    public const string SerialTaken = "errors.hrm.device.serial_taken";
    public const string ProviderUnknown = "errors.hrm.device.provider_unknown";
    public const string HostInvalid = "errors.hrm.device.host_invalid";
    public const string PortInvalid = "errors.hrm.device.port_invalid";
    public const string ProtocolInvalid = "errors.hrm.device.protocol_invalid";
    public const string LocationUnknown = "errors.hrm.device.location_unknown";
    public const string CredentialsRequired = "errors.hrm.device.credentials_required";
    public const string BiometricDisabled = "errors.hrm.device.biometric_disabled";
    public const string StatusInvalid = "errors.hrm.device.status_invalid";
    public const string NotPullable = "errors.hrm.device.not_pullable";
    public const string Retired = "errors.hrm.device.retired";
    public const int MaxLog = 500;

    private const int TextLength = 100;
    private static readonly string[] Statuses = ["active", "retired"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> CreateAsync(DeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(DevicePermissions.DevicesCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CompanyId is not { } company)
        {
            errors["companyId"] = [PersonService.Required];
            return PeopleResult.Fields(errors);
        }

        if (!await caller.CoversCompanyAsync(DevicePermissions.DevicesCreate, company, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var provider = request.Provider?.Trim();
        var driver = runtime.Driver(provider);
        var serial = request.SerialNo?.Trim();
        if (driver is null)
        {
            errors["provider"] = [ProviderUnknown];
        }

        if (string.IsNullOrEmpty(serial) || serial.Length > TextLength)
        {
            errors["serialNo"] = [SerialRequired];
        }

        if (request.LocationId is not { } location || !await LocationExistsAsync(company, location, cancellationToken).ConfigureAwait(false))
        {
            errors["locationId"] = [LocationUnknown];
        }

        var connection = driver is null ? null : Connection(driver, request, errors);
        if (driver is { CanPull: false } && string.IsNullOrWhiteSpace(request.Credentials))
        {
            errors["credentials"] = [CredentialsRequired];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        if (!await runtime.BiometricEnabledAsync(company, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Invalid(BiometricDisabled);
        }

        if (await db.Set<AttendanceDevice>().AsNoTracking().AnyAsync(d => d.CompanyId == company && d.SerialNo == serial && !d.IsDeleted, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Conflict(SerialTaken);
        }

        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        var row = new AttendanceDevice
        {
            Id = id,
            CompanyId = company,
            LocationId = request.LocationId!.Value,
            ConnectorId = await runtime.ConnectorAsync(driver!.Provider, actor.UserId, now, cancellationToken).ConfigureAwait(false),
            SerialNo = serial!,
            Model = Clean(request.Model),
            Connection = connection!,
            Status = "active",
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, AttendanceDeviceAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (!string.IsNullOrWhiteSpace(request.Credentials))
        {
            row.CredentialsRef = runtime.NewReference(id);
            await runtime.SaveSecretAsync(row.CredentialsRef, request.Credentials, cancellationToken).ConfigureAwait(false);
        }

        db.Add(row);
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid deviceId, DeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(DevicePermissions.DevicesUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<AttendanceDevice>().FirstOrDefaultAsync(d => d.Id == deviceId && !d.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, AttendanceDeviceAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, AttendanceDeviceAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        var provider = (await runtime.ProvidersAsync([row.ConnectorId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(row.ConnectorId);
        var driver = runtime.Driver(provider);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CompanyId is { } other && other != row.CompanyId)
        {
            errors["companyId"] = [PersonService.CannotClear];
        }

        if (request.Provider is { } p && !string.Equals(p.Trim(), provider, StringComparison.Ordinal))
        {
            errors["provider"] = [ProviderUnknown];
        }

        if (request.SerialNo is { } s && s.Trim() != row.SerialNo)
        {
            var serial = s.Trim();
            if (serial.Length is 0 or > TextLength)
            {
                errors["serialNo"] = [SerialRequired];
            }
            else if (await db.Set<AttendanceDevice>().AsNoTracking().AnyAsync(d => d.CompanyId == row.CompanyId && d.SerialNo == serial && !d.IsDeleted && d.Id != row.Id, cancellationToken).ConfigureAwait(false))
            {
                errors["serialNo"] = [SerialTaken];
            }
            else
            {
                row.SerialNo = serial;
            }
        }

        if (request.LocationId is { } loc && loc != row.LocationId)
        {
            if (await LocationExistsAsync(row.CompanyId, loc, cancellationToken).ConfigureAwait(false))
            {
                row.LocationId = loc;
            }
            else
            {
                errors["locationId"] = [LocationUnknown];
            }
        }

        if (driver is not null && (request.Host is not null || request.Port is not null || request.Protocol is not null))
        {
            var merged = new DeviceRequest { Host = request.Host ?? Text(row, "host"), Port = request.Port ?? Number(row, "port"), Protocol = request.Protocol ?? Text(row, "protocol") };
            if (Connection(driver, merged, errors) is { } connection)
            {
                row.Connection = connection;
            }
        }

        if (request.Model is not null)
        {
            row.Model = Clean(request.Model);
        }

        if (request.Status is { } status)
        {
            if (!Statuses.Contains(status, StringComparer.Ordinal))
            {
                errors["status"] = [StatusInvalid];
            }
            else if (status != row.Status && !(status == "active" && row.Status == "offline"))
            {
                row.Status = status;
            }
        }

        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        if (!string.IsNullOrWhiteSpace(request.Credentials))
        {
            row.CredentialsRef ??= runtime.NewReference(row.Id);
            await runtime.SaveSecretAsync(row.CredentialsRef, request.Credentials, cancellationToken).ConfigureAwait(false);
        }

        EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> SyncNowAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(DevicePermissions.DevicesSync, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await db.Set<AttendanceDevice>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && !d.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, AttendanceDeviceAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, AttendanceDeviceAccess.Scope, "sync", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.Status == "retired")
        {
            return PeopleResult.Invalid(Retired);
        }

        var provider = (await runtime.ProvidersAsync([row.ConnectorId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(row.ConnectorId);
        if (runtime.Driver(provider) is not { CanPull: true })
        {
            return PeopleResult.Invalid(NotPullable);
        }

        var now = clock.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"update hrm.attendance_devices set sync_requested_at = {now} where id = {deviceId}", cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { id = deviceId, syncRequestedAt = now });
    }

    public async Task<PeopleResult> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(DevicePermissions.DevicesRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var query = db.Set<AttendanceDevice>().AsNoTracking().Where(d => !d.IsDeleted);
        if (companyId is { } c)
        {
            query = query.Where(d => d.CompanyId == c);
        }

        query = await scope.ApplyAsync(query, AttendanceDeviceAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var rows = await query.OrderBy(d => d.SerialNo).Take(1000).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = await ViewsAsync(rows, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { devices = items });
    }

    public async Task<PeopleResult> PunchLogAsync(PunchLogQuery q, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(q);
        if (!await permissions.HasPermissionAsync(DevicePermissions.DevicesRead, cancellationToken).ConfigureAwait(false)
            || !await permissions.HasPermissionAsync(AttendancePermissions.EventsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (q.Source is { } src && !AttendanceSources.All.Contains(src, StringComparer.Ordinal))
        {
            return PeopleResult.Invalid(AttendanceEventStore.UnknownSource);
        }

        var query = db.Set<AttendanceEvent>().AsNoTracking().AsQueryable();
        if (q.CompanyId is { } company)
        {
            query = query.Where(e => e.CompanyId == company);
        }

        if (q.From is { } from)
        {
            query = query.Where(e => e.EventDate >= from);
        }

        if (q.To is { } to)
        {
            query = query.Where(e => e.EventDate <= to);
        }

        if (q.Source is { } source)
        {
            query = query.Where(e => e.Source == source);
        }

        if (q.DeviceId is { } device)
        {
            query = query.Where(e => e.DeviceId == device);
        }

        if (q.EmploymentId is { } employment)
        {
            query = query.Where(e => e.EmploymentId == employment);
        }

        if (q.IncludeDuplicates != true)
        {
            query = query.Where(e => !e.IsDuplicate);
        }

        if (!string.IsNullOrWhiteSpace(q.EmployeeNo))
        {
            var no = q.EmployeeNo.Trim();
            var ids = db.Set<Employment>().AsNoTracking().Where(e => e.EmployeeNo == no).Select(e => e.Id);
            query = query.Where(e => ids.Contains(e.EmploymentId));
        }

        if (q.After is { } after && after.Split('|') is [var at, var id] && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var afterAt) && Guid.TryParse(id, out var afterId))
        {
            query = query.Where(e => e.EventAt < afterAt || (e.EventAt == afterAt && e.Id.CompareTo(afterId) < 0));
        }

        query = await scope.ApplyAsync(query, AttendanceEventAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(q.Limit ?? 100, 1, MaxLog);
        var page = await query.OrderByDescending(e => e.EventAt).ThenByDescending(e => e.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var more = page.Count > limit;
        page = [.. page.Take(limit)];
        var employments = page.Select(e => e.EmploymentId).Distinct().ToList();
        var numbers = await db.Set<Employment>().AsNoTracking().Where(e => employments.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.EmployeeNo, cancellationToken).ConfigureAwait(false);
        var serials = await db.Set<AttendanceDevice>().AsNoTracking().Where(d => page.Select(e => e.DeviceId).Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.SerialNo, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            events = page.Select(e => new
            {
                id = e.Id,
                companyId = e.CompanyId,
                employmentId = e.EmploymentId,
                employeeNo = numbers.GetValueOrDefault(e.EmploymentId),
                eventAt = e.EventAt,
                receivedAt = e.ReceivedAt,
                eventDate = e.EventDate,
                direction = e.Direction,
                source = e.Source,
                deviceId = e.DeviceId,
                deviceSerial = e.DeviceId is { } d ? serials.GetValueOrDefault(d) : null,
                isDuplicate = e.IsDuplicate,
            }),
            next = more ? $"{page[^1].EventAt:O}|{page[^1].Id}" : null,
        });
    }

    private async Task<List<DeviceView>> ViewsAsync(IReadOnlyList<AttendanceDevice> rows, CancellationToken ct)
    {
        var providers = await runtime.ProvidersAsync(rows.Select(r => r.ConnectorId), ct).ConfigureAwait(false);
        var open = await db.Set<UnmappedPunch>().AsNoTracking().Where(p => p.Status == "open").GroupBy(p => p.DeviceId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var offline = new Dictionary<Guid, int>();
        var items = new List<DeviceView>();
        foreach (var d in rows)
        {
            if (!offline.TryGetValue(d.CompanyId, out var minutes))
            {
                offline[d.CompanyId] = minutes = await settings.IntAsync(HrmSettingKeys.HrmAttDeviceOfflineMinutes, d.CompanyId, 30, ct).ConfigureAwait(false);
            }

            items.Add(new DeviceView(d.Id, d.CompanyId, d.LocationId, d.SerialNo, d.Model, providers.GetValueOrDefault(d.ConnectorId), Text(d, "host"), Number(d, "port"), Text(d, "protocol"),
                d.CredentialsRef is not null, d.Status, d.Status != "retired" && DeviceRules.IsOnline(d.LastSeenAt, now, minutes), d.LastSeenAt, d.LastSyncAt, d.LastError, d.SyncRequestedAt,
                open.GetValueOrDefault(d.Id), d.RowVersion));
        }

        return items;
    }

    private sealed record DeviceView(Guid Id, Guid CompanyId, Guid LocationId, string SerialNo, string? Model, string? Provider, string? Host, int? Port, string? Protocol, bool HasCredentials,
        string Status, bool Online, DateTimeOffset? LastSeenAt, DateTimeOffset? LastSyncAt, string? LastError, DateTimeOffset? SyncRequestedAt, int UnmappedOpen, long RowVersion);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, TextLength)];

    private static string? Text(AttendanceDevice d, string name) =>
        d.Connection.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Number(AttendanceDevice d, string name) =>
        d.Connection.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>The connection document (host, port, protocol only): a pushing device needs none of them.</summary>
    private static JsonDocument? Connection(IAttendanceDeviceDriver driver, DeviceRequest request, Dictionary<string, string[]> errors)
    {
        if (!driver.CanPull)
        {
            return JsonDocument.Parse("""{"protocol":"push"}""");
        }

        var host = request.Host?.Trim();
        var protocol = request.Protocol?.Trim().ToLowerInvariant() ?? "tcp";
        if (!DeviceRules.IsHost(host))
        {
            errors["host"] = [HostInvalid];
        }

        if (request.Port is < 1 or > 65535)
        {
            errors["port"] = [PortInvalid];
        }

        if (!DeviceRules.Protocols.Contains(protocol, StringComparer.Ordinal))
        {
            errors["protocol"] = [ProtocolInvalid];
        }

        return errors.ContainsKey("host") || errors.ContainsKey("port") || errors.ContainsKey("protocol")
            ? null
            : JsonSerializer.SerializeToDocument(new { host, port = request.Port ?? ZkTecoDriver.DefaultPort, protocol });
    }

    private async Task<bool> LocationExistsAsync(Guid company, Guid location, CancellationToken ct) =>
        (await db.Database.SqlQuery<bool>($"select exists (select 1 from org.locations where id = {location} and company_id = {company}) as \"Value\"").ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault();

    private async Task<PeopleResult> SaveAsync(AttendanceDevice row, CancellationToken ct)
    {
        var created = db.Entry(row).State == EntityState.Added;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(SerialTaken);
        }

        return new PeopleResult(created ? PeopleOutcome.Created : PeopleOutcome.Done, Body: (await ViewsAsync([row], ct).ConfigureAwait(false))[0]);
    }
}
