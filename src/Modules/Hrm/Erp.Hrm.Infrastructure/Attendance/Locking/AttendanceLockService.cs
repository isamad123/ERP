using System.Globalization;
using System.Text.Json;
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
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// The attendance lock console (slice S1.31 Build items 3, 4 and 7; FR-HRM-ATT-17, ATT-21, ATT-64, FR-XM-26, FR-XM-32). Lists the pay periods of the pay
/// groups in the caller's companies with their attendance state (open, lock asked for, locked by payroll or HR, reopen asked for), who locked and why,
/// what the lock accepted, the open adjustments and the punches that arrived after the lock. HR asks for a lock once the cut-off date has passed and
/// for the cancellation of the locking run and the reopening of its period; both are decided through the workflow by someone other than the
/// requester (document types <c>hrm.attendance_period_lock</c> and <c>hrm.attendance_period_reopen</c>). An HR lock never reopens.
/// </summary>
internal sealed class AttendanceLockService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier,
    IWorkflowService workflow,
    AttendancePeriodLocks locks) : IAttendanceLockService
{
    public const string BeforeCutOff = "errors.hrm.attendance.lock.before_cut_off";
    public const string AlreadyLocked = "errors.hrm.attendance.lock.already_locked";
    public const string RequestWaiting = "errors.hrm.attendance.lock.request_waiting";
    public const string Changed = "errors.hrm.attendance.lock.changed";

    /// <summary>The most pay periods one list answers (newest first).</summary>
    public const int ListLimit = 60;

    /// <summary>The most late punches one answer lists (the count is always the full number).</summary>
    public const int LateLimit = 200;

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> ListAsync(Guid? companyId, Guid? payGroupId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceLockPermissions.PeriodsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var periods = db.Set<PayPeriod>().AsNoTracking();
        if (companyId is { } c)
        {
            periods = periods.Where(p => p.CompanyId == c);
        }

        if (payGroupId is { } g)
        {
            periods = periods.Where(p => p.PayGroupId == g);
        }

        var companies = await AllowedCompaniesAsync(await periods.Select(p => p.CompanyId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false), "read", cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var latest = new List<PayPeriod>();
        foreach (var company in companies)
        {
            // Periods that have started in the company's time, newest first (the calendar's future periods have nothing to lock yet).
            var today = await applier.TodayAsync(company, now, cancellationToken).ConfigureAwait(false);
            latest.AddRange(await periods.Where(p => p.CompanyId == company && p.StartDate <= today).OrderByDescending(p => p.StartDate).Take(ListLimit).ToListAsync(cancellationToken).ConfigureAwait(false));
        }

        var rows = await RowsAsync([.. latest.OrderByDescending(p => p.StartDate).ThenBy(p => p.PayGroupId).Take(ListLimit)], cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { items = rows });
    }

    public async Task<PeopleResult> GetAsync(Guid payPeriodId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceLockPermissions.PeriodsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var payPeriod = await VisibleAsync(payPeriodId, "read", cancellationToken).ConfigureAwait(false);
        if (payPeriod is null)
        {
            return PeopleResult.NotFound;
        }

        var row = (await RowsAsync([payPeriod], cancellationToken).ConfigureAwait(false))[0];
        var period = await db.Set<AttendancePeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.PayPeriodId == payPeriod.Id, cancellationToken).ConfigureAwait(false);
        var probe = period ?? new AttendancePeriod { Id = Guid.Empty, CompanyId = payPeriod.CompanyId, PayGroupId = payPeriod.PayGroupId, PayPeriodId = payPeriod.Id, PeriodStart = payPeriod.StartDate, PeriodEnd = payPeriod.EndDate, Status = "open" };

        // What a lock would accept now (FR-HRM-ATT-64: unresolved exceptions are shown at the lock); a locked period shows what it accepted.
        var (unprocessed, exceptions) = probe.Status == "locked" ? (probe.UnprocessedAccepted ?? 0, probe.ExceptionsAccepted ?? 0) : await AttendancePeriodQueries.OpenItemsAsync(db, probe, cancellationToken).ConfigureAwait(false);
        var today = await applier.TodayAsync(payPeriod.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var reopen = period is { Status: "locked" } ? await locks.ReopenRefusalAsync(db, period, null, cancellationToken).ConfigureAwait(false) : AttendancePeriodLocks.NotLocked;
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            period = row,
            openItems = new { unprocessedDays = unprocessed, openExceptions = exceptions },
            canRequestLock = period is not { Status: "locked" } && period?.LockWorkflowInstanceId is null && today > payPeriod.CutOffDate,
            lockRefusal = period is { Status: "locked" } ? AlreadyLocked : period?.LockWorkflowInstanceId is not null ? RequestWaiting : today > payPeriod.CutOffDate ? null : BeforeCutOff,
            canRequestReopen = reopen is null && period!.ReopenWorkflowInstanceId is null,
            reopenRefusal = reopen ?? (period!.ReopenWorkflowInstanceId is not null ? RequestWaiting : null),
        });
    }

    public async Task<PeopleResult> RequestLockAsync(Guid payPeriodId, PeriodLockRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceLockPermissions.Lock, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var payPeriod = await VisibleAsync(payPeriodId, "lock", cancellationToken).ConfigureAwait(false);
        if (payPeriod is null)
        {
            return PeopleResult.NotFound;
        }

        var errors = AttendanceLockRules.CheckReason(request.Reason);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        if (await applier.TodayAsync(payPeriod.CompanyId, now, cancellationToken).ConfigureAwait(false) <= payPeriod.CutOffDate)
        {
            return PeopleResult.Conflict(BeforeCutOff, new Dictionary<string, object?> { ["cutOffDate"] = payPeriod.CutOffDate });
        }

        return await StartAsync(payPeriod, AttendanceLockEvents.LockDocumentType, async period =>
        {
            if (period.Status == "locked")
            {
                return (PeopleResult.Conflict(AlreadyLocked), false);
            }

            var (unprocessed, exceptions) = await AttendancePeriodQueries.OpenItemsAsync(db, period, cancellationToken).ConfigureAwait(false);
            if ((unprocessed > 0 || exceptions > 0) && !request.AcceptOpenItems)
            {
                return (PeopleResult.Conflict(AttendancePeriodLocks.OpenItems, new Dictionary<string, object?> { ["unprocessedDays"] = unprocessed, ["openExceptions"] = exceptions }), false);
            }

            var again = period.LockWorkflowInstanceId is not null; // a request sent back is sent again; one still waiting is refused by the workflow
            period.LockReason = request.Reason!.Trim();
            period.LockRequestedBy = actor.UserId;
            period.LockAcceptOpen = request.AcceptOpenItems;
            EmploymentApplier.Bump(period, now, actor.UserId);
            return (null, again);
        }, (period, instance) => period.LockWorkflowInstanceId = instance, p => new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["reason"] = Json(request.Reason!.Trim()),
            ["accept_open_items"] = Json(request.AcceptOpenItems),
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> RequestReopenAsync(Guid payPeriodId, PeriodReopenRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(AttendanceLockPermissions.Reopen, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var payPeriod = await VisibleAsync(payPeriodId, "reopen", cancellationToken).ConfigureAwait(false);
        if (payPeriod is null)
        {
            return PeopleResult.NotFound;
        }

        var errors = AttendanceLockRules.CheckReason(request.Reason);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        return await StartAsync(payPeriod, AttendanceLockEvents.ReopenDocumentType, async period =>
        {
            if (await locks.ReopenRefusalAsync(db, period, null, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return (PeopleResult.Conflict(refused), false);
            }

            var again = period.ReopenWorkflowInstanceId is not null;
            period.ReopenReason = request.Reason!.Trim();
            period.ReopenRequestedBy = actor.UserId;
            EmploymentApplier.Bump(period, now, actor.UserId);
            return (null, again);
        }, (period, instance) => period.ReopenWorkflowInstanceId = instance, p => new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["reason"] = Json(request.Reason!.Trim()),
            ["run_id"] = Json(p.LockedByRunId?.ToString("D")),
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> LateEventsAsync(Guid payPeriodId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(AttendanceLockPermissions.PeriodsRead, cancellationToken).ConfigureAwait(false)
            || !await permissions.HasPermissionAsync("hrm.attendance_events.read", cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var payPeriod = await VisibleAsync(payPeriodId, "read", cancellationToken).ConfigureAwait(false);
        if (payPeriod is null)
        {
            return PeopleResult.NotFound;
        }

        var period = await db.Set<AttendancePeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.PayPeriodId == payPeriod.Id, cancellationToken).ConfigureAwait(false);
        var events = await scope.ApplyAsync(AttendancePeriodQueries.LateEvents(db, payPeriod.StartDate, payPeriod.EndDate, payPeriod.PayGroupId, period is { Status: "locked" } ? period.LockedAt : null),
            AttendanceEventAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var total = await events.CountAsync(cancellationToken).ConfigureAwait(false);
        var employments = db.Set<Employment>().AsNoTracking();
        var items = await events.OrderBy(e => e.ReceivedAt).ThenBy(e => e.Id).Take(LateLimit)
            .Select(e => new
            {
                id = e.Id,
                employmentId = e.EmploymentId,
                employeeNo = employments.Where(x => x.Id == e.EmploymentId).Select(x => x.EmployeeNo).FirstOrDefault(),
                eventDate = e.EventDate,
                eventAt = e.EventAt,
                direction = e.Direction,
                source = e.Source,
                receivedAt = e.ReceivedAt,
            }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { total, lockedAt = period?.LockedAt, items });
    }

    /// <summary>The rows of the list: each pay period with its group, its attendance state and lock record, and its adjustments.</summary>
    private async Task<List<object>> RowsAsync(IReadOnlyList<PayPeriod> payPeriods, CancellationToken ct)
    {
        var ids = payPeriods.Select(p => p.Id).ToList();
        var groupIds = payPeriods.Select(p => p.PayGroupId).Distinct().ToList();
        var groups = await db.Set<PayGroup>().AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct).ConfigureAwait(false);
        var periods = await db.Set<AttendancePeriod>().AsNoTracking().Where(a => ids.Contains(a.PayPeriodId)).ToDictionaryAsync(a => a.PayPeriodId, ct).ConfigureAwait(false);
        var periodIds = periods.Values.Select(p => p.Id).ToList();
        var waiting = await db.Set<AttendanceAdjustment>().AsNoTracking().Where(a => periodIds.Contains(a.PeriodId) && a.Status == "submitted")
            .GroupBy(a => a.PeriodId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        var carried = await db.Set<AttendanceAdjustment>().AsNoTracking().Where(a => ids.Contains(a.TargetPayPeriodId) && a.Status == "approved")
            .GroupBy(a => a.TargetPayPeriodId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        var users = periods.Values.SelectMany(p => new[] { p.LockedBy, p.LockRequestedBy, p.ReopenRequestedBy }).OfType<Guid>().Distinct().ToList();
        var numbers = await db.Set<Employment>().AsNoTracking().Where(e => e.UserRef != null && users.Contains(e.UserRef.Value))
            .OrderBy(e => e.EmployeeNo).Select(e => new { User = e.UserRef!.Value, e.EmployeeNo }).ToListAsync(ct).ConfigureAwait(false);
        string? No(Guid? user) => user is { } u ? numbers.Find(n => n.User == u)?.EmployeeNo : null;

        var rows = new List<object>(payPeriods.Count);
        foreach (var p in payPeriods)
        {
            periods.TryGetValue(p.Id, out var a);
            groups.TryGetValue(p.PayGroupId, out var group);
            var state = a switch
            {
                null => "open",
                { Status: "locked", ReopenWorkflowInstanceId: not null, ReopenRequestedBy: not null } => "reopen_requested",
                { Status: "locked" } => "locked",
                { LockWorkflowInstanceId: not null } => "lock_requested",
                _ => "open",
            };
            rows.Add(new
            {
                payPeriodId = p.Id,
                companyId = p.CompanyId,
                payGroupId = p.PayGroupId,
                payGroupCode = group?.Code,
                payGroupName = group?.NameI18n,
                periodNo = p.PeriodNo,
                taxYear = p.TaxYear,
                startDate = p.StartDate,
                endDate = p.EndDate,
                cutOffDate = p.CutOffDate,
                payStatus = p.Status,
                periodId = a?.Id,
                state,
                lockKind = a?.LockKind,
                lockedAt = a?.LockedAt,
                lockedBy = a?.LockedBy,
                lockedByEmployeeNo = No(a?.LockedBy),
                lockedByRunId = a?.LockedByRunId,
                lockReason = a?.LockReason,
                lockRequestedBy = a?.LockRequestedBy,
                lockRequestedByEmployeeNo = No(a?.LockRequestedBy),
                lockAcceptOpen = a?.LockAcceptOpen,
                lockInstanceId = a?.LockWorkflowInstanceId,
                unprocessedAccepted = a?.UnprocessedAccepted,
                exceptionsAccepted = a?.ExceptionsAccepted,
                reopenInstanceId = a?.ReopenWorkflowInstanceId,
                reopenRequestedBy = a?.ReopenRequestedBy,
                reopenRequestedByEmployeeNo = No(a?.ReopenRequestedBy),
                reopenReason = a?.ReopenReason,
                reopenedAt = a?.ReopenedAt,
                adjustmentsWaiting = a is not null && waiting.TryGetValue(a.Id, out var w) ? w : 0,
                adjustmentsCarriedIn = carried.TryGetValue(p.Id, out var ci) ? ci : 0,
                rowVersion = a?.RowVersion,
            });
        }

        return rows;
    }

    private async Task<List<Guid>> AllowedCompaniesAsync(IEnumerable<Guid> companies, string action, CancellationToken ct)
    {
        var allowed = new List<Guid>();
        foreach (var c in companies)
        {
            if (await scope.AllowsAsync(new AttendancePeriod { CompanyId = c }, AttendancePeriodAccess.Scope, action, Source, ct).ConfigureAwait(false))
            {
                allowed.Add(c);
            }
        }

        return allowed;
    }

    /// <summary>The pay period, by its own id or (the workflow links name the document) by its attendance period's id, in the caller's companies for the action.</summary>
    private async Task<PayPeriod?> VisibleAsync(Guid payPeriodId, string action, CancellationToken ct)
    {
        var p = await db.Set<PayPeriod>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == payPeriodId, ct).ConfigureAwait(false);
        if (p is null && await db.Set<AttendancePeriod>().AsNoTracking().Where(a => a.Id == payPeriodId).Select(a => (Guid?)a.PayPeriodId).FirstOrDefaultAsync(ct).ConfigureAwait(false) is { } byAttendance)
        {
            p = await db.Set<PayPeriod>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == byAttendance, ct).ConfigureAwait(false);
        }

        return p is not null && (await AllowedCompaniesAsync([p.CompanyId], action, ct).ConfigureAwait(false)).Count == 1 ? p : null;
    }

    /// <summary>
    /// In one transaction: the attendance period taken under its row lock (opened when no day was processed yet), <paramref name="prepare"/> checks and
    /// records the request (or refuses), the workflow starts (or a returned request is sent again) and the period keeps the instance. 202 with the instance.
    /// </summary>
    private async Task<PeopleResult> StartAsync(
        PayPeriod payPeriod, string documentType, Func<AttendancePeriod, Task<(PeopleResult? Refused, bool Resubmit)>> prepare, Action<AttendancePeriod, Guid> attach,
        Func<AttendancePeriod, Dictionary<string, JsonElement>> extra, CancellationToken ct)
    {
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            PeopleResult? result = null;
            await strategy.ExecuteAsync(async () =>
            {
                result = null;
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var now = clock.UtcNow;
                var period = await AttendancePeriodQueries.TakeAsync(db, payPeriod, now, actor.UserId, ct).ConfigureAwait(false);
                var (refused, resubmit) = await prepare(period).ConfigureAwait(false);
                if (refused is not null)
                {
                    result = refused;
                    return;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                var group = await db.Set<PayGroup>().AsNoTracking().FirstAsync(g => g.Id == payPeriod.PayGroupId, ct).ConfigureAwait(false);
                var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["company"] = Json(payPeriod.CompanyId.ToString()),
                    ["pay_group"] = Json(group.Code),
                    ["period_start"] = Json(Iso(payPeriod.StartDate)),
                    ["period_end"] = Json(Iso(payPeriod.EndDate)),
                };
                foreach (var (key, value) in extra(period))
                {
                    values[key] = value;
                }

                var dbTx = db.Database.CurrentTransaction!.GetDbTransaction();
                var started = resubmit
                    ? await workflow.ResubmitAsync(documentType, period.Id, values, dbTx, ct).ConfigureAwait(false)
                    : await workflow.StartAsync(new WorkflowStartRequest(documentType, period.Id, payPeriod.CompanyId, [], values), dbTx, ct).ConfigureAwait(false);
                attach(period, started.InstanceId);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                result = new PeopleResult(PeopleOutcome.Pending, Body: new { payPeriodId = payPeriod.Id, periodId = period.Id, instanceId = started.InstanceId, status = started.Status });
            }).ConfigureAwait(false);
            return result!;
        }
        catch (WorkflowException ex) when (ex.ErrorKey is "errors.workflow.not_returned" or "errors.workflow.request_open")
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(RequestWaiting);
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(ex.ErrorKey, ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(Changed);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return PersonService.Refusal(pg);
        }
    }

    internal static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    internal static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
