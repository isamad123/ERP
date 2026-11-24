using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>
/// Device-user mapping, consent, removal tasks and unmapped punches (slice S1.27b Build item 8; FR-HRM-ATT-03, ATT-20). A device user number is
/// mapped to an employment from a date; while <c>hrm.att.biometric_enabled</c> is on a mapping to one device needs the employee's consent date
/// (not in the future), and without the switch no device mapping is made at all. A mapping for all devices of the company is a badge or PIN number
/// (the kiosk's) and involves no biometrics. Making a mapping brings the open unmapped punches it explains into the event store with their original
/// times. Ending a mapping of one device, by hand or because the employment ended (<see cref="DeviceSeparationConsumer"/>), makes a removal task:
/// the user must be removed from the device, which HR confirms. No template is ever read, stored or logged: the system holds a user number only.
/// </summary>
internal sealed class DeviceMappingService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    ICallerAccess caller,
    IDataScopeFilter scope,
    DeviceRuntime runtime,
    DeviceIngestion ingestion,
    EmploymentApplier applier) : IDeviceMappingService
{
    public const string NumberRequired = "errors.hrm.device.user_no_required";
    public const string EmploymentUnknown = "errors.hrm.device.employment_unknown";
    public const string DeviceUnknown = "errors.hrm.device.device_unknown";
    public const string ConsentRequired = "errors.hrm.device.consent_required";
    public const string ConsentInFuture = "errors.hrm.device.consent_in_future";
    public const string BiometricDisabled = "errors.hrm.device.biometric_disabled";
    public const string NumberInUse = "errors.hrm.device.number_in_use";
    public const string EndBeforeStart = "errors.hrm.device.end_before_start";
    public const string AlreadyEnded = "errors.hrm.device.already_ended";
    public const string NoRemovalDue = "errors.hrm.device.no_removal_due";
    public const string NotOpen = "errors.hrm.device.punch_not_open";
    public const int NumberLength = 50;

    private static readonly string[] Current = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> MapAsync(MapRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(DevicePermissions.MapsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CompanyId is not { } company)
        {
            errors["companyId"] = [PersonService.Required];
            return PeopleResult.Fields(errors);
        }

        if (!await caller.CoversCompanyAsync(DevicePermissions.MapsCreate, company, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var number = request.DeviceUserNo?.Trim();
        if (string.IsNullOrEmpty(number) || number.Length > NumberLength || number.Any(char.IsControl))
        {
            errors["deviceUserNo"] = [NumberRequired];
        }

        var no = request.EmployeeNo?.Trim();
        var employment = request.EmploymentId is { } e
            ? await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == e && x.CompanyId == company, cancellationToken).ConfigureAwait(false)
            : string.IsNullOrEmpty(no) ? null : await db.Set<Employment>().AsNoTracking().Where(x => x.CompanyId == company && x.EmployeeNo == no && Current.Contains(x.Status)).OrderByDescending(x => x.JoiningDate).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (employment is null || !Current.Contains(employment.Status) || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            errors["employmentId"] = [EmploymentUnknown];
        }

        AttendanceDevice? device = null;
        if (request.DeviceId is { } d)
        {
            device = await db.Set<AttendanceDevice>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == d && x.CompanyId == company && !x.IsDeleted && x.Status != "retired", cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                errors["deviceId"] = [DeviceUnknown];
            }
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        var today = await applier.TodayAsync(company, now, cancellationToken).ConfigureAwait(false);
        DateTimeOffset? consentAt = null;
        if (device is not null)
        {
            if (!await runtime.BiometricEnabledAsync(company, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Invalid(BiometricDisabled);
            }

            if (request.ConsentDate is not { } consent)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["consentDate"] = [ConsentRequired] });
            }

            if (consent > today)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["consentDate"] = [ConsentInFuture] });
            }

            consentAt = new DateTimeOffset(consent.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        var from = request.ValidFrom ?? today;
        var overlap = await db.Set<DeviceUserMap>().AsNoTracking().AnyAsync(m => m.CompanyId == company && m.DeviceId == request.DeviceId && m.DeviceUserNo == number && (m.ValidTo == null || m.ValidTo > from), cancellationToken).ConfigureAwait(false);
        if (overlap)
        {
            return PeopleResult.Conflict(NumberInUse);
        }

        var row = new DeviceUserMap
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = company,
            DeviceId = request.DeviceId,
            DeviceUserNo = number!,
            EmploymentId = employment!.Id,
            ConsentAt = consentAt,
            ValidFrom = from,
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, DeviceUserMapAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        db.Add(row);
        var failure = await SaveAsync(cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        var replayed = await ingestion.ReplayAsync(row, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Created, Body: new { map = View(row, employment.EmployeeNo), replayedPunches = replayed });
    }

    public async Task<PeopleResult> EndAsync(Guid mapId, EndMapRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(DevicePermissions.MapsEnd, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<DeviceUserMap>().FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, DeviceUserMapAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, DeviceUserMapAccess.Scope, "end", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        var now = clock.UtcNow;
        var end = request.EndDate ?? await applier.TodayAsync(row.CompanyId, now, cancellationToken).ConfigureAwait(false);
        if (row.ValidTo is { } ended && ended <= end)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Invalid(AlreadyEnded);
        }

        if (end <= row.ValidFrom)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Invalid(EndBeforeStart);
        }

        row.ValidTo = end;
        await MakeRemovalTaskAsync(row, now, cancellationToken).ConfigureAwait(false);
        EmploymentApplier.Bump(row, now, actor.UserId);
        var failure = await SaveAsync(cancellationToken).ConfigureAwait(false);
        return failure ?? new PeopleResult(PeopleOutcome.Done, Body: View(row, await EmployeeNoAsync(row.EmploymentId, cancellationToken).ConfigureAwait(false)));
    }

    public async Task<PeopleResult> ConfirmRemovalAsync(Guid mapId, RemovalDoneRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(DevicePermissions.MapsRemoval, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<DeviceUserMap>().FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, DeviceUserMapAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, DeviceUserMapAccess.Scope, "removal", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        if (row.RemovalDueAt is null || row.RemovalDoneAt is not null)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Invalid(NoRemovalDue);
        }

        var now = clock.UtcNow;
        row.RemovalDoneAt = now;
        EmploymentApplier.Bump(row, now, actor.UserId);
        var failure = await SaveAsync(cancellationToken).ConfigureAwait(false);
        return failure ?? new PeopleResult(PeopleOutcome.Done, Body: View(row, await EmployeeNoAsync(row.EmploymentId, cancellationToken).ConfigureAwait(false)));
    }

    public async Task<PeopleResult> ListAsync(Guid? companyId, Guid? deviceId, string? state, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(DevicePermissions.MapsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var query = db.Set<DeviceUserMap>().AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(m => m.CompanyId == c);
        }

        if (deviceId is { } d)
        {
            query = query.Where(m => m.DeviceId == d);
        }

        if (state is "removal_pending")
        {
            query = query.Where(m => m.RemovalDueAt != null && m.RemovalDoneAt == null);
        }
        else if (state is "active" or "ended")
        {
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            query = state == "active" ? query.Where(m => m.ValidTo == null || m.ValidTo > today) : query.Where(m => m.ValidTo != null && m.ValidTo <= today);
        }

        query = await scope.ApplyAsync(query, DeviceUserMapAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var rows = await query.OrderBy(m => m.DeviceUserNo).ThenByDescending(m => m.ValidFrom).Take(1000).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = rows.Select(r => r.EmploymentId).Distinct().ToList();
        var numbers = await db.Set<Employment>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.EmployeeNo, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { maps = rows.Select(r => View(r, numbers.GetValueOrDefault(r.EmploymentId))) });
    }

    public async Task<PeopleResult> UnmappedAsync(Guid? companyId, Guid? deviceId, string? status, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(DevicePermissions.UnmappedRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var query = db.Set<UnmappedPunch>().AsNoTracking().Where(p => p.Status == (status ?? "open"));
        if (companyId is { } c)
        {
            query = query.Where(p => p.CompanyId == c);
        }

        if (deviceId is { } d)
        {
            query = query.Where(p => p.DeviceId == d);
        }

        query = await scope.ApplyAsync(query, UnmappedPunchAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var rows = await query.OrderByDescending(p => p.PunchedAt).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            punches = rows.Select(p => new
            {
                id = p.Id,
                companyId = p.CompanyId,
                deviceId = p.DeviceId,
                deviceUserNo = p.DeviceUserNo,
                punchedAt = p.PunchedAt,
                direction = p.Direction,
                receivedAt = p.ReceivedAt,
                status = p.Status,
                rowVersion = p.RowVersion,
            }),
        });
    }

    public async Task<PeopleResult> DiscardAsync(Guid punchId, DiscardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(DevicePermissions.UnmappedDiscard, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<UnmappedPunch>().FirstOrDefaultAsync(p => p.Id == punchId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, UnmappedPunchAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, UnmappedPunchAccess.Scope, "discard", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        if (row.Status != "open")
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Invalid(NotOpen);
        }

        var now = clock.UtcNow;
        row.Status = "discarded";
        row.ResolvedAt = now;
        row.ResolvedBy = actor.UserId;
        EmploymentApplier.Bump(row, now, actor.UserId);
        var failure = await SaveAsync(cancellationToken).ConfigureAwait(false);
        return failure ?? new PeopleResult(PeopleOutcome.Done, Body: new { id = row.Id, status = row.Status, rowVersion = row.RowVersion });
    }

    /// <summary>Marks the removal task of a mapping of one device and tells HR (event <c>hrm.device_user.removal_due</c>); a mapping for all devices has none.</summary>
    private async Task MakeRemovalTaskAsync(DeviceUserMap row, DateTimeOffset now, CancellationToken ct)
    {
        if (row.DeviceId is not { } device || row.RemovalDueAt is not null)
        {
            return;
        }

        row.RemovalDueAt = now;
        var serial = await db.Set<AttendanceDevice>().AsNoTracking().Where(x => x.Id == device).Select(x => x.SerialNo).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        applier.RaiseFor(DeviceEvents.RemovalDue, DeviceEvents.AggregateType, row.Id, now, actor.UserId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["map_id"] = row.Id,
            ["device_id"] = device,
            ["device_serial"] = serial,
            ["device_user_no"] = row.DeviceUserNo,
            ["employee_no"] = await EmployeeNoAsync(row.EmploymentId, ct).ConfigureAwait(false),
            ["company_id"] = row.CompanyId,
            ["link"] = "/time/devices?tab=maps",
        });
    }

    private async Task<string?> EmployeeNoAsync(Guid employmentId, CancellationToken ct) =>
        await db.Set<Employment>().AsNoTracking().Where(e => e.Id == employmentId).Select(e => e.EmployeeNo).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task<PeopleResult?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(NumberInUse);
        }
    }

    private static object View(DeviceUserMap m, string? employeeNo) => new
    {
        id = m.Id,
        companyId = m.CompanyId,
        deviceId = m.DeviceId,
        deviceUserNo = m.DeviceUserNo,
        employmentId = m.EmploymentId,
        employeeNo,
        validFrom = m.ValidFrom,
        validTo = m.ValidTo,
        consentAt = m.ConsentAt,
        removalDueAt = m.RemovalDueAt,
        removalDoneAt = m.RemovalDoneAt,
        rowVersion = m.RowVersion,
    };
}
