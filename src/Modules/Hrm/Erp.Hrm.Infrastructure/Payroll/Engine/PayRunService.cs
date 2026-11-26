using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Payroll.Engine;

/// <summary>
/// Payroll runs and their calculation (slice S1.35a Build items 2 to 7; FR-HRM-PAY-03, -05, -07, -31, -33, FR-XM-26, GHR-06). A calculation first locks
/// the period's attendance and leave through <see cref="IAttendancePeriodLock"/> (refused when it cannot: days not processed or open exceptions without
/// the flag), then works through the run's employees in batches of <c>hrm.pay.chunk_size</c>: each batch takes its snapshots, calculates from the stored
/// form of each snapshot and saves the employees, results, lines and used inputs together, so a stop loses at most one batch and a later call goes on
/// with the employees that have no result (S1.35b turns this into the chunked resumable job). An employee the engine cannot complete (a statutory
/// component without its calculator, a missing table, a formula that cannot be worked out) gets no result and is listed with the reasons; the run
/// stays uncalculated until every employee has one, then it becomes <c>calculated</c> and <c>hrm.pay_run.calculated</c> is published.
/// </summary>
internal sealed class PayRunService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ISettings settings,
    INumberAllocator numbers,
    IAttendancePeriodLock locks,
    PaySnapshotBuilder builder,
    EmploymentApplier applier,
    IJobRequests jobs,
    ISensitiveAccessLog accessLog,
    IEnumerable<IStatutoryCalculator> calculators) : IPayRunService
{
    public const string NumberType = "hrm.pay_run_no";
    public const string RunExists = "errors.hrm.pay.run.exists";
    public const string PeriodClosed = "errors.hrm.pay.run.period_closed";
    public const string PeriodOfOtherGroup = "errors.hrm.pay.run.period_other_group";
    public const string GroupInactive = "errors.hrm.pay.group_inactive";
    public const string NotCalculable = "errors.hrm.pay.run.not_calculable";
    public const string LockRefused = "errors.hrm.pay.run.lock_refused";
    public const string EmployeeConflict = "errors.hrm.pay.run.employee_in_other_run";
    public const string BeforeCutOff = "errors.hrm.pay.run.before_cut_off";
    public const string InputsChanged = "errors.hrm.pay.run.inputs_changed";
    public const string Calculating = "errors.hrm.pay.run.calculating";
    public const string NotCalculating = "errors.hrm.pay.run.not_calculating";
    public const string NotInRun = "errors.hrm.pay.run.employment_not_in_run";
    public const string TraceSalaryClass = "errors.hrm.pay.trace.salary_class";

    /// <summary>The most incomplete employees the summary explains (the rest are counted).</summary>
    private const int IncompleteShown = 50;

    private static readonly string[] Calculable = PayCalculator.Calculable;

    /// <summary>A recalculation may also follow a finished calculation, until the run moves on (validation and later are S1.38's; locked is never).</summary>
    private static readonly string[] Recalculable = ["created", "inputs_imported", "calculated"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> CreateAsync(PayRunCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayRunPermissions.Run, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.PayGroupId is null)
        {
            errors["payGroupId"] = [PersonService.Required];
        }

        if (request.PayPeriodId is null)
        {
            errors["payPeriodId"] = [PersonService.Required];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var group = await db.Set<PayGroup>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == request.PayGroupId && !g.IsDeleted, cancellationToken).ConfigureAwait(false);
        var probe = group is null ? null : new PayRun { CompanyId = group.CompanyId, RunNo = string.Empty, Kind = "regular", CurrencyCode = group.CurrencyCode, Status = "created", StageLog = JsonDocument.Parse("[]") };
        if (group is null || !await scope.AllowsAsync(probe!, PayRunAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["payGroupId"] = ["errors.hrm.pay.run.group_not_found"] });
        }

        if (!group.IsActive)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["payGroupId"] = [GroupInactive] });
        }

        var period = await db.Set<PayPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PayPeriodId, cancellationToken).ConfigureAwait(false);
        if (period is null || period.PayGroupId != group.Id)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["payPeriodId"] = [period is null ? "errors.data.not_found" : PeriodOfOtherGroup] });
        }

        if (period.Status == "closed")
        {
            return PeopleResult.Conflict(PeriodClosed);
        }

        var now = clock.UtcNow;
        var user = actor.UserId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var existing = await db.Set<PayRun>().AsNoTracking()
                .Where(r => r.PayGroupId == group.Id && r.PayPeriodId == period.Id && r.Kind == "regular" && r.Status != "cancelled").Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (existing is { } other)
            {
                return PeopleResult.Conflict(RunExists, new Dictionary<string, object?> { ["runId"] = other });
            }

            var run = new PayRun
            {
                Id = Guid.CreateVersion7(now),
                CompanyId = group.CompanyId,
                RunNo = await numbers.AssignAsync(new NumberRequest(NumberType, group.CompanyId, null, period.EndDate), tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false),
                PayGroupId = group.Id,
                PayPeriodId = period.Id,
                Kind = "regular",
                CurrencyCode = group.CurrencyCode,
                StageLog = PayCalculator.Stage(null, "created", now, user),
                Status = "created",
                CreatedAt = now,
                CreatedBy = user,
                UpdatedAt = now,
                UpdatedBy = user,
            };
            db.Add(run);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Created, Body: RunView(run, null));
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return PeopleResult.Conflict(RunExists);
        }
    }

    public async Task<PeopleResult> CalculateAsync(Guid runId, PayRunCalculateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (run, refusal, lockView) = await PrepareAsync(runId, request.AcceptOpenItems, Calculable, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        // A normal calculation takes fresh snapshots of every employee without a result; those left incomplete before are tried again.
        return await QueueAsync(run!, lockView!, request.AcceptOpenItems, fresh: true, recalculate: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> RecalculateAsync(Guid runId, PayRunRecalculateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (run, refusal, lockView) = await PrepareAsync(runId, request.AcceptOpenItems, Recalculable, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var listed = (request.EmploymentIds ?? []).Distinct().ToList();
        if (listed.Count > 0)
        {
            var known = await db.Set<PayRunEmployee>().AsNoTracking().Where(e => e.PayRunId == runId && listed.Contains(e.EmploymentId)).Select(e => e.EmploymentId).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (known.Count != listed.Count)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["employmentIds"] = [NotInRun] });
            }
        }

        return await QueueAsync(run!, lockView!, request.AcceptOpenItems, request.FreshSnapshot, listed, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> CancelCalculationAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayRunPermissions.Run, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var run = await db.Set<PayRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !await scope.AllowsAsync(run, PayRunAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var last = LastRequest(run);
        var active = last is null ? [] : (await jobs.StatesAsync(last.Value.JobRunIds, cancellationToken).ConfigureAwait(false)).Where(j => j.Status is "queued" or "running").Select(j => j.Id).ToList();
        if (active.Count == 0)
        {
            return PeopleResult.Conflict(NotCalculating);
        }

        var user = actor.UserId;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var asked = await jobs.CancelAsync(active, user, tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.pay_runs where id = {runId} for update", cancellationToken).ConfigureAwait(false);
        var tracked = await db.Set<PayRun>().FirstAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        tracked.StageLog = PayCalculator.Stage(tracked.StageLog, "calculation_cancel_requested", now, user, new Dictionary<string, object?> { ["request"] = last!.Value.RequestId.ToString(), ["jobRunIds"] = active });
        EmploymentApplier.Bump(tracked, now, user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { id = runId, requestId = last.Value.RequestId, cancelled = asked });
    }

    /// <summary>Permission, scope, stage, cut-off and the attendance lock (FR-XM-26) before a calculation or a recalculation is queued.</summary>
    private async Task<(PayRun? Run, PeopleResult? Refusal, Dictionary<string, object?>? Lock)> PrepareAsync(Guid runId, bool acceptOpenItems, string[] statuses, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(PayRunPermissions.Run, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.Forbidden(), null);
        }

        var run = await db.Set<PayRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (run is null || !await scope.AllowsAsync(run, PayRunAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound, null);
        }

        if (run.Kind != "regular" || !statuses.Contains(run.Status))
        {
            return (null, PeopleResult.Conflict(NotCalculable, new Dictionary<string, object?> { ["status"] = run.Status }), null);
        }

        // The lock freezes the period's days, so it waits for the cut-off date as HR's lock does (S1.31; review S1.35a finding 2).
        var cutOff = await db.Set<PayPeriod>().AsNoTracking().Where(p => p.Id == run.PayPeriodId).Select(p => p.CutOffDate).FirstAsync(ct).ConfigureAwait(false);
        if (await applier.TodayAsync(run.CompanyId, clock.UtcNow, ct).ConfigureAwait(false) <= cutOff)
        {
            return (null, PeopleResult.Conflict(BeforeCutOff, new Dictionary<string, object?> { ["cutOffDate"] = cutOff }), null);
        }

        // FR-XM-26: the period's attendance and leave lock first; nothing is queued when they cannot (a recalculation finds them locked already).
        var locked = await locks.LockAsync(new PeriodLockRequest(run.PayPeriodId, run.Id, acceptOpenItems, actor.UserId), null, ct).ConfigureAwait(false);
        var lockView = new Dictionary<string, object?>
        {
            ["outcome"] = locked.Outcome.ToString(),
            ["attendancePeriodId"] = locked.PeriodId,
            ["unprocessedDays"] = locked.UnprocessedDays,
            ["openExceptions"] = locked.OpenExceptions,
        };
        if (locked.Outcome == PeriodLockOutcome.Refused)
        {
            return (null, PeopleResult.Conflict(locked.ErrorKey ?? LockRefused, new Dictionary<string, object?> { ["lock"] = lockView }), null);
        }

        return (run, null, lockView);
    }

    /// <summary>
    /// Queues the lanes of one calculation request with the run row held: refused while another request of the run is still queued or running; marks
    /// the employees to calculate (those listed by a recalculation, and every employee left incomplete before) as <c>included</c>; freezes the rounding
    /// mode and the currency's decimals for the request; records the request (who, when, its job runs) in the stage log.
    /// </summary>
    private async Task<PeopleResult> QueueAsync(PayRun probe, Dictionary<string, object?> lockView, bool acceptOpenItems, bool fresh, List<Guid>? recalculate, CancellationToken ct)
    {
        var last = LastRequest(probe);
        if (last is not null)
        {
            var running = (await jobs.StatesAsync(last.Value.JobRunIds, ct).ConfigureAwait(false)).Where(j => j.Status is "queued" or "running").Select(j => j.Id).ToList();
            if (running.Count > 0)
            {
                return PeopleResult.Conflict(Calculating, new Dictionary<string, object?> { ["requestId"] = last.Value.RequestId, ["jobRunIds"] = running });
            }
        }

        // The rounding mode and the currency's minor unit are frozen for the whole request (review S1.35a finding 11); the engine rounds lines to 0 to 4 places.
        var read = await builder.ContextAsync(probe, ct).ConfigureAwait(false);
        var ctx = read with { CurrencyDecimals = Math.Clamp(read.CurrencyDecimals, 0, 4) };
        var eligible = await builder.EligibleAsync(ctx, ct).ConfigureAwait(false);
        var chunkSize = await IntSettingAsync(HrmSettingKeys.HrmPayChunkSize, probe.CompanyId, 200, ct).ConfigureAwait(false);
        var maxLanes = Math.Clamp(await IntSettingAsync(HrmSettingKeys.HrmPayMaxParallelChunks, probe.CompanyId, 2, ct).ConfigureAwait(false), 1, 8);
        var user = actor.UserId;
        var now = clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.pay_runs where id = {probe.Id} for update", ct).ConfigureAwait(false);
        var run = await db.Set<PayRun>().FirstAsync(r => r.Id == probe.Id, ct).ConfigureAwait(false);
        var statuses = recalculate is null ? Calculable : Recalculable;
        if (!statuses.Contains(run.Status) || run.RowVersion != probe.RowVersion)
        {
            return PeopleResult.Conflict(run.RowVersion != probe.RowVersion ? Calculating : NotCalculable, new Dictionary<string, object?> { ["status"] = run.Status });
        }

        // The employees this request calculates are marked included: a recalculation's (all of the run's when none is listed) and every incomplete one.
        var rows = await db.Set<PayRunEmployee>().Where(e => e.PayRunId == run.Id).ToListAsync(ct).ConfigureAwait(false);
        var marked = 0;
        foreach (var row in rows.Where(r => r.Status == "error" || (recalculate is not null && (recalculate.Count == 0 ? r.Status == "calculated" : recalculate.Contains(r.EmploymentId) && r.Status == "calculated"))))
        {
            row.Status = "included";
            EmploymentApplier.Bump(row, now, user);
            marked++;
        }

        var todo = eligible.Count(id => rows.FirstOrDefault(r => r.EmploymentId == id) is not { Status: "calculated" or "excluded" or "held" });
        var lanes = Math.Max(1, Math.Min(maxLanes, (todo + chunkSize - 1) / chunkSize));
        var requestId = Guid.CreateVersion7(now);
        var jobRunIds = new List<Guid>();
        for (var lane = 0; lane < lanes; lane++)
        {
            var parameters = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["pay_run_id"] = run.Id.ToString(),
                ["request_id"] = requestId.ToString(),
                ["lane"] = lane,
                ["lanes"] = lanes,
                ["fresh"] = fresh,
                ["rounding_mode"] = ctx.RoundingMode,
                ["currency_decimals"] = ctx.CurrencyDecimals,
            });
            jobRunIds.Add(await jobs.QueueAsync(new JobRequest(PayrollCalculateJob.JobCode, run.CompanyId, parameters, user), tx.GetDbTransaction(), ct).ConfigureAwait(false));
        }

        var stage = recalculate is null ? "calculation_requested" : "recalculation_requested";
        if (run.Status == "calculated")
        {
            // A recalculated run is not calculated until every employee has a result again; its totals wait for that.
            run.Status = "created";
            run.TotalGross = null;
            run.TotalNet = null;
            run.TotalEmployer = null;
            run.EmployeeCount = null;
            foreach (var row in rows)
            {
                row.RunStatus = "created";
                EmploymentApplier.Bump(row, now, user);
            }
        }

        run.CalcJobRunId = jobRunIds[0];
        run.StageLog = PayCalculator.Stage(run.StageLog, stage, now, user, new Dictionary<string, object?>
        {
            ["request"] = requestId.ToString(),
            ["jobRunIds"] = jobRunIds,
            ["lanes"] = lanes,
            ["employees"] = eligible.Count,
            ["toCalculate"] = todo,
            ["marked"] = marked,
            ["fresh"] = fresh,
            ["acceptOpenItems"] = acceptOpenItems,
            ["roundingMode"] = ctx.RoundingMode,
            ["currencyDecimals"] = ctx.CurrencyDecimals,
            ["engineVersion"] = PayEngine.Version,
        });
        EmploymentApplier.Bump(run, now, user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Pending, Body: new
        {
            id = run.Id,
            runNo = run.RunNo,
            status = run.Status,
            requestId,
            jobRunIds,
            lanes,
            employees = eligible.Count,
            toCalculate = todo,
            engineVersion = PayEngine.Version,
            @lock = lockView,
        });
    }

    /// <summary>The run's last calculation request in the stage log: its id and job runs.</summary>
    private static (Guid RequestId, List<Guid> JobRunIds, JsonObject Entry)? LastRequest(PayRun run)
    {
        var entry = PayCalculator.Entries(run.StageLog).LastOrDefault(e => e["stage"]?.GetValue<string>() is "calculation_requested" or "recalculation_requested");
        if (entry is null || !Guid.TryParse(entry["request"]?.GetValue<string>(), out var id))
        {
            return null;
        }

        var ids = (entry["jobRunIds"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.GetValue<string>(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        return (id, ids, entry);
    }

    private async Task<int> IntSettingAsync(string key, Guid companyId, int fallback, CancellationToken ct)
    {
        var value = (await settings.GetAsync(key, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0 ? n : fallback;
    }

    public async Task<PeopleResult> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayRunPermissions.RunsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var run = await db.Set<PayRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !await scope.AllowsAsync(run, PayRunAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var plan = await fields.PlanAsync(PayRunAccess.Fields, PayRunAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var access = (await plan.ResolveAsync([run], Source, cancellationToken).ConfigureAwait(false))[0];
        var counts = await db.Set<PayRunEmployee>().AsNoTracking().Where(e => e.PayRunId == runId).GroupBy(e => e.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var view = RunView(run, access);
        var byStatus = counts.ToDictionary(c => c.Key, c => c.Count, StringComparer.Ordinal);
        view["employees"] = byStatus;

        // Ruling Q2: why employees are incomplete, worked out again from their stored snapshots (keys and component codes only, never an amount).
        if (byStatus.GetValueOrDefault("error") > 0)
        {
            var registered = calculators.OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
            var failed = await db.Set<PayRunEmployee>().AsNoTracking().Where(e => e.PayRunId == runId && e.Status == "error").OrderBy(e => e.EmploymentId).Take(IncompleteShown)
                .Select(e => new { e.EmploymentId, e.Snapshot }).ToListAsync(cancellationToken).ConfigureAwait(false);
            view["incomplete"] = failed.Select(f => new
            {
                employmentId = f.EmploymentId,
                problems = PayEngine.Calculate(PaySnapshotCodec.Read(PaySnapshotCodec.Unpack(f.Snapshot)), registered).Problems.Select(p => new { key = p.Key, subject = p.Subject }).ToList(),
            }).ToList();
        }

        // FR-HRM-PAY-34: the progress of the last calculation: its lanes (job runs) with their state, and the employees done of those it had to do.
        if (LastRequest(run) is { } last)
        {
            var states = await jobs.StatesAsync(last.JobRunIds, cancellationToken).ConfigureAwait(false);
            var employees = last.Entry["employees"]?.GetValue<int>() ?? 0;
            var calculated = byStatus.GetValueOrDefault("calculated");
            var incomplete = byStatus.GetValueOrDefault("error");
            var waiting = Math.Max(0, employees - calculated - incomplete - byStatus.GetValueOrDefault("excluded") - byStatus.GetValueOrDefault("held"));
            view["calculation"] = new Dictionary<string, object?>
            {
                ["requestId"] = last.RequestId,
                ["stage"] = last.Entry["stage"]?.GetValue<string>(),
                ["requestedAt"] = last.Entry["at"]?.GetValue<string>(),
                ["requestedBy"] = last.Entry["by"]?.GetValue<string>(),
                ["fresh"] = last.Entry["fresh"]?.GetValue<bool>(),
                ["lanes"] = states.Select(j => new { jobRunId = j.Id, status = j.Status, progressPct = j.ProgressPct, attempt = j.Attempt, error = j.Error }).ToList(),
                ["running"] = states.Any(j => j.Status is "queued" or "running"),
                ["employees"] = employees,
                ["calculated"] = calculated,
                ["incomplete"] = incomplete,
                ["waiting"] = waiting,
                ["percent"] = employees == 0 ? 100m : Math.Round((employees - waiting) * 100m / employees, 1),
            };
        }

        return new PeopleResult(PeopleOutcome.Done, Body: view);
    }

    public async Task<PeopleResult> TraceAsync(Guid resultId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayRunPermissions.TraceRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var query = await scope.ApplyAsync(db.Set<PayResult>().AsNoTracking().Where(r => r.Id == resultId), PayResultAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var result = await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return PeopleResult.NotFound;
        }

        // The trace holds the employee's amounts and salary inputs: it is shown only with the salary field class on the result (GHR-06).
        var plan = await fields.PlanAsync(PayResultAccess.Fields, PayResultAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var access = (await plan.ResolveAsync([result], Source, cancellationToken).ConfigureAwait(false))[0];
        if (access.Of("gross") < FieldAccessLevel.ReadOnly)
        {
            return PeopleResult.Forbidden(TraceSalaryClass);
        }

        var trace = await db.Set<PayTrace>().AsNoTracking().FirstOrDefaultAsync(t => t.ResultId == resultId, cancellationToken).ConfigureAwait(false);
        if (trace is null)
        {
            return PeopleResult.NotFound;
        }

        await accessLog.RecordAsync("hrm.pay_traces", resultId, ["trace"], "view", 1, cancellationToken).ConfigureAwait(false); // before the answer (FR-KRN-AUD-02)
        return new PeopleResult(PeopleOutcome.Done, Body: new Dictionary<string, object?>
        {
            ["resultId"] = result.Id,
            ["payRunId"] = result.PayRunId,
            ["employmentId"] = result.EmploymentId,
            ["engineVersion"] = trace.EngineVersion,
            ["traceHash"] = Convert.ToHexStringLower(PayTraceDocument.Hash(trace.Trace.RootElement)),
            ["trace"] = trace.Trace.RootElement.Clone(),
        });
    }

    public async Task<PeopleResult> ResultsAsync(Guid runId, Guid? employmentId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayRunPermissions.ResultsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var run = await db.Set<PayRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !await scope.AllowsAsync(run, PayRunAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var query = db.Set<PayResult>().AsNoTracking().Where(r => r.PayRunId == runId);
        if (employmentId is { } only)
        {
            query = query.Where(r => r.EmploymentId == only);
        }

        query = await scope.ApplyAsync(query, PayResultAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var results = await query.OrderBy(r => r.EmploymentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var resultIds = results.Select(r => r.Id).ToList();
        var lines = await (from l in db.Set<PayResultLine>().AsNoTracking()
                           join c in db.Set<PayComponent>().AsNoTracking() on l.ComponentId equals c.Id
                           where resultIds.Contains(l.ResultId)
                           orderby c.PayslipOrder, c.Code
                           select new { Line = l, c.Code, c.Kind }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var employmentIds = results.Select(r => r.EmploymentId).ToList();
        var employeeNos = await db.Set<Employment>().AsNoTracking().Where(e => employmentIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.EmployeeNo, cancellationToken).ConfigureAwait(false);

        // Salary-class fields: shown only with the field class, per row under the caller's data scope (GHR-06).
        var resultPlan = await fields.PlanAsync(PayResultAccess.Fields, PayResultAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var resultAccess = await resultPlan.ResolveAsync(results, Source, cancellationToken).ConfigureAwait(false);
        var linePlan = await fields.PlanAsync(PayResultLineAccess.Fields, PayResultLineAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false);
        var lineAccess = await linePlan.ResolveAsync([.. lines.Select(l => l.Line)], Source, cancellationToken).ConfigureAwait(false);
        var items = new List<Dictionary<string, object?>>();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var view = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = r.Id,
                ["employmentId"] = r.EmploymentId,
                ["employeeNo"] = employeeNos.GetValueOrDefault(r.EmploymentId),
                ["payPeriodId"] = r.PayPeriodId,
                ["taxYear"] = r.TaxYear,
                ["workedDays"] = r.WorkedDays,
                ["paidDays"] = r.PaidDays,
                ["correctsResultId"] = r.CorrectsResultId,
            };
            Show(view, resultAccess[i], "gross", "gross", r.Gross);
            Show(view, resultAccess[i], "deductions", "deductions", r.Deductions);
            Show(view, resultAccess[i], "net", "net", r.Net);
            Show(view, resultAccess[i], "employer_cost", "employerCost", r.EmployerCost);
            Show(view, resultAccess[i], "taxable_income_ytd", "taxableIncomeYtd", r.TaxableIncomeYtd);
            Show(view, resultAccess[i], "tax_ytd", "taxYtd", r.TaxYtd);
            view["lines"] = lines.Select((l, j) => (l, j)).Where(x => x.l.Line.ResultId == r.Id).Select(x =>
            {
                var line = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = x.l.Line.Id,
                    ["componentId"] = x.l.Line.ComponentId,
                    ["componentCode"] = x.l.Code,
                    ["kind"] = x.l.Kind,
                    ["units"] = x.l.Line.Units,
                };
                Show(line, lineAccess[x.j], "amount", "amount", x.l.Line.Amount);
                Show(line, lineAccess[x.j], "rate", "rate", x.l.Line.Rate);
                return line;
            }).ToList();
            items.Add(view);
        }

        // FR-KRN-AUD-02: an answer that shows salary amounts is recorded in the access log before it leaves (S1.35b).
        var shown = new List<string>();
        if (resultAccess.Any(a => a.Of("gross") >= FieldAccessLevel.ReadOnly))
        {
            shown.AddRange(["gross", "deductions", "net", "employer_cost", "taxable_income_ytd", "tax_ytd"]);
        }

        if (lineAccess.Any(a => a.Of("amount") >= FieldAccessLevel.ReadOnly))
        {
            shown.AddRange(["lines.amount", "lines.rate"]);
        }

        if (shown.Count > 0 && items.Count > 0)
        {
            await accessLog.RecordAsync("hrm.pay_results", employmentId is null ? null : results[0].Id, shown, "view", items.Count, cancellationToken).ConfigureAwait(false);
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { runId, runNo = run.RunNo, status = run.Status, items });
    }

    private static void Show(Dictionary<string, object?> view, FieldAccessMap access, string column, string name, object? value)
    {
        if (access.Of(column) >= FieldAccessLevel.ReadOnly)
        {
            view[name] = value;
        }
    }

    private static Dictionary<string, object?> RunView(PayRun run, FieldAccessMap? access)
    {
        var view = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = run.Id,
            ["runNo"] = run.RunNo,
            ["companyId"] = run.CompanyId,
            ["payGroupId"] = run.PayGroupId,
            ["payPeriodId"] = run.PayPeriodId,
            ["kind"] = run.Kind,
            ["currencyCode"] = run.CurrencyCode,
            ["status"] = run.Status,
            ["snapshotTakenAt"] = run.SnapshotTakenAt,
            ["employeeCount"] = run.EmployeeCount,
            ["stageLog"] = JsonSerializer.Deserialize<JsonElement>(run.StageLog.RootElement.GetRawText()),
            ["rowVersion"] = run.RowVersion,
        };
        if (access is not null)
        {
            Show(view, access, "total_gross", "totalGross", run.TotalGross);
            Show(view, access, "total_net", "totalNet", run.TotalNet);
            Show(view, access, "total_employer", "totalEmployer", run.TotalEmployer);
        }

        return view;
    }
}
