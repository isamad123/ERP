using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll.Engine;

/// <summary>The run being calculated with its pay group and period, the currency's minor unit and the rounding mode read when its snapshots are taken.</summary>
internal sealed record PayRunContext(PayRun Run, PayGroup Group, PayPeriod Period, int CurrencyDecimals, string RoundingMode);

/// <summary>
/// Takes the snapshot of a batch of employees of a run (slice S1.35 Build item 3, FR-HRM-PAY-31, ruling Q4): reads every fact the calculation will use
/// with a few queries per batch (S1.35b's chunks call it per chunk) and asks each input provider and statutory calculator once per batch. Nothing it
/// reads is evaluated here; the engine (<see cref="PayEngine"/>) works from the snapshot only.
/// </summary>
internal sealed class PaySnapshotBuilder(
    HrmDbContext db,
    IEnumerable<IPayInputProvider> providers,
    IEnumerable<IStatutoryCalculator> calculators,
    IEffectiveShiftReader shifts,
    ISettings settings)
{
    /// <summary>The run's context: pay group, period, the currency's decimals (2 when the currency table does not say) and the rounding mode.</summary>
    public async Task<PayRunContext> ContextAsync(PayRun run, CancellationToken ct)
    {
        var group = await db.Set<PayGroup>().AsNoTracking().FirstAsync(g => g.Id == run.PayGroupId, ct).ConfigureAwait(false);
        var period = await db.Set<PayPeriod>().AsNoTracking().FirstAsync(p => p.Id == run.PayPeriodId, ct).ConfigureAwait(false);
        var decimals = (await db.Database.SqlQuery<int>($"select decimals as \"Value\" from ref.currencies where code = {run.CurrencyCode}").ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault(2);
        var mode = (await settings.GetAsync(HrmSettingKeys.HrmPayRoundingMode, new SettingContext(run.CompanyId, null), ct).ConfigureAwait(false)).Value;
        return new PayRunContext(run, group, period, decimals, mode.ValueKind == JsonValueKind.String ? mode.GetString()! : PayRounding.HalfAwayFromZero);
    }

    /// <summary>
    /// The employments the run pays: assigned to the run's pay group on some day of the period, joined by its end, not separated before its start and
    /// not cancelled; in id order.
    /// </summary>
    public async Task<List<Guid>> EligibleAsync(PayRunContext ctx, CancellationToken ct)
    {
        var (start, end, groupId) = (ctx.Period.StartDate, ctx.Period.EndDate, ctx.Group.Id);
        return await (from a in db.Set<EmploymentAssignment>().AsNoTracking()
                      join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
                      where a.PayGroupId == groupId && a.ValidFrom <= end && (a.ValidTo == null || a.ValidTo > start)
                            && e.CompanyId == ctx.Run.CompanyId && e.Status != "cancelled" && e.JoiningDate <= end && (e.SeparationDate == null || e.SeparationDate >= start)
                      select e.Id).Distinct().OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<Guid, PaySnapshot>> BuildAsync(PayRunContext ctx, IReadOnlyList<Guid> employmentIds, CancellationToken ct)
    {
        var ids = employmentIds.ToList();
        var (start, end) = (ctx.Period.StartDate, ctx.Period.EndDate);
        var run = ctx.Run;
        var employments = await db.Set<Employment>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);
        var assignments = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => ids.Contains(a.EmploymentId) && a.ValidFrom <= end && (a.ValidTo == null || a.ValidTo > start))
            .OrderBy(a => a.ValidFrom).ThenBy(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        var compensation = await db.Set<CompensationAssignment>().AsNoTracking()
            .Where(c => ids.Contains(c.EmploymentId) && c.ValidFrom <= end && (c.ValidTo == null || c.ValidTo > start))
            .OrderBy(c => c.ValidFrom).ThenBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        var compensationIds = compensation.Select(c => c.Id).ToList();
        var lines = await db.Set<CompensationLine>().AsNoTracking().Where(l => compensationIds.Contains(l.AssignmentId)).ToListAsync(ct).ConfigureAwait(false);
        var structureIds = compensation.Select(c => c.StructureId).Distinct().ToList();
        var structureRows = await db.Set<SalaryStructureComponent>().AsNoTracking()
            .Where(r => structureIds.Contains(r.StructureId) && r.ValidFrom <= end && (r.ValidTo == null || r.ValidTo > start))
            .OrderBy(r => r.StructureId).ThenBy(r => r.ValidFrom).ThenBy(r => r.EvaluationOrder).ThenBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false);

        // Inputs of every provider, asked once for the batch.
        var request = new PayInputRequest(run.CompanyId, run.Id, run.PayPeriodId, run.PayGroupId, start, end, ids);
        var inputs = new List<PayInputItem>();
        foreach (var provider in providers.OrderBy(p => p.Code, StringComparer.Ordinal))
        {
            inputs.AddRange((await provider.ForAsync(request, ct).ConfigureAwait(false)).Where(i => employments.ContainsKey(i.EmploymentId)));
        }

        // The components the structures, the lines and the inputs name, with every version in force during the period.
        var componentIds = structureRows.Select(r => r.ComponentId).Concat(lines.Select(l => l.ComponentId)).Concat(inputs.Where(i => i.ComponentId is not null).Select(i => i.ComponentId!.Value)).Distinct().ToList();
        var components = await db.Set<PayComponent>().AsNoTracking().Where(c => componentIds.Contains(c.Id) && c.CompanyId == run.CompanyId).ToListAsync(ct).ConfigureAwait(false);
        var versions = await db.Set<PayComponentVersion>().AsNoTracking()
            .Where(v => componentIds.Contains(v.ComponentId) && v.ValidFrom <= end && (v.ValidTo == null || v.ValidTo > start))
            .OrderBy(v => v.ValidFrom).ThenBy(v => v.Id).ToListAsync(ct).ConfigureAwait(false);
        var snapshotComponents = components.OrderBy(c => c.Code, StringComparer.Ordinal).Select(c => new SnapshotComponent(
            c.Id, c.Code, c.Kind, c.PayslipOrder, [.. versions.Where(v => v.ComponentId == c.Id).Select(Version)])).ToList();

        // Tables of the non-statutory lookups, in force at the period's end; the company's own version before the tenant-wide one.
        var tableCodes = snapshotComponents.SelectMany(c => c.Versions).Where(v => v.Method == "table_lookup" && !v.IsStatutory && v.TableCode is not null).Select(v => v.TableCode!).Distinct().ToList();
        var tables = new List<SnapshotTable>();
        if (tableCodes.Count > 0)
        {
            var heads = await db.Set<StatutoryTable>().AsNoTracking()
                .Where(t => tableCodes.Contains(t.Code) && (t.CompanyId == run.CompanyId || t.CompanyId == null) && t.ValidFrom <= end && (t.ValidTo == null || t.ValidTo > end))
                .ToListAsync(ct).ConfigureAwait(false);
            var chosen = heads.GroupBy(t => t.Code).Select(g => g.OrderBy(t => t.CompanyId == null ? 1 : 0).First()).ToList();
            var tableIds = chosen.Select(t => t.Id).ToList();
            var rows = await db.Set<StatutoryTableRow>().AsNoTracking().Where(r => tableIds.Contains(r.TableId)).ToListAsync(ct).ConfigureAwait(false);
            tables.AddRange(chosen.OrderBy(t => t.Code, StringComparer.Ordinal).Select(t => new SnapshotTable(t.Code, t.Id, t.ValidFrom, t.Kind,
                [.. rows.Where(r => r.TableId == t.Id).OrderBy(r => r.Seq).Select(r => new SnapshotTableRow(r.Seq, r.LowerBound, r.UpperBound, r.FixedAmount, r.Rate, r.Key))])));
        }

        // The day results of the period (their day types are the plan; their attendance policy versions are frozen).
        var days = await db.Set<AttendanceDay>().AsNoTracking()
            .Where(d => ids.Contains(d.EmploymentId) && d.WorkDate >= start && d.WorkDate <= end && d.SupersededAt == null)
            .Select(d => new { d.EmploymentId, d.WorkDate, d.DayType, d.PolicyVersionId }).ToListAsync(ct).ConfigureAwait(false);

        // What each statutory calculator needs, frozen once for the batch.
        var statutory = new Dictionary<string, IReadOnlyDictionary<Guid, JsonElement>>(StringComparer.Ordinal);
        foreach (var calculator in calculators.OrderBy(c => c.Code, StringComparer.Ordinal))
        {
            statutory[calculator.Code] = await calculator.FreezeAsync(
                new StatutoryFreezeRequest(run.CompanyId, run.Id, run.PayPeriodId, ctx.Period.TaxYear, ctx.Period.PeriodNo, start, end, ids), ct).ConfigureAwait(false);
        }

        var runFacts = new SnapshotRun(run.Id, run.Kind, run.CompanyId, run.PayGroupId, run.PayPeriodId, ctx.Period.TaxYear, ctx.Period.PeriodNo, start, end, run.CurrencyCode, ctx.CurrencyDecimals, ctx.RoundingMode);
        var snapshots = new Dictionary<Guid, PaySnapshot>();
        foreach (var id in ids)
        {
            if (!employments.TryGetValue(id, out var e))
            {
                continue;
            }

            var own = assignments.Where(a => a.EmploymentId == id).ToList();
            var employed = Employed(e, own, ctx.Group.Id, start, end);
            var comp = compensation.Where(c => c.EmploymentId == id).ToList();
            var structures = comp.Select(c => c.StructureId).ToHashSet();
            var rowsOf = structureRows.Where(r => structures.Contains(r.StructureId)).ToList();
            var mine = inputs.Where(i => i.EmploymentId == id).OrderBy(i => i.Provider, StringComparer.Ordinal).ThenBy(i => i.Source, StringComparer.Ordinal)
                .ThenBy(i => i.SourceId).ThenBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Date).ThenBy(i => i.ComponentId).ToList();
            var used = rowsOf.Select(r => r.ComponentId).Concat(comp.SelectMany(c => lines.Where(l => l.AssignmentId == c.Id).Select(l => l.ComponentId)))
                .Concat(mine.Where(i => i.ComponentId is not null).Select(i => i.ComponentId!.Value)).ToHashSet();
            var ownComponents = snapshotComponents.Where(c => used.Contains(c.ComponentId)).ToList();
            var ownDays = days.Where(d => d.EmploymentId == id).ToList();
            var plan = PayEngine.NeedsPlan(ownComponents.SelectMany(c => c.Versions))
                ? new SnapshotDays(true, await NonWorkingAsync(id, start, end, ownDays.ToDictionary(d => d.WorkDate, d => d.DayType), ct).ConfigureAwait(false))
                : new SnapshotDays(false, []);
            var frozen = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (code, answers) in statutory)
            {
                if (answers.TryGetValue(id, out var value))
                {
                    frozen[code] = value;
                }
            }

            snapshots[id] = new PaySnapshot(
                PayEngine.Version,
                runFacts,
                new SnapshotEmployment(e.Id, e.EmployeeType, e.JoiningDate, e.SeparationDate, employed),
                [.. own.Select(a => new SnapshotAssignment(a.Id, a.ValidFrom, a.ValidTo, a.OrgUnitId, a.BranchId, a.PositionId, a.GradeId, a.LocationId, a.PayGroupId, a.CostAllocation.RootElement.Clone()))],
                plan,
                [.. comp.Select(c => new SnapshotCompensation(c.Id, c.StructureId, c.ValidFrom, c.ValidTo, c.CurrencyCode,
                    [.. lines.Where(l => l.AssignmentId == c.Id).OrderBy(l => l.ComponentId).Select(l => new SnapshotCompensationLine(l.ComponentId, l.Amount, l.Percent, l.Units))]))],
                [.. rowsOf.Select(r => new SnapshotStructureRow(r.Id, r.StructureId, r.ComponentId, r.EvaluationOrder, r.DefaultAmount, r.DefaultPercent, r.IsMandatory, r.CurrencyCode, r.ValidFrom, r.ValidTo))],
                ownComponents,
                [.. tables.Where(t => ownComponents.SelectMany(c => c.Versions).Any(v => v.TableCode == t.Code))],
                mine,
                [.. ownDays.Where(d => d.PolicyVersionId is not null).Select(d => d.PolicyVersionId!.Value).Distinct().Order().Select(v => new SnapshotPolicy("attendance", v))],
                frozen);
        }

        return snapshots;
    }

    /// <summary>The days of the period on which the employment is employed in the pay group, as inclusive ranges.</summary>
    private static List<DateRange> Employed(Employment e, List<EmploymentAssignment> assignments, Guid group, DateOnly start, DateOnly end)
    {
        var ranges = new List<DateRange>();
        DateOnly? open = null;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var day = d;
            var on = day >= e.JoiningDate && (e.SeparationDate is null || day <= e.SeparationDate)
                     && assignments.Any(a => a.PayGroupId == group && a.ValidFrom <= day && (a.ValidTo is null || a.ValidTo > day));
            if (on && open is null)
            {
                open = day;
            }
            else if (!on && open is { } from)
            {
                ranges.Add(new DateRange(from, day.AddDays(-1)));
                open = null;
            }
        }

        if (open is { } last)
        {
            ranges.Add(new DateRange(last, end));
        }

        return ranges;
    }

    /// <summary>The planned weekly-off and holiday dates of the period: the processed day's type, else the effective shift (a day nothing plans is working).</summary>
    private async Task<List<DateOnly>> NonWorkingAsync(Guid employmentId, DateOnly start, DateOnly end, Dictionary<DateOnly, string> processed, CancellationToken ct)
    {
        var result = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var type = processed.TryGetValue(d, out var t) ? t : (await shifts.ReadAsync(employmentId, d, ct).ConfigureAwait(false))?.DayType;
            if (type is "weekly_off" or "holiday")
            {
                result.Add(d);
            }
        }

        return result;
    }

    private static SnapshotVersion Version(PayComponentVersion v)
    {
        var flags = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var p in v.StatutoryFlags.RootElement.EnumerateObject())
        {
            flags[p.Name] = p.Value.ValueKind == JsonValueKind.True;
        }

        int? divisor = null;
        string? count = null;
        if (v.ProRataParams is { } pp && pp.RootElement.ValueKind == JsonValueKind.Object)
        {
            divisor = pp.RootElement.TryGetProperty("divisor", out var dv) && dv.TryGetInt32(out var di) ? di : null;
            count = pp.RootElement.TryGetProperty("count", out var cv) ? cv.GetString() : null;
        }

        return new SnapshotVersion(v.Id, v.ValidFrom, v.ValidTo, v.Method, v.Formula, [.. v.BaseComponentIds], v.StatutoryTableCode, v.ProRataRule, divisor, count, flags, v.TaxTreatment);
    }
}
