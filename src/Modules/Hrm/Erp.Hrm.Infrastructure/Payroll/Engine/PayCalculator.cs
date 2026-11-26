using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll.Engine;

/// <summary>
/// One calculation request of a run (slice S1.35b, CR-093 S1.35b detail): its id, the lane of this job run and the number of lanes, whether employees
/// are calculated from a fresh snapshot or from the one stored, and the rounding mode and currency decimals frozen for the whole request when it was
/// made (review S1.35a finding 11).
/// </summary>
internal sealed record PayCalculationRequest(Guid PayRunId, Guid RequestId, int Lane, int Lanes, bool Fresh, string RoundingMode, int CurrencyDecimals);

/// <summary>What a chunk did: the employees calculated, those left incomplete (with their problems) and the employment the lane's cursor reached.</summary>
internal sealed record PayChunkOutcome(int Calculated, IReadOnlyList<object> Incomplete, Guid? Cursor);

/// <summary>Where a run stands after a lane asked to finish it.</summary>
internal enum PayFinish
{
    /// <summary>Other lanes still have employees to calculate.</summary>
    Pending,

    /// <summary>Every lane is done but some employees have no result yet (they became eligible after their lane passed): the last lane calculates them.</summary>
    Sweep,

    /// <summary>Every employee has a result: the run is calculated and the event is written.</summary>
    Calculated,

    /// <summary>Every employee was tried and some are incomplete: the run stays uncalculated.</summary>
    Incomplete,

    /// <summary>The run is no longer calculable (calculated by another lane, cancelled, or moved on).</summary>
    NotCalculable,
}

