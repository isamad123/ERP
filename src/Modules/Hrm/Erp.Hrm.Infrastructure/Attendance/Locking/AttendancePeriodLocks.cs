using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Attendance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>The state of the payroll run that locked a period, as the lock needs it (slice S1.31, CR-095).</summary>
public enum LockingRunState
{
    /// <summary>No such run.</summary>
    Missing,

    /// <summary>The run is still being worked on (created up to approved): it may be cancelled, which reopens the period.</summary>
    Open,

    /// <summary>The run was cancelled before it was locked.</summary>
    Cancelled,

    /// <summary>The run reached locked (or paid, posted, closed): it is final and is never cancelled (FR-XM-05).</summary>
    Finalized,
}

/// <summary>
/// The payroll runs as the attendance lock needs them (slice S1.31 Build item 4, CR-095): the state of the run that locked a period, whether a finalized
/// run uses a pay period (then the period never reopens), and the cancellation of the locking run when its reopening is approved. An HR-internal
/// contract: the payroll run service (S1.35, S1.38) replaces the default <see cref="PayRunTableStates"/>, which reads and writes <c>hrm.pay_runs</c>
/// as the approved data design defines it; until runs exist nothing uses a period, so a lock is always HR's and never reopens.
/// </summary>
public interface IPayRunStates
{
    Task<LockingRunState> StateAsync(HrmDbContext db, Guid runId, CancellationToken cancellationToken);

    Task<bool> FinalizedRunUsesAsync(HrmDbContext db, Guid payPeriodId, CancellationToken cancellationToken);

