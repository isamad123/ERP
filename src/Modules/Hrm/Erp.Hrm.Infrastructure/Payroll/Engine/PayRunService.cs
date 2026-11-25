using System.Globalization;
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
    IEnumerable<IPayInputProvider> providers,
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

    private static readonly string[] Calculable = ["created", "inputs_imported"];

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
                StageLog = Stage(null, "created", now, user),
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
        if (!await permissions.HasPermissionAsync(PayRunPermissions.Run, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var run = await db.Set<PayRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !await scope.AllowsAsync(run, PayRunAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (run.Kind != "regular" || !Calculable.Contains(run.Status))
        {
            return PeopleResult.Conflict(NotCalculable, new Dictionary<string, object?> { ["status"] = run.Status });
        }

        // FR-XM-26: the period's attendance and leave lock first; nothing is calculated when they cannot.
        var user = actor.UserId;
        var locked = await locks.LockAsync(new PeriodLockRequest(run.PayPeriodId, run.Id, request.AcceptOpenItems, user), null, cancellationToken).ConfigureAwait(false);
        var lockView = new Dictionary<string, object?>
        {
            ["outcome"] = locked.Outcome.ToString(),
            ["attendancePeriodId"] = locked.PeriodId,
            ["unprocessedDays"] = locked.UnprocessedDays,
            ["openExceptions"] = locked.OpenExceptions,
        };
        if (locked.Outcome == PeriodLockOutcome.Refused)
        {
            return PeopleResult.Conflict(locked.ErrorKey ?? LockRefused, new Dictionary<string, object?> { ["lock"] = lockView });
        }

        var ctx = await builder.ContextAsync(run, cancellationToken).ConfigureAwait(false);
        var eligible = await builder.EligibleAsync(ctx, cancellationToken).ConfigureAwait(false);
        var done = (await db.Set<PayRunEmployee>().AsNoTracking().Where(e => e.PayRunId == run.Id && e.Status == "calculated").Select(e => e.EmploymentId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var todo = eligible.Where(id => !done.Contains(id)).ToList();
        var chunkSize = await ChunkSizeAsync(run.CompanyId, cancellationToken).ConfigureAwait(false);
        var registered = calculators.OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
        var incomplete = new List<object>();
        foreach (var chunk in todo.Chunk(chunkSize))
        {
            try
            {
                incomplete.AddRange(await CalculateChunkAsync(ctx, chunk, registered, user, cancellationToken).ConfigureAwait(false));
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { ConstraintName: "ex_pay_run_employees_employment_id_period" })
            {
                return PeopleResult.Conflict(EmployeeConflict);
            }
            catch (InvalidOperationException e) when (e.Message == NotCalculable)
            {
                return PeopleResult.Conflict(NotCalculable);
            }
        }

        return await FinishAsync(run.Id, eligible.Count, incomplete, lockView, user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One batch in one transaction: snapshots, calculation from the stored form, employees, results, lines and used inputs.</summary>
    private async Task<List<object>> CalculateChunkAsync(PayRunContext ctx, Guid[] chunk, List<IStatutoryCalculator> registered, Guid user, CancellationToken ct)
    {
        var incomplete = new List<object>();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The run row is held for the batch: two calculations of one run never write the same employee, and a run that moved on is not written.
        var status = (await db.Database.SqlQuery<string>($"select status as \"Value\" from hrm.pay_runs where id = {ctx.Run.Id} for update").ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault();
        if (status is null || !Calculable.Contains(status))
        {
            throw new InvalidOperationException(NotCalculable);
        }

        var now = clock.UtcNow;
        var ids = chunk.ToList();
        var rows = await db.Set<PayRunEmployee>().Where(e => e.PayRunId == ctx.Run.Id && ids.Contains(e.EmploymentId)).ToDictionaryAsync(e => e.EmploymentId, ct).ConfigureAwait(false);
        ids = [.. ids.Where(id => !rows.TryGetValue(id, out var r) || r.Status != "calculated")];
        var snapshots = await builder.BuildAsync(ctx, ids, ct).ConfigureAwait(false);
        var byCode = providers.ToDictionary(p => p.Code, StringComparer.Ordinal);
        var pendingLines = new List<PayResultLine>();
        var consumed = new List<(IPayInputProvider Provider, PayInputsConsumed Items)>();
        foreach (var id in ids)
        {
            if (!snapshots.TryGetValue(id, out var taken))
            {
                continue;
            }

            // The calculation reads the snapshot as it is stored (PAY-31): the canonical JSON, compressed, with its SHA-256.
            var canonical = PaySnapshotCodec.Canonical(taken);
            var snapshot = PaySnapshotCodec.Read(canonical);
            var calc = PayEngine.Calculate(snapshot, registered);
            var employed = snapshot.Employment.Employed;
            if (!rows.TryGetValue(id, out var row))
            {
                row = new PayRunEmployee
                {
                    Id = Guid.CreateVersion7(now),
                    CompanyId = ctx.Run.CompanyId,
                    PayRunId = ctx.Run.Id,
                    EmploymentId = id,
                    RunKind = ctx.Run.Kind,
                    CreatedAt = now,
                    CreatedBy = user,
                };
                db.Add(row);
            }
            else
            {
                EmploymentApplier.Bump(row, now, user);
            }

            row.PeriodStart = employed.Count > 0 ? employed.Min(r => r.From) : ctx.Period.StartDate;
            row.PeriodEnd = employed.Count > 0 ? employed.Max(r => r.To) : ctx.Period.EndDate;
            row.RunStatus = status;
            row.Snapshot = PaySnapshotCodec.Pack(canonical);
            row.SnapshotHash = PaySnapshotCodec.Hash(canonical);
            row.Status = calc.Complete ? "calculated" : "error";
            row.ValidationFlags = [.. Flags(calc)];
            row.UpdatedAt = now;
            row.UpdatedBy = user;
            if (!calc.Complete)
            {
                incomplete.Add(new { employmentId = id, problems = calc.Problems.Select(p => new { key = p.Key, subject = p.Subject }).ToList() });
                continue;
            }

            var result = new PayResult
            {
                Id = Guid.CreateVersion7(now),
                CompanyId = ctx.Run.CompanyId,
                PayRunId = ctx.Run.Id,
                EmploymentId = id,
                TaxYear = ctx.Period.TaxYear,
                PayPeriodId = ctx.Period.Id,
                Gross = calc.Gross,
                Deductions = calc.Deductions,
                Net = calc.Net,
                EmployerCost = calc.EmployerCost,
                TaxableIncomeYtd = calc.Facts.GetValueOrDefault("taxable_income_ytd"),
                TaxYtd = calc.Facts.GetValueOrDefault("tax_ytd"),
                WorkedDays = calc.WorkedDays,
                PaidDays = calc.PaidDays,
                CreatedAt = now,
                CreatedBy = user,
                UpdatedAt = now,
                UpdatedBy = user,
            };
            db.Add(result);
            var allocation = Allocation(snapshot);
            var lineIds = new Dictionary<Guid, Guid>();
            foreach (var line in calc.Lines)
            {
                var entity = new PayResultLine
                {
                    Id = Guid.CreateVersion7(now),
                    CompanyId = ctx.Run.CompanyId,
                    ResultId = result.Id,
                    PayPeriodId = ctx.Period.Id,
                    EmploymentId = id,
                    ComponentId = line.ComponentId,
                    Amount = line.Amount,
                    Units = line.Units,
                    Rate = line.Rate,
                    CostAllocation = JsonDocument.Parse(allocation),
                    CreatedAt = now,
                    CreatedBy = user,
                    UpdatedAt = now,
                    UpdatedBy = user,
                };
                lineIds[line.ComponentId] = entity.Id;
                pendingLines.Add(entity);
            }

            // The inputs the result used, handed back to their providers in the same transaction once the lines are saved (one-time inputs become consumed by the run).
            foreach (var group in snapshot.Inputs.GroupBy(i => i.Provider, StringComparer.Ordinal))
            {
                if (byCode.TryGetValue(group.Key, out var provider))
                {
                    consumed.Add((provider, new PayInputsConsumed(ctx.Run.Id, ctx.Period.Id, id, result.Id,
                        [.. group.Select(i => new ConsumedPayInput(i, i.ComponentId is { } c && lineIds.TryGetValue(c, out var l) ? l : null))])));
                }
            }
        }

        // Saved in order (the tables are linked by ids only): employees and results, then their lines, then what the providers mark as used.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.AddRange(pendingLines);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var (provider, items) in consumed)
        {
            await provider.ConsumedAsync(items, ct).ConfigureAwait(false);
        }

        var tracked = await db.Set<PayRun>().FirstAsync(r => r.Id == ctx.Run.Id, ct).ConfigureAwait(false);
        if (tracked.SnapshotTakenAt is null)
        {
            tracked.SnapshotTakenAt = now;
            EmploymentApplier.Bump(tracked, now, user);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return incomplete;
    }

    /// <summary>When every employee has a result the run is calculated: totals, count, stage log, event; else the stage log records the stop.</summary>
    private async Task<PeopleResult> FinishAsync(Guid runId, int eligible, List<object> incomplete, Dictionary<string, object?> lockView, Guid user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.pay_runs where id = {runId} for update", ct).ConfigureAwait(false);
        var run = await db.Set<PayRun>().FirstAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        var counts = await db.Set<PayRunEmployee>().AsNoTracking().Where(e => e.PayRunId == runId).GroupBy(e => e.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        var calculated = counts.Where(c => c.Key == "calculated").Sum(c => c.Count);
        var errors = counts.Where(c => c.Key == "error").Sum(c => c.Count);
        var now = clock.UtcNow;
        if (errors == 0 && Calculable.Contains(run.Status))
        {
            var totals = await db.Set<PayResult>().AsNoTracking().Where(r => r.PayRunId == runId)
                .GroupBy(_ => 1).Select(g => new { Gross = g.Sum(r => r.Gross), Net = g.Sum(r => r.Net), Employer = g.Sum(r => r.EmployerCost), Count = g.Count() })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            run.Status = "calculated";
            run.TotalGross = totals?.Gross ?? 0m;
            run.TotalNet = totals?.Net ?? 0m;
            run.TotalEmployer = totals?.Employer ?? 0m;
            run.EmployeeCount = totals?.Count ?? 0;
            run.StageLog = Stage(run.StageLog, "calculated", now, user, new Dictionary<string, object?> { ["engineVersion"] = PayEngine.Version, ["employees"] = run.EmployeeCount });
            EmploymentApplier.Bump(run, now, user);
            await db.Database.ExecuteSqlInterpolatedAsync($"update hrm.pay_run_employees set run_status = 'calculated', updated_at = {now}, updated_by = {user}, row_version = row_version + 1 where pay_run_id = {runId}", ct).ConfigureAwait(false);
            db.Add(new OutboxMessage
            {
                Id = Guid.CreateVersion7(now),
                OccurredAt = now,
                EventType = PayRunEvents.Calculated,
                AggregateType = PayRunEvents.AggregateType,
                AggregateId = run.Id,
                Payload = JsonSerializer.SerializeToDocument(new Dictionary<string, object?>
                {
                    ["payRunId"] = run.Id,
                    ["runNo"] = run.RunNo,
                    ["companyId"] = run.CompanyId,
                    ["payGroupId"] = run.PayGroupId,
                    ["payPeriodId"] = run.PayPeriodId,
                    ["kind"] = run.Kind,
                    ["employeeCount"] = run.EmployeeCount,
                    ["engineVersion"] = PayEngine.Version,
                    ["calculatedBy"] = user,
                }, JsonSerializerOptions.Web),
                Headers = JsonSerializer.SerializeToDocument(new { actor = user }, JsonSerializerOptions.Web),
            });
        }
        else
        {
            run.StageLog = Stage(run.StageLog, "calculation_incomplete", now, user, new Dictionary<string, object?> { ["engineVersion"] = PayEngine.Version, ["incomplete"] = errors });
            EmploymentApplier.Bump(run, now, user);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            id = run.Id,
            runNo = run.RunNo,
            status = run.Status,
            employees = eligible,
            calculated,
            incomplete,
            engineVersion = PayEngine.Version,
            @lock = lockView,
        });
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
        view["employees"] = counts.ToDictionary(c => c.Key, c => c.Count, StringComparer.Ordinal);
        return new PeopleResult(PeopleOutcome.Done, Body: view);
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

    /// <summary>The run's stage log with one more entry: the stage, when and who (FR-HRM-PAY-10), and the stage's facts (never an amount).</summary>
    private static JsonDocument Stage(JsonDocument? log, string stage, DateTimeOffset at, Guid by, Dictionary<string, object?>? facts = null)
    {
        var list = log is null ? new JsonArray() : JsonNode.Parse(log.RootElement.GetRawText()) as JsonArray ?? [];
        var entry = new JsonObject { ["stage"] = stage, ["at"] = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ["by"] = by.ToString() };
        foreach (var (key, value) in facts ?? new Dictionary<string, object?>())
        {
            entry[key] = JsonSerializer.SerializeToNode(value);
        }

        list.Add(entry);
        return JsonDocument.Parse(list.ToJsonString());
    }

    private static IEnumerable<string> Flags(PayCalculation calc)
    {
        if (!calc.Complete)
        {
            yield return "incomplete";
        }
        else if (calc.Net < 0m)
        {
            yield return "negative_net";
        }
    }

    /// <summary>The cost allocation of the result lines: the assignment in force on the last day paid (unit, branch and its split as stored).</summary>
    private static string Allocation(PaySnapshot s)
    {
        var last = s.Employment.Employed.Count > 0 ? s.Employment.Employed.Max(r => r.To) : s.Run.PeriodEnd;
        var a = s.Assignments.LastOrDefault(x => x.ValidFrom <= last && (x.ValidTo is null || x.ValidTo > last)) ?? (s.Assignments.Count > 0 ? s.Assignments[^1] : null);
        return a is null
            ? "{}"
            : JsonSerializer.Serialize(new { assignmentId = a.Id, orgUnitId = a.OrgUnitId, branchId = a.BranchId, split = a.CostAllocation }, JsonSerializerOptions.Web);
    }

    private async Task<int> ChunkSizeAsync(Guid companyId, CancellationToken ct)
    {
        var value = (await settings.GetAsync(HrmSettingKeys.HrmPayChunkSize, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0 ? n : 200;
    }
}