/// <summary>
/// The payroll calculation on a given HR database context (slice S1.35 Build items 5, 8, 9 and 11; FR-HRM-PAY-03, -31, -32, -33, -34): a chunk takes or
/// reads each employee's snapshot, calculates from its stored form, and saves the employee, the result (a new one, or the earlier unlocked one replaced
/// as a new row version, its lines replaced: the audit log keeps every earlier version), its lines, its trace (<c>hrm.pay_traces</c>) and the inputs it
/// used, in the caller's transaction (the job's chunk, which also saves the checkpoint). The finish makes the run calculated when every employee has a
/// result, once, with the event. Every write is under the run being calculable; the guard triggers refuse anything on a locked run (PAY-33).
/// </summary>
internal sealed class PayCalculator(
    HrmDbContext db,
    IClock clock,
    PaySnapshotBuilder builder,
    IEnumerable<IPayInputProvider> providers,
    IEnumerable<IStatutoryCalculator> calculators)
{
    /// <summary>The run statuses a calculation may write in.</summary>
    public static readonly string[] Calculable = ["created", "inputs_imported"];

    /// <summary>The lane of an employment: a fixed function of its id (the last four bytes, which are random in a UUIDv7), so lanes never share one.</summary>
    public static int LaneOf(Guid employmentId, int lanes) => lanes <= 1 ? 0 : (int)(BitConverter.ToUInt32(employmentId.ToByteArray(), 12) % (uint)lanes);

    /// <summary>The run's context with the request's frozen rounding mode and currency decimals.</summary>
    public async Task<PayRunContext?> ContextAsync(PayCalculationRequest request, CancellationToken ct)
    {
        var run = await db.Set<PayRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.PayRunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var ctx = await builder.ContextAsync(run, ct).ConfigureAwait(false);
        return ctx with { RoundingMode = request.RoundingMode, CurrencyDecimals = request.CurrencyDecimals };
    }

    /// <summary>
    /// The next employees of the lane after the cursor that still need a result in this request: eligible, of this lane, with no row or a row marked
    /// <c>included</c> (a request marks the employees it recalculates, and those left incomplete before, as included).
    /// </summary>
    public async Task<(List<Guid> Ids, Guid? Cursor)> NextAsync(PayRunContext ctx, PayCalculationRequest request, Guid? after, int take, bool anyLane, CancellationToken ct)
    {
        var eligible = await builder.EligibleAsync(ctx, ct).ConfigureAwait(false);
        var candidates = eligible.Where(id => (anyLane || LaneOf(id, request.Lanes) == request.Lane) && (after is null || id.CompareTo(after.Value) > 0)).ToList();
        if (candidates.Count == 0)
        {
            return ([], after);
        }

        var done = await StatusesAsync(ctx.Run.Id, ct).ConfigureAwait(false);
        var ids = new List<Guid>();
        Guid? cursor = after;
        foreach (var id in candidates)
        {
            cursor = id;
            if (!done.TryGetValue(id, out var status) || status == "included")
            {
                ids.Add(id);
                if (ids.Count == take)
                {
                    break;
                }
            }
        }

        return (ids, ids.Count == 0 ? candidates[^1] : cursor);
    }

    /// <summary>How many eligible employees the lane has (for its progress).</summary>
    public async Task<int> LaneSizeAsync(PayRunContext ctx, PayCalculationRequest request, CancellationToken ct) =>
        (await builder.EligibleAsync(ctx, ct).ConfigureAwait(false)).Count(id => LaneOf(id, request.Lanes) == request.Lane);

    /// <summary>
    /// One chunk in the caller's transaction: snapshots (fresh, or the stored one when the request says so and the employee has one), calculation from
    /// the stored form, the employee, the result and its lines and trace, the inputs used. An employee who becomes incomplete loses an earlier result.
    /// </summary>
    public async Task<PayChunkOutcome> ChunkAsync(PayRunContext ctx, PayCalculationRequest request, IReadOnlyList<Guid> chunk, Guid user, CancellationToken ct)
    {
        var incomplete = new List<object>();
        var run = ctx.Run;
        var status = (await db.Database.SqlQuery<string>($"select status as \"Value\" from hrm.pay_runs where id = {run.Id} for share").ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault();
        if (status is null || !Calculable.Contains(status))
        {
            throw new PayRunNotCalculableException();
        }

        var now = clock.UtcNow;
        var ids = chunk.ToList();
        var rows = await db.Set<PayRunEmployee>().Where(e => e.PayRunId == run.Id && ids.Contains(e.EmploymentId)).ToDictionaryAsync(e => e.EmploymentId, ct).ConfigureAwait(false);
        ids = [.. ids.Where(id => !rows.TryGetValue(id, out var r) || r.Status == "included")];
        var stored = request.Fresh ? [] : ids.Where(rows.ContainsKey).ToHashSet();
        var built = await builder.BuildAsync(ctx, [.. ids.Where(id => !stored.Contains(id))], ct).ConfigureAwait(false);

        // Earlier results of these employees in this run (a recalculation): the result row is kept and changed (a new row version); its lines and trace
        // are replaced. Removed first, as the tables are linked by ids only.
        var results = await db.Set<PayResult>().Where(r => r.PayRunId == run.Id && ids.Contains(r.EmploymentId)).ToDictionaryAsync(r => r.EmploymentId, ct).ConfigureAwait(false);
        var resultIds = results.Values.Select(r => r.Id).ToList();
        if (resultIds.Count > 0)
        {
            db.RemoveRange(await db.Set<PayResultLine>().Where(l => resultIds.Contains(l.ResultId)).ToListAsync(ct).ConfigureAwait(false));
            db.RemoveRange(await db.Set<PayTrace>().Where(t => resultIds.Contains(t.ResultId)).ToListAsync(ct).ConfigureAwait(false));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var registered = calculators.OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
        var byCode = providers.ToDictionary(p => p.Code, StringComparer.Ordinal);
        var pendingLines = new List<PayResultLine>();
        var traces = new List<PayTrace>();
        var consumed = new List<(IPayInputProvider Provider, PayInputsConsumed Items)>();
        var calculated = 0;
        foreach (var id in ids)
        {
            byte[] canonical;
            if (stored.Contains(id))
            {
                canonical = PaySnapshotCodec.Unpack(rows[id].Snapshot);
            }
            else if (built.TryGetValue(id, out var taken))
            {
                canonical = PaySnapshotCodec.Canonical(taken);
            }
            else
            {
                continue;
            }

            // The calculation reads the snapshot as it is stored (PAY-31): the canonical JSON, compressed, with its SHA-256.
            var snapshot = PaySnapshotCodec.Read(canonical);
            var hash = PaySnapshotCodec.Hash(canonical);
            var calc = PayEngine.Calculate(snapshot, registered);
            var employed = snapshot.Employment.Employed;
            if (!rows.TryGetValue(id, out var row))
            {
                row = new PayRunEmployee
                {
                    Id = Guid.CreateVersion7(now),
                    CompanyId = run.CompanyId,
                    PayRunId = run.Id,
                    EmploymentId = id,
                    RunKind = run.Kind,
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
            if (!stored.Contains(id))
            {
                row.Snapshot = PaySnapshotCodec.Pack(canonical);
                row.SnapshotHash = hash;
            }

            row.Status = calc.Complete ? "calculated" : "error";
            row.ValidationFlags = [.. Flags(calc)];
            row.UpdatedAt = now;
            row.UpdatedBy = user;
            results.TryGetValue(id, out var result);
            if (!calc.Complete)
            {
                // Never silently zero: no result, and an earlier one of this run is removed (the audit log keeps it).
                if (result is not null)
                {
                    db.Remove(result);
                }

                incomplete.Add(new { employmentId = id, problems = calc.Problems.Select(p => new { key = p.Key, subject = p.Subject }).ToList() });
                continue;
            }

            if (result is null)
            {
                result = new PayResult
                {
                    Id = Guid.CreateVersion7(now),
                    CompanyId = run.CompanyId,
                    PayRunId = run.Id,
                    EmploymentId = id,
                    CreatedAt = now,
                    CreatedBy = user,
                };
                db.Add(result);
            }
            else
            {
                EmploymentApplier.Bump(result, now, user);
            }

            result.TaxYear = ctx.Period.TaxYear;
            result.PayPeriodId = ctx.Period.Id;
            result.Gross = calc.Gross;
            result.Deductions = calc.Deductions;
            result.Net = calc.Net;
            result.EmployerCost = calc.EmployerCost;
            result.TaxableIncomeYtd = calc.Facts.GetValueOrDefault("taxable_income_ytd");
            result.TaxYtd = calc.Facts.GetValueOrDefault("tax_ytd");
            result.WorkedDays = calc.WorkedDays;
            result.PaidDays = calc.PaidDays;
            result.UpdatedAt = now;
            result.UpdatedBy = user;
            calculated++;

            var allocation = Allocation(snapshot);
            var lineIds = new Dictionary<Guid, Guid>();
            foreach (var line in calc.Lines)
            {
                var entity = new PayResultLine
                {
                    Id = Guid.CreateVersion7(now),
                    CompanyId = run.CompanyId,
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

            // PAY-32: the trace of the result, with the engine version (the trace table is not audited, CR-034).
            traces.Add(new PayTrace
            {
                ResultId = result.Id,
                Trace = JsonDocument.Parse(PayTraceDocument.Build(snapshot, calc, hash)),
                EngineVersion = calc.EngineVersion,
            });

            // The inputs the result used, handed back to their providers in the same transaction once the lines are saved (one-time inputs become consumed by the run).
            foreach (var group in snapshot.Inputs.GroupBy(i => i.Provider, StringComparer.Ordinal))
            {
                if (byCode.TryGetValue(group.Key, out var provider))
                {
                    consumed.Add((provider, new PayInputsConsumed(run.Id, ctx.Period.Id, id, result.Id,
                        [.. group.Select(i => new ConsumedPayInput(i, i.ComponentId is { } c && lineIds.TryGetValue(c, out var l) ? l : null))])));
                }
            }
        }

        // Saved in order (the tables are linked by ids only): employees and results, then their lines and traces, then what the providers mark as used.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.AddRange(pendingLines);
        db.AddRange(traces);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var (provider, items) in consumed)
        {
            await provider.ConsumedAsync(items, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return new PayChunkOutcome(calculated, incomplete, chunk.Count > 0 ? chunk[^1] : null);
    }

    /// <summary>
    /// A lane is done: recorded once in the stage log, then the run is finished when every eligible employee has a result (calculated, with the totals,
    /// the employees' copy of the status and the event <c>hrm.pay_run.calculated</c>) or was tried and is incomplete (recorded once per request). Holds the
    /// run row, so lanes finishing together are taken one after the other.
    /// </summary>
    public async Task<PayFinish> FinishAsync(PayRunContext ctx, PayCalculationRequest request, Guid user, CancellationToken ct)
    {
        var runId = ctx.Run.Id;
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.pay_runs where id = {runId} for update", ct).ConfigureAwait(false);
        var run = await db.Set<PayRun>().FirstAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (!Calculable.Contains(run.Status))
        {
            return PayFinish.NotCalculable;
        }

        var now = clock.UtcNow;
        var key = request.RequestId.ToString();
        var log = Entries(run.StageLog);
        if (!log.Any(e => Is(e, "lane_done", key) && e["lane"]?.GetValue<int>() == request.Lane))
        {
            run.StageLog = Stage(run.StageLog, "lane_done", now, user, new Dictionary<string, object?> { ["request"] = key, ["lane"] = request.Lane });
            EmploymentApplier.Bump(run, now, user);
            log = Entries(run.StageLog);
        }

        var eligible = await builder.EligibleAsync(ctx, ct).ConfigureAwait(false);
        var statuses = await StatusesAsync(runId, ct).ConfigureAwait(false);
        var pending = eligible.Count(id => !statuses.TryGetValue(id, out var s) || s == "included");
        if (pending > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var lanesDone = log.Where(e => Is(e, "lane_done", key)).Select(e => e["lane"]?.GetValue<int>()).Distinct().Count();
            return lanesDone >= request.Lanes ? PayFinish.Sweep : PayFinish.Pending;
        }

        var errors = eligible.Count(id => statuses[id] == "error");
        if (errors > 0)
        {
            if (!log.Any(e => Is(e, "calculation_incomplete", key)))
            {
                run.StageLog = Stage(run.StageLog, "calculation_incomplete", now, user, new Dictionary<string, object?> { ["request"] = key, ["engineVersion"] = PayEngine.Version, ["incomplete"] = errors });
                EmploymentApplier.Bump(run, now, user);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return PayFinish.Incomplete;
        }

        var totals = await db.Set<PayResult>().AsNoTracking().Where(r => r.PayRunId == runId)
            .GroupBy(_ => 1).Select(g => new { Gross = g.Sum(r => r.Gross), Net = g.Sum(r => r.Net), Employer = g.Sum(r => r.EmployerCost), Count = g.Count() })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        run.Status = "calculated";
        run.TotalGross = totals?.Gross ?? 0m;
        run.TotalNet = totals?.Net ?? 0m;
        run.TotalEmployer = totals?.Employer ?? 0m;
        run.EmployeeCount = totals?.Count ?? 0;
        run.StageLog = Stage(run.StageLog, "calculated", now, user, new Dictionary<string, object?> { ["request"] = key, ["engineVersion"] = PayEngine.Version, ["employees"] = run.EmployeeCount });
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
                ["requestId"] = request.RequestId,
            }, JsonSerializerOptions.Web),
            Headers = JsonSerializer.SerializeToDocument(new { actor = user }, JsonSerializerOptions.Web),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return PayFinish.Calculated;
    }

    /// <summary>Adds a stage entry once per request (the start, a cancel): nothing when the run already has one for the request.</summary>
    public async Task StageOnceAsync(Guid runId, string stage, Guid requestId, Guid user, Dictionary<string, object?>? facts, bool snapshotTaken, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.pay_runs where id = {runId} for update", ct).ConfigureAwait(false);
        var run = await db.Set<PayRun>().FirstAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        var key = requestId.ToString();
        if (Entries(run.StageLog).Any(e => Is(e, stage, key)))
        {
            return;
        }

        var now = clock.UtcNow;
        var all = new Dictionary<string, object?>(facts ?? [], StringComparer.Ordinal) { ["request"] = key };
        run.StageLog = Stage(run.StageLog, stage, now, user, all);
        if (snapshotTaken && run.SnapshotTakenAt is null)
        {
            run.SnapshotTakenAt = now;
        }

        EmploymentApplier.Bump(run, now, user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The run's stage log with one more entry: the stage, when and who (FR-HRM-PAY-10), and the stage's facts (never an amount).</summary>
    public static JsonDocument Stage(JsonDocument? log, string stage, DateTimeOffset at, Guid by, Dictionary<string, object?>? facts = null)
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

    /// <summary>The stage log's entries.</summary>
    public static List<JsonObject> Entries(JsonDocument log) =>
        (JsonNode.Parse(log.RootElement.GetRawText()) as JsonArray ?? []).OfType<JsonObject>().ToList();

    private static bool Is(JsonObject entry, string stage, string request) =>
        entry["stage"]?.GetValue<string>() == stage && entry["request"]?.GetValue<string>() == request;

    private async Task<Dictionary<Guid, string>> StatusesAsync(Guid runId, CancellationToken ct) =>
        await db.Set<PayRunEmployee>().AsNoTracking().Where(e => e.PayRunId == runId).ToDictionaryAsync(e => e.EmploymentId, e => e.Status, ct).ConfigureAwait(false);

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
}

/// <summary>The run stopped being calculable while a chunk was about to write (another lane finished it, or it was cancelled or moved on).</summary>
internal sealed class PayRunNotCalculableException() : Exception(PayRunService.NotCalculable);