    /// <summary>Cancels a run that has not reached locked (status cancelled, the reason in its stage log); false when it cannot be cancelled.</summary>
    Task<bool> CancelBeforeLockAsync(HrmDbContext db, Guid runId, string reason, Guid actor, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The default <see cref="IPayRunStates"/> over <c>hrm.pay_runs</c> (statuses of the approved data design): final from <c>locked</c> on.</summary>
internal sealed class PayRunTableStates : IPayRunStates
{
    private static readonly string[] Final = ["locked", "paid", "posted", "closed"];

    public async Task<LockingRunState> StateAsync(HrmDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var status = await db.Set<PayRun>().AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return status switch
        {
            null => LockingRunState.Missing,
            "cancelled" => LockingRunState.Cancelled,
            _ when Final.Contains(status) => LockingRunState.Finalized,
            _ => LockingRunState.Open,
        };
    }

    public Task<bool> FinalizedRunUsesAsync(HrmDbContext db, Guid payPeriodId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Set<PayRun>().AsNoTracking().AnyAsync(r => r.PayPeriodId == payPeriodId && Final.Contains(r.Status), cancellationToken);
    }

    public async Task<bool> CancelBeforeLockAsync(HrmDbContext db, Guid runId, string reason, Guid actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.pay_runs where id = {runId} for update", cancellationToken).ConfigureAwait(false);
        var run = await db.Set<PayRun>().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.Status == "cancelled" || Final.Contains(run.Status))
        {
            return false;
        }

        var log = run.StageLog.RootElement.ValueKind == JsonValueKind.Array ? JsonNode.Parse(run.StageLog.RootElement.GetRawText())!.AsArray() : [];
        log.Add(new JsonObject
        {
            ["stage"] = "cancelled",
            ["at"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            ["by"] = actor.ToString("D"),
            ["reason"] = reason,
        });
        run.StageLog = JsonDocument.Parse(log.ToJsonString());
        run.Status = "cancelled";
        EmploymentApplier.Bump(run, now, actor);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// The attendance period lock (contract <see cref="IAttendancePeriodLock"/>, slice S1.31 Build items 1, 2 and 4; FR-XM-26, FR-HRM-ATT-17, ATT-21). It replaces
/// the reading implementation and the test doubles of S1.29 and S1.32. A lock takes the attendance period of the pay period (opened here when no day
/// was processed yet) under a row lock, counts the employee days not processed and the open exceptions (refused unless accepted, and then recorded),
/// stamps the time, kind, who and the run, and publishes <c>hrm.attendance.locked</c> with the employments of the period. From then on the database
/// refuses changes to the period's day results, counted leave days, leave usage and encashment, payable overtime, penalties and approved
/// regularizations (rules.yaml, S1.31). A reopen follows only the cancellation of the locking run before it was locked (CR-095).
/// </summary>
internal sealed class AttendancePeriodLocks(HrmDbContext db, IServiceProvider services, IClock clock) : IAttendancePeriodLock
{
    public const string NoPayPeriod = "errors.hrm.attendance.lock.no_pay_period";
    public const string RunUnknown = "errors.hrm.attendance.lock.run_unknown";
    public const string OpenItems = "errors.hrm.attendance.lock.open_items";
    public const string NotLocked = "errors.hrm.attendance.lock.not_locked";
    public const string UseAdjustment = "errors.hrm.attendance.lock.use_adjustment";
    public const string RunStillOpen = "errors.hrm.attendance.lock.run_open";
    public const string RunFinal = "errors.hrm.attendance.lock.run_final";
    public const string OwnApproval = "errors.hrm.attendance.lock.own_approval";
    public const string ReasonRequired = "errors.hrm.attendance.lock.reason_required";

    public async Task<bool> IsLockedAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken)
    {
        var date = day;
        var payGroup = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employmentId && a.ValidFrom <= date && (a.ValidTo == null || a.ValidTo > date))
            .OrderByDescending(a => a.ValidFrom).Select(a => a.PayGroupId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return payGroup is { } group && await db.Set<AttendancePeriod>().AsNoTracking()
            .AnyAsync(p => p.PayGroupId == group && p.Status == "locked" && p.PeriodStart <= date && p.PeriodEnd >= date, cancellationToken).ConfigureAwait(false);
    }

    public Task<PeriodLockResult> LockAsync(PeriodLockRequest request, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OnTransactionAsync(transaction, on => LockOnAsync(on, request, cancellationToken), cancellationToken);
    }

    public Task<PeriodLockResult> ReopenAsync(Guid payPeriodId, string reason, PeriodReopenApproval approval, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return OnTransactionAsync(transaction, on => ReopenOnAsync(on, payPeriodId, reason, approval, cancellationToken), cancellationToken);
    }

    private async Task<PeriodLockResult> LockOnAsync(HrmDbContext on, PeriodLockRequest request, CancellationToken ct)
    {
        var payPeriod = await on.Set<PayPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PayPeriodId, ct).ConfigureAwait(false);
        if (payPeriod is null)
        {
            return new PeriodLockResult(PeriodLockOutcome.Refused, null, NoPayPeriod);
        }

        if (request.RunId is { } run && !await on.Set<PayRun>().AsNoTracking().AnyAsync(r => r.Id == run && r.PayPeriodId == payPeriod.Id && r.Status != "cancelled", ct).ConfigureAwait(false))
        {
            return new PeriodLockResult(PeriodLockOutcome.Refused, null, RunUnknown);
        }

        var now = clock.UtcNow;
        var period = await AttendancePeriodQueries.TakeAsync(on, payPeriod, now, request.LockedBy, ct).ConfigureAwait(false);
        if (period.Status == "locked")
        {
            return new PeriodLockResult(PeriodLockOutcome.AlreadyLocked, period.Id, UnprocessedDays: period.UnprocessedAccepted ?? 0, OpenExceptions: period.ExceptionsAccepted ?? 0);
        }

        var (unprocessed, exceptions) = await AttendancePeriodQueries.OpenItemsAsync(on, period, ct).ConfigureAwait(false);
        if ((unprocessed > 0 || exceptions > 0) && !request.AcceptOpenItems)
        {
            return new PeriodLockResult(PeriodLockOutcome.Refused, period.Id, OpenItems, unprocessed, exceptions);
        }

        period.Status = "locked";
        period.LockedAt = now;
        period.LockKind = request.RunId is null ? "manual" : "payroll";
        period.LockedBy = request.LockedBy;
        period.LockedByRunId = request.RunId;
        period.LockReason = request.Reason ?? (request.RunId is null ? period.LockReason : null);
        period.UnprocessedAccepted = unprocessed > 0 ? unprocessed : null;
        period.ExceptionsAccepted = exceptions > 0 ? exceptions : null;
        period.LockWorkflowInstanceId = request.WorkflowInstanceId ?? period.LockWorkflowInstanceId;
        EmploymentApplier.Bump(period, now, request.LockedBy);
        await on.SaveChangesAsync(ct).ConfigureAwait(false);
        var employments = await AttendancePeriodQueries.EmploymentsAsync(on, period, ct).ConfigureAwait(false);
        AttendancePeriodQueries.Publish(on, AttendanceLockEvents.Locked, period, now, request.LockedBy, new Dictionary<string, object?>
        {
            ["lockKind"] = period.LockKind,
            ["runId"] = period.LockedByRunId,
            ["lockedBy"] = period.LockedBy,
            ["unprocessedAccepted"] = period.UnprocessedAccepted,
            ["exceptionsAccepted"] = period.ExceptionsAccepted,
            ["employmentIds"] = employments,
        });
        await on.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PeriodLockResult(PeriodLockOutcome.Locked, period.Id, null, unprocessed, exceptions);
    }

    private async Task<PeriodLockResult> ReopenOnAsync(HrmDbContext on, Guid payPeriodId, string reason, PeriodReopenApproval approval, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new PeriodLockResult(PeriodLockOutcome.Refused, null, ReasonRequired);
        }

        var id = await on.Set<AttendancePeriod>().AsNoTracking().Where(p => p.PayPeriodId == payPeriodId).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (id is null)
        {
            return new PeriodLockResult(PeriodLockOutcome.Refused, null, NotLocked);
        }

        await on.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.attendance_periods where id = {id.Value} for update", ct).ConfigureAwait(false);
        var period = await on.Set<AttendancePeriod>().FirstAsync(p => p.Id == id.Value, ct).ConfigureAwait(false);
        var refused = await ReopenRefusalAsync(on, period, approval, ct).ConfigureAwait(false);
        if (refused is not null)
        {
            return new PeriodLockResult(PeriodLockOutcome.Refused, period.Id, refused);
        }

        var runs = services.GetRequiredService<IPayRunStates>();
        if (await runs.StateAsync(on, period.LockedByRunId!.Value, ct).ConfigureAwait(false) != LockingRunState.Cancelled)
        {
            return new PeriodLockResult(PeriodLockOutcome.Refused, period.Id, RunStillOpen); // reopening follows the cancellation of the run (CR-095)
        }

        var now = clock.UtcNow;
        var run = period.LockedByRunId;
        var lockedAt = period.LockedAt;
        period.Status = "open";
        period.LockedAt = null;
        period.LockKind = null;
        period.LockedBy = null;
        period.LockedByRunId = null;
        period.UnprocessedAccepted = null;
        period.ExceptionsAccepted = null;
        period.ReopenedAt = now;
        period.ReopenReason = reason.Trim();
        period.ReopenRequestedBy = approval.RequestedBy;
        period.ReopenWorkflowInstanceId = approval.WorkflowInstanceId ?? period.ReopenWorkflowInstanceId;
        EmploymentApplier.Bump(period, now, approval.ApprovedBy);
        await on.SaveChangesAsync(ct).ConfigureAwait(false);
        var employments = await AttendancePeriodQueries.EmploymentsAsync(on, period, ct).ConfigureAwait(false);
        AttendancePeriodQueries.Publish(on, AttendanceLockEvents.Reopened, period, now, approval.ApprovedBy, new Dictionary<string, object?>
        {
            ["runId"] = run,
            ["reason"] = period.ReopenReason,
            ["requestedBy"] = approval.RequestedBy,
            ["approvedBy"] = approval.ApprovedBy,
            ["employmentIds"] = employments,
        });

        // Punches that arrived while the period was locked are worked out now (ruling Q5: they were kept as events and never reprocessed while locked).
        foreach (var employment in await AttendancePeriodQueries.LateEvents(on, period.PeriodStart, period.PeriodEnd, period.PayGroupId, lockedAt).Select(e => e.EmploymentId).Distinct().ToListAsync(ct).ConfigureAwait(false))
        {
            AttendanceReprocessing.Add(on, employment, period.PeriodStart, period.PeriodEnd, "period_reopened", now, approval.ApprovedBy);
        }

        await on.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PeriodLockResult(PeriodLockOutcome.Reopened, period.Id);
    }

    /// <summary>Why a locked period may not reopen (null when it may, once its run is cancelled): not locked, HR's lock, a final run, or the requester's own approval.</summary>
    internal async Task<string?> ReopenRefusalAsync(HrmDbContext on, AttendancePeriod period, PeriodReopenApproval? approval, CancellationToken ct)
    {
        if (period.Status != "locked")
        {
            return NotLocked;
        }

        if (period.LockedByRunId is not { } run)
        {
            return UseAdjustment; // HR's lock never reopens (CR-095): a later change is a retroactive adjustment
        }

        if (approval is not null && approval.RequestedBy == approval.ApprovedBy)
        {
            return OwnApproval; // FR-XM-32
        }

        var runs = services.GetRequiredService<IPayRunStates>();
        return await runs.StateAsync(on, run, ct).ConfigureAwait(false) == LockingRunState.Finalized || await runs.FinalizedRunUsesAsync(on, period.PayPeriodId, ct).ConfigureAwait(false)
            ? RunFinal
            : null;
    }

    private async Task<PeriodLockResult> OnTransactionAsync(DbTransaction? transaction, Func<HrmDbContext, Task<PeriodLockResult>> work, CancellationToken ct)
    {
        if (transaction is not null)
        {
            await using var on = await AttendanceEngine.OnAsync(services, transaction.Connection!, transaction, ct).ConfigureAwait(false);
            return await work(on).ConfigureAwait(false);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var result = await work(db).ConfigureAwait(false);
            if (result.Outcome is PeriodLockOutcome.Locked or PeriodLockOutcome.Reopened)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            db.ChangeTracker.Clear();
            return result;
        }).ConfigureAwait(false);
    }
}

