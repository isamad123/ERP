using System.Security.Cryptography;
using System.Text;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// Web, QR and kiosk capture (slice S1.27a Build items 3 and 4; FR-HRM-ATT-01). The employee of a web or QR punch is the signed-in user's own
/// employment, taken from the token (<c>employments.user_ref</c>), never from the request; a request can only choose among the user's own
/// employments by company. Web and QR punches are recorded with the caller's IP and refused outside <c>hrm.att.allowed_networks</c> when
/// that list is not empty. A QR code is signed for one location and one period (<c>hrm.att.qr_window_seconds</c>); a scan is accepted in its
/// period and the one before, and only inside a signed-in session, so a photograph of the code is useless (Q5). A kiosk punch is made by a
/// station's signed-in user for the employee whose badge or PIN number is mapped in <c>hrm.device_user_maps</c> for all devices of the company
/// (R-155: the number alone identifies the person; there is no second secret). Selfie and geo-fence checks are release 1.1 (CR-088 Q2).
/// Every punch goes through <see cref="IAttendanceEventStore"/>; the event time of web, QR and kiosk punches is the server's clock.
/// </summary>
internal sealed class AttendanceCaptureService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    ICallerAccess caller,
    IDataScopeFilter scope,
    IAttendanceEventStore store,
    AttendanceSettingReader settings,
    EmploymentApplier applier,
    IFieldEncryptorProvider keys,
    ITenantConnectionProvider tenant) : IAttendanceCaptureService
{
    public const string DirectionRequired = "errors.hrm.attendance.direction_required";
    public const string NoEmployment = "errors.hrm.attendance.no_employment";
    public const string CompanyRequired = "errors.hrm.attendance.company_required";
    public const string NetworkNotAllowed = "errors.hrm.attendance.network_not_allowed";
    public const string QrInvalid = "errors.hrm.attendance.qr_invalid";
    public const string QrExpired = "errors.hrm.attendance.qr_expired";
    public const string QrOtherCompany = "errors.hrm.attendance.qr_other_company";
    public const string BadgeRequired = "errors.hrm.attendance.badge_required";
    public const string BadgeUnknown = "errors.hrm.attendance.badge_unknown";
    public const string NotActive = "errors.hrm.attendance.employment_not_active";
    public const string BatchEmpty = "errors.hrm.attendance.batch_empty";
    public const string BatchTooLarge = "errors.hrm.attendance.batch_too_large";
    public const string SourceNotAllowed = "errors.hrm.attendance.source_not_allowed";
    public const int MaxBatch = 500;

    private const string QrPurpose = "hrm.attendance.qr";
    private static readonly string[] CurrentStatuses = ["active", "on_notice"];
    private static readonly string[] WebDirections = [AttendanceDirections.In, AttendanceDirections.Out];

    private DbContextScopeSource Source => new(db);

    // ================================================================== web

    public async Task<PeopleResult> PunchAsync(PunchRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendancePermissions.Punch, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (Check(request.Direction, request.IdempotencyKey) is { } invalid)
        {
            return invalid;
        }

        var own = await OwnEmploymentAsync(request.CompanyId, null, cancellationToken).ConfigureAwait(false);
        if (own.Failure is not null)
        {
            return own.Failure;
        }

        return await RecordAsync(own.Employment!, request.Direction!, AttendanceSources.Web, request.IdempotencyKey!, ipAddress, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> MyDayAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendancePermissions.Punch, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var employments = await db.Set<Employment>().AsNoTracking().Where(e => e.UserRef == actor.UserId && CurrentStatuses.Contains(e.Status))
            .OrderBy(e => e.EmployeeNo).Select(e => new { e.Id, e.CompanyId, e.EmployeeNo }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var chosen = companyId is { } c ? employments.FirstOrDefault(e => e.CompanyId == c) : employments.Count == 1 ? employments[0] : null;
        var events = new List<object>();
        string? next = null;
        DateOnly? today = null;
        if (chosen is not null)
        {
            var date = await applier.TodayAsync(chosen.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            today = date;
            var rows = await db.Set<AttendanceEvent>().AsNoTracking().Where(e => e.EmploymentId == chosen.Id && e.EventDate == date).OrderBy(e => e.EventAt).ThenBy(e => e.Id)
                .Select(e => new { e.Id, e.EventAt, e.Direction, e.Source, e.IsDuplicate }).ToListAsync(cancellationToken).ConfigureAwait(false);
            events.AddRange(rows);
            var last = rows.LastOrDefault(r => !r.IsDuplicate && r.Direction is AttendanceDirections.In or AttendanceDirections.Out);
            next = last?.Direction == AttendanceDirections.In ? AttendanceDirections.Out : AttendanceDirections.In;
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            employments = employments.Select(e => new { employmentId = e.Id, companyId = e.CompanyId, employeeNo = e.EmployeeNo }),
            employmentId = chosen?.Id,
            companyId = chosen?.CompanyId,
            needsCompany = chosen is null && employments.Count > 1,
            today,
            serverTime = clock.UtcNow,
            next,
            events,
        });
    }

    // ================================================================== QR

    public async Task<PeopleResult> QrCodeAsync(Guid companyId, Guid locationId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendancePermissions.QrDisplay, cancellationToken).ConfigureAwait(false)
            || !await caller.CoversCompanyAsync(AttendancePermissions.QrDisplay, companyId, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (await LocationCompanyAsync(locationId, cancellationToken).ConfigureAwait(false) != companyId)
        {
            return PeopleResult.NotFound;
        }

        var window = await settings.QrWindowAsync(companyId, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var period = AttendanceRules.QrPeriod(now, window);
        var code = await CodeAsync(locationId, period, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            code,
            companyId,
            locationId,
            windowSeconds = window,
            expiresAt = AttendanceRules.QrPeriodEnd(period, window),
            serverTime = now,
        });
    }

    public async Task<PeopleResult> QrPunchAsync(QrPunchRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendancePermissions.Punch, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (Check(request.Direction, request.IdempotencyKey) is { } invalid)
        {
            return invalid;
        }

        var parts = (request.Code ?? string.Empty).Trim().Split('.');
        if (parts.Length != 3 || !Guid.TryParse(parts[0], out var locationId) || !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var period))
        {
            return PeopleResult.Invalid(QrInvalid);
        }

        var locationCompany = await LocationCompanyAsync(locationId, cancellationToken).ConfigureAwait(false);
        if (locationCompany is null || !await SignatureOkAsync(locationId, period, parts[2], cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Invalid(QrInvalid);
        }

        var window = await settings.QrWindowAsync(locationCompany.Value, cancellationToken).ConfigureAwait(false);
        if (!AttendanceRules.QrPeriodAccepted(period, AttendanceRules.QrPeriod(clock.UtcNow, window)))
        {
            return PeopleResult.Invalid(QrExpired);
        }

        var own = await OwnEmploymentAsync(null, locationCompany, cancellationToken).ConfigureAwait(false);
        if (own.Failure is not null)
        {
            return own.Failure;
        }

        return await RecordAsync(own.Employment!, request.Direction!, AttendanceSources.Qr, request.IdempotencyKey!, ipAddress, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CodeAsync(Guid locationId, long period, CancellationToken ct)
    {
        var mac = await MacAsync(locationId, period, ct).ConfigureAwait(false);
        return $"{locationId:D}.{period}.{Base64Url(mac)}";
    }

    private async Task<byte[]> MacAsync(Guid locationId, long period, CancellationToken ct)
    {
        var enc = await keys.GetAsync(ct).ConfigureAwait(false);
        return enc.BlindIndex(Encoding.UTF8.GetBytes($"qr|{locationId:D}|{period}"), QrPurpose)[..12];
    }

    private async Task<bool> SignatureOkAsync(Guid locationId, long period, string presented, CancellationToken ct)
    {
        var expected = Encoding.ASCII.GetBytes(Base64Url(await MacAsync(locationId, period, ct).ConfigureAwait(false)));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(presented));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The company of a location (a read of the kernel's organization data, as the existence checks of other modules; null when there is none).</summary>
    private async Task<Guid?> LocationCompanyAsync(Guid locationId, CancellationToken ct)
    {
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select company_id from org.locations where id = @id and not is_deleted", connection);
        command.Parameters.AddWithValue("id", locationId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as Guid?;
    }

    // ================================================================== kiosk

    public async Task<PeopleResult> KioskPunchAsync(KioskPunchRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendancePermissions.Kiosk, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.CompanyId is not { } companyId || !await caller.CoversCompanyAsync(AttendancePermissions.Kiosk, companyId, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var badge = request.BadgeNo?.Trim();
        if (string.IsNullOrEmpty(badge) || badge.Length > 40)
        {
            return PeopleResult.Invalid(BadgeRequired);
        }

        if (Check(request.Direction, request.IdempotencyKey) is { } invalid)
        {
            return invalid;
        }

        var now = clock.UtcNow;
        var today = await applier.TodayAsync(companyId, now, cancellationToken).ConfigureAwait(false);
        var employmentId = await db.Set<DeviceUserMap>().AsNoTracking().Where(m => m.CompanyId == companyId && m.DeviceId == null && m.DeviceUserNo == badge && m.ValidFrom <= today && (m.ValidTo == null || m.ValidTo > today))
            .Select(m => (Guid?)m.EmploymentId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var employment = employmentId is { } id ? await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false) : null;
        if (employment is null)
        {
            return PeopleResult.NotFound with { Error = BadgeUnknown };
        }

        if (!CurrentStatuses.Contains(employment.Status))
        {
            return PeopleResult.Invalid(NotActive);
        }

        var recorded = await RecordAsync(employment, request.Direction!, AttendanceSources.Kiosk, request.IdempotencyKey!, ipAddress, false, cancellationToken, stationScope: actor.UserId.ToString("D")).ConfigureAwait(false);
        if (recorded.Body is null)
        {
            return recorded;
        }

        var first = await db.Set<Person>().AsNoTracking().Where(p => p.Id == employment.PersonId).Select(p => p.FirstName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return recorded with { Body = new { punch = recorded.Body, employeeNo = employment.EmployeeNo, firstName = first } };
    }

    // ================================================================== HR entry and imports

    public async Task<PeopleResult> AppendAsync(AppendEventsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendancePermissions.EventsAppend, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var items = request.Events ?? [];
        if (items.Count == 0)
        {
            return PeopleResult.Invalid(BatchEmpty);
        }

        if (items.Count > MaxBatch)
        {
            return PeopleResult.Invalid(BatchTooLarge);
        }

        // Which employments the caller may write for (data scope); the answer for the others is the one for an unknown employment.
        var ids = items.Where(i => i.EmploymentId is not null).Select(i => i.EmploymentId!.Value).Distinct().ToList();
        var allowed = new HashSet<Guid>();
        foreach (var employment in await db.Set<Employment>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            {
                allowed.Add(employment.Id);
            }
        }

        var inputs = new List<AttendanceEventInput>();
        var early = new Dictionary<int, AttendanceEventOutcome>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key = item.IdempotencyKey ?? string.Empty;
            if (item.EmploymentId is not { } employmentId || !allowed.Contains(employmentId))
            {
                early[i] = new(key, AttendanceEventOutcome.Refused, null, false, AttendanceEventStore.EmploymentUnknown);
            }
            else if (item.Source is not (AttendanceSources.HrEntry or AttendanceSources.Import))
            {
                early[i] = new(key, AttendanceEventOutcome.Refused, null, false, SourceNotAllowed);
            }
            else if (string.IsNullOrWhiteSpace(item.IdempotencyKey))
            {
                early[i] = new(key, AttendanceEventOutcome.Refused, null, false, AttendanceEventStore.KeyRequired);
            }
            else if (item.EventAt is not { } at)
            {
                early[i] = new(key, AttendanceEventOutcome.Refused, null, false, AttendanceEventStore.FutureTime);
            }
            else
            {
                inputs.Add(new AttendanceEventInput(employmentId, at, item.Direction ?? string.Empty, item.Source, AttendanceRules.StoredKey(item.Source, employmentId.ToString("D"), key), null, null, item.SupersedesEventId));
            }
        }

        var stored = await store.AppendAsync(inputs, cancellationToken).ConfigureAwait(false);
        var results = new List<object>();
        var next = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var outcome = early.TryGetValue(i, out var e) ? e : stored[next++] with { IdempotencyKey = items[i].IdempotencyKey ?? string.Empty };
            results.Add(new { index = i, key = outcome.IdempotencyKey, status = outcome.Status, eventId = outcome.EventId, isDuplicate = outcome.IsDuplicate, error = outcome.Error });
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { results });
    }

    // ================================================================== shared

    private static PeopleResult? Check(string? direction, string? key)
    {
        if (direction is null || !WebDirections.Contains(direction, StringComparer.Ordinal))
        {
            return PeopleResult.Invalid(DirectionRequired);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return PeopleResult.Invalid(AttendanceEventStore.KeyRequired);
        }

        return key.Length > 100 ? PeopleResult.Invalid(AttendanceEventStore.KeyTooLong) : null;
    }

    private async Task<(Employment? Employment, PeopleResult? Failure)> OwnEmploymentAsync(Guid? companyId, Guid? mustBeCompany, CancellationToken ct)
    {
        var query = db.Set<Employment>().AsNoTracking().Where(e => e.UserRef == actor.UserId && CurrentStatuses.Contains(e.Status));
        if ((mustBeCompany ?? companyId) is { } wanted)
        {
            query = query.Where(e => e.CompanyId == wanted);
        }

        var found = await query.OrderBy(e => e.EmployeeNo).Take(2).ToListAsync(ct).ConfigureAwait(false);
        return found.Count switch
        {
            0 => (null, PeopleResult.Invalid(mustBeCompany is not null && await HasAnyAsync(ct).ConfigureAwait(false) ? QrOtherCompany : NoEmployment)),
            1 => (found[0], null),
            _ => (null, PeopleResult.Invalid(CompanyRequired)),
        };
    }

    private Task<bool> HasAnyAsync(CancellationToken ct) =>
        db.Set<Employment>().AsNoTracking().AnyAsync(e => e.UserRef == actor.UserId && CurrentStatuses.Contains(e.Status), ct);

    private async Task<PeopleResult> RecordAsync(Employment employment, string direction, string source, string key, string? ipAddress, bool checkNetwork, CancellationToken ct, string? stationScope = null)
    {
        if (checkNetwork)
        {
            var ranges = await settings.NetworksAsync(employment.CompanyId, ct).ConfigureAwait(false);
            if (!AttendanceRules.AddressAllowed(ipAddress, [.. ranges]))
            {
                return PeopleResult.Forbidden(NetworkNotAllowed);
            }
        }

        var now = clock.UtcNow;
        var stored = (await store.AppendAsync([new AttendanceEventInput(employment.Id, now, direction, source,
            AttendanceRules.StoredKey(source, stationScope ?? employment.Id.ToString("D"), key), null, ipAddress)], ct).ConfigureAwait(false))[0];
        if (stored.Status == AttendanceEventOutcome.Refused)
        {
            return PeopleResult.Invalid(stored.Error!);
        }

        var body = new { eventId = stored.EventId, status = stored.Status, eventAt = now, direction, source, isDuplicate = stored.IsDuplicate };
        return new PeopleResult(stored.Status == AttendanceEventOutcome.Stored ? PeopleOutcome.Created : PeopleOutcome.Done, Body: body);
    }
}