/// <summary>Queries shared by the lock, the console and the adjustments (slice S1.31).</summary>
internal static class AttendancePeriodQueries
{
    /// <summary>
    /// The attendance period of a pay period, under a row lock until the transaction ends: opened (status open, the pay period's dates) when no day was
    /// processed yet, under the same advisory lock the engine takes to open one (S1.29a), and saved through the context so the change log records it.
    /// </summary>
    public static async Task<AttendancePeriod> TakeAsync(HrmDbContext db, PayPeriod payPeriod, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var id = await db.Set<AttendancePeriod>().AsNoTracking().Where(p => p.PayPeriodId == payPeriod.Id).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (id is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.attendance_period:" + payPeriod.Id.ToString("D")}, 0))", ct).ConfigureAwait(false);
            id = await db.Set<AttendancePeriod>().AsNoTracking().Where(p => p.PayPeriodId == payPeriod.Id).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        if (id is null)
        {
            var row = new AttendancePeriod
            {
                Id = Guid.CreateVersion7(now), CompanyId = payPeriod.CompanyId, PayGroupId = payPeriod.PayGroupId, PayPeriodId = payPeriod.Id, PeriodStart = payPeriod.StartDate,
                PeriodEnd = payPeriod.EndDate, Status = "open", CreatedAt = now, CreatedBy = actor, UpdatedAt = now, UpdatedBy = actor,
            };
            db.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.Entry(row).State = EntityState.Detached;
            id = row.Id;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.attendance_periods where id = {id.Value} for update", ct).ConfigureAwait(false);
        return await db.Set<AttendancePeriod>().FirstAsync(p => p.Id == id.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The employee days of the period without a current result (every day an employment is in the pay group between its joining and its leaving) and the
    /// open exceptions of the period's days (FR-HRM-ATT-64: shown at the lock).
    /// </summary>
    public static async Task<(int Unprocessed, int Exceptions)> OpenItemsAsync(HrmDbContext db, AttendancePeriod p, CancellationToken ct)
    {
        var unprocessed = await db.Database.SqlQuery<int>($"""
            select count(*)::int as "Value" from (
                select distinct a.employment_id, d::date as day
                from hrm.employment_assignments a
                join hrm.employments e on e.id = a.employment_id and e.status <> 'cancelled'
                cross join lateral generate_series(greatest(a.valid_from, {p.PeriodStart}, e.joining_date),
                                                   least(coalesce(a.valid_to - 1, {p.PeriodEnd}), {p.PeriodEnd}, coalesce(e.separation_date, {p.PeriodEnd})), interval '1 day') d
                where a.pay_group_id = {p.PayGroupId} and a.valid_from <= {p.PeriodEnd} and (a.valid_to is null or a.valid_to > {p.PeriodStart})
            ) x
            where not exists (select 1 from hrm.attendance_days ad where ad.employment_id = x.employment_id and ad.work_date = x.day and ad.superseded_at is null)
            """).SingleAsync(ct).ConfigureAwait(false);
        var exceptions = await db.Database.SqlQuery<int>($"""
            select count(*)::int as "Value" from hrm.attendance_exceptions x
            where x.status = 'open' and x.work_date between {p.PeriodStart} and {p.PeriodEnd}
              and exists (select 1 from hrm.attendance_days d where d.id = x.attendance_day_id and d.work_date = x.work_date and d.period_id = {p.Id})
            """).SingleAsync(ct).ConfigureAwait(false);
        return (unprocessed, exceptions);
    }

    /// <summary>Every employment of the period: in its pay group at some day of it (not a cancelled hire), or with a day result in it.</summary>
    public static async Task<List<Guid>> EmploymentsAsync(HrmDbContext db, AttendancePeriod p, CancellationToken ct) =>
        await db.Database.SqlQuery<Guid>($"""
            select a.employment_id as "Value" from hrm.employment_assignments a
            join hrm.employments e on e.id = a.employment_id and e.status <> 'cancelled'
            where a.pay_group_id = {p.PayGroupId} and a.valid_from <= {p.PeriodEnd} and (a.valid_to is null or a.valid_to > {p.PeriodStart})
            union
            select d.employment_id from hrm.attendance_days d where d.period_id = {p.Id} and d.work_date between {p.PeriodStart} and {p.PeriodEnd}
            order by 1
            """).ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Punches dated in the period received after it locked (or, for a period reopened since, after its last lock: <paramref name="since"/>) by
    /// employments of its pay group on the punch's date. Nothing when the period never locked.
    /// </summary>
    public static IQueryable<AttendanceEvent> LateEvents(HrmDbContext db, DateOnly start, DateOnly end, Guid payGroupId, DateTimeOffset? since)
    {
        if (since is not { } after)
        {
            return db.Set<AttendanceEvent>().AsNoTracking().Where(_ => false);
        }

        var assignments = db.Set<EmploymentAssignment>().AsNoTracking();
        return db.Set<AttendanceEvent>().AsNoTracking().Where(e => e.EventDate >= start && e.EventDate <= end && e.ReceivedAt > after
            && assignments.Any(a => a.EmploymentId == e.EmploymentId && a.PayGroupId == payGroupId && a.ValidFrom <= e.EventDate && (a.ValidTo == null || a.ValidTo > e.EventDate)));
    }

    /// <summary>
    /// The next open payroll period of a pay group after a date (FR-XM-26): the first pay period starting after it that is not closed and whose attendance
    /// is not locked; null when the calendar has none yet (generate the next periods first).
    /// </summary>
    public static Task<PayPeriod?> NextOpenAsync(HrmDbContext db, Guid payGroupId, DateOnly after, CancellationToken ct)
    {
        var locked = db.Set<AttendancePeriod>().AsNoTracking().Where(a => a.Status == "locked");
        return db.Set<PayPeriod>().AsNoTracking()
            .Where(p => p.PayGroupId == payGroupId && p.StartDate > after && p.Status != "closed" && !locked.Any(a => a.PayPeriodId == p.Id))
            .OrderBy(p => p.StartDate).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The date a posting payroll reads is dated on (FR-XM-26, R-304): <paramref name="date"/> while its attendance period is open, otherwise the first day of the
    /// next open payroll period of the pay group (the posting is never dated inside a lock); <paramref name="date"/> again when the calendar has no open
    /// period yet (the database then refuses the posting and the caller's answer says so).
    /// </summary>
    public static async Task<DateOnly> OpenDateAsync(HrmDbContext db, Guid employmentId, DateOnly date, CancellationToken ct)
    {
        var period = await AdjustmentPlanner.LockedPeriodOfAsync(db, employmentId, date, ct).ConfigureAwait(false);
        if (period is not { Status: "locked" })
        {
            return date;
        }

        return (await NextOpenAsync(db, period.PayGroupId, period.PeriodEnd, ct).ConfigureAwait(false))?.StartDate ?? date;
    }

    /// <summary>Writes a lock event to the HR outbox with the period's facts and <paramref name="extra"/>, saved with the caller's change.</summary>
    public static void Publish(HrmDbContext db, string eventType, AttendancePeriod p, DateTimeOffset now, Guid actor, Dictionary<string, object?> extra)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["periodId"] = p.Id,
            ["payPeriodId"] = p.PayPeriodId,
            ["payGroupId"] = p.PayGroupId,
            ["companyId"] = p.CompanyId,
            ["periodStart"] = p.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["periodEnd"] = p.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in extra)
        {
            payload[key] = value;
        }

        db.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            EventType = eventType,
            AggregateType = AttendanceLockEvents.AggregateType,
            AggregateId = p.Id,
            Payload = JsonSerializer.SerializeToDocument(payload, JsonSerializerOptions.Web),
            Headers = JsonSerializer.SerializeToDocument(new { actor }, JsonSerializerOptions.Web),
        });
    }
}
