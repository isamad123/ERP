using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Hrm.Infrastructure.Policies;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>One line of a year-end preview: an employment and leave type, its closing balance and what happens to it.</summary>
internal sealed record YearEndLine(
    Guid EmploymentId, string EmployeeNo, Guid LeaveTypeId, string Code, string Unit, DateOnly YearStart, DateOnly YearEnd, decimal Closing, decimal CarryForward,
    decimal Encash, decimal Lapse, DateOnly? CarryExpiresOn, Guid? PolicyVersionId, string? RateBasis, string Status);

/// <summary>A leave type of a year-end run with its leave year (types may start their year in different months).</summary>
internal sealed record YearEndType(Guid LeaveTypeId, string Code, DateOnly YearStart, DateOnly YearEnd);

/// <summary>
/// The year-end of a company and leave year (slice S1.32 Build item 12; FR-HRM-LVE-11, LVE-03): for every employment current on the last day of the
/// leave year and every leave type with entries in it, the closing balance (expiring leave gone, <see cref="LeaveLots"/>) and what the policy in force
/// on that day does with it (<see cref="YearEndRules"/>). A type with no policy for the employee is listed and nothing happens to it; an employment that
/// ended before the year's end is left to its final settlement.
/// </summary>
internal static class LeaveYearEndPlanner
{
    public const string Ok = "ok";
    public const string NoPolicy = "no_policy";
    public const string Separated = "separated";

    public static async Task<(List<YearEndType> Types, List<YearEndLine> Lines)> PlanAsync(HrmDbContext db, IServiceProvider services, Guid companyId, int leaveYear, CancellationToken ct)
    {
        var settings = services.GetRequiredService<ISettings>();
        var policies = new LeavePolicyReader(PolicyResolver.For(db, services));
        var years = new LeaveYearSetting(settings);
        var companyMonth = await years.StartMonthAsync(companyId, ct).ConfigureAwait(false);
        var companyYear = LeaveYears.For(leaveYear, companyMonth);
        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => t.CompanyId == companyId && !t.IsDeleted).OrderBy(t => t.Code).ToListAsync(ct).ConfigureAwait(false);
        var withEntries = await db.Set<LeaveLedger>().AsNoTracking().Where(x => x.CompanyId == companyId && x.LeaveYear == leaveYear)
            .Select(x => new { x.EmploymentId, x.LeaveTypeId }).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var employmentIds = withEntries.Select(x => x.EmploymentId).Distinct().ToList();
        var employments = await db.Set<Employment>().AsNoTracking().Where(e => employmentIds.Contains(e.Id) && e.Status != "cancelled").OrderBy(e => e.EmployeeNo).ToListAsync(ct).ConfigureAwait(false);
        var lines = new List<YearEndLine>();
        var spans = new Dictionary<Guid, YearEndType>();
        foreach (var e in employments)
        {
            foreach (var type in types.Where(t => withEntries.Any(x => x.EmploymentId == e.Id && x.LeaveTypeId == t.Id)))
            {
                var first = (await policies.AtAsync(e.Id, Min(companyYear.End, e.SeparationDate), ct).ConfigureAwait(false))?.For(type.Code);
                var span = LeaveYears.For(leaveYear, first?.YearStartMonth ?? companyMonth);
                spans.TryAdd(type.Id, new YearEndType(type.Id, type.Code, span.Start, span.End));
                var entries = await LeaveBalances.LinesAsync(db, e.Id, type.Id, leaveYear, ct).ConfigureAwait(false);
                var closing = LeaveLots.Project(entries, span.End).Closing;
                if (e.SeparationDate is { } last && last < span.End)
                {
                    lines.Add(new YearEndLine(e.Id, e.EmployeeNo, type.Id, type.Code, type.Unit, span.Start, span.End, closing, 0m, 0m, 0m, null, null, null, Separated));
                    continue;
                }

                var policy = await policies.AtAsync(e.Id, span.End, ct).ConfigureAwait(false);
                var rules = policy?.For(type.Code);
                if (rules is null)
                {
                    lines.Add(new YearEndLine(e.Id, e.EmployeeNo, type.Id, type.Code, type.Unit, span.Start, span.End, closing, 0m, 0m, 0m, null, null, null, NoPolicy));
                    continue;
                }

                if (closing == 0m)
                {
                    continue;
                }

                var split = YearEndRules.Split(closing, rules, span.End.AddDays(1));
                lines.Add(new YearEndLine(e.Id, e.EmployeeNo, type.Id, type.Code, type.Unit, span.Start, span.End, closing, split.CarryForward, split.Encash, split.Lapse, split.CarryExpiresOn,
                    policy!.Resolution.Policy?.VersionId, rules.EncashmentRateBasis, Ok));
            }
        }

        return ([.. spans.Values.OrderBy(t => t.Code, StringComparer.Ordinal)], lines);
    }

    public static JsonDocument Summary(int leaveYear, IReadOnlyList<YearEndType> types, IReadOnlyList<YearEndLine> lines, DateTimeOffset now)
    {
        var ok = lines.Where(l => l.Status == Ok).ToList();
        return JsonSerializer.SerializeToDocument(new
        {
            leaveYear,
            preparedAt = now,
            types,
            totals = new
            {
                lines = lines.Count,
                carryForward = ok.Sum(l => l.CarryForward),
                encash = ok.Sum(l => l.Encash),
                lapse = ok.Sum(l => l.Lapse),
                noPolicy = lines.Count(l => l.Status == NoPolicy),
                separated = lines.Count(l => l.Status == Separated),
            },
            lines,
        }, JsonSerializerOptions.Web);
    }

    public static List<YearEndLine> Lines(JsonDocument summary) =>
        summary.RootElement.TryGetProperty("lines", out var l) ? l.Deserialize<List<YearEndLine>>(JsonSerializerOptions.Web) ?? [] : [];

    public static List<YearEndType> Types(JsonDocument summary) =>
        summary.RootElement.TryGetProperty("types", out var t) ? t.Deserialize<List<YearEndType>>(JsonSerializerOptions.Web) ?? [] : [];

    private static DateOnly Min(DateOnly a, DateOnly? b) => b is { } x && x < a ? x : a;
}

/// <summary>
/// The job <c>hrm.leave_year_end</c> (slice S1.32 Build item 12): for the run's company and the parameter <c>leave_year</c>, makes a preview run
/// (<c>hrm.leave_year_end_runs</c>, status <c>preview</c>) with every line; it changes no balance. A newer preview replaces an older one of the same
/// year (rolled back); a year already confirmed is refused.
/// </summary>
internal sealed class LeaveYearEndJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.leave_year_end";

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var company = job.CompanyId ?? throw new JobFailedException("errors.hrm.leave.year_end.company_required");
        if (job.Parameters.ValueKind != JsonValueKind.Object || !job.Parameters.TryGetProperty("leave_year", out var y) || !y.TryGetInt32(out var leaveYear) || leaveYear is < 2000 or > 2200)
        {
            throw new JobFailedException("errors.hrm.leave.year_end.year_required");
        }

        var actor = job.Services.GetRequiredService<ICurrentActor>().UserId;
        var lines = 0;
        await job.CommitChunkAsync(async (connection, transaction, ct) =>
        {
            await using var db = await LeaveOnTransaction.OpenAsync(job.Services, transaction, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.leave_year_end:" + company.ToString("D", CultureInfo.InvariantCulture)}, 0))", ct).ConfigureAwait(false);
            if (await db.Set<LeaveYearEndRun>().AnyAsync(r => r.CompanyId == company && r.LeaveYear == leaveYear && r.Status == "applied", ct).ConfigureAwait(false))
            {
                throw new JobFailedException("errors.hrm.leave.year_end.already_applied");
            }

            var (types, plan) = await LeaveYearEndPlanner.PlanAsync(db, job.Services, company, leaveYear, ct).ConfigureAwait(false);
            var now = clock.UtcNow;
            foreach (var older in await db.Set<LeaveYearEndRun>().Where(r => r.CompanyId == company && r.LeaveYear == leaveYear && r.Status == "preview").ToListAsync(ct).ConfigureAwait(false))
            {
                older.Status = "rolled_back";
                EmploymentApplier.Bump(older, now, actor);
            }

            db.Add(new LeaveYearEndRun
            {
                Id = Guid.CreateVersion7(now), CompanyId = company, LeaveYear = leaveYear, JobRunId = job.RunId, Summary = LeaveYearEndPlanner.Summary(leaveYear, types, plan, now), Status = "preview",
                CreatedAt = now, CreatedBy = actor, UpdatedAt = now, UpdatedBy = actor, RowVersion = 1,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            lines = plan.Count;
        }, null, 100m, cancellationToken).ConfigureAwait(false);
        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"Year-end preview of leave year {leaveYear}: {lines} lines; nothing was posted."), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Posting a confirmed year-end (FR-HRM-LVE-11), on the caller's transaction: every line is checked again (its closing balance must still be the
/// previewed one, else the preview is stale and a new one is made), then for each <c>ok</c> line the expiries still due in the year, the lapse and
/// the payout dated on the year's last day, and the carry-forward out of the year and into the next (expiring as the policy says). Each entry is
/// posted once (the ledger's unique index), so confirming again posts nothing new; the run becomes <c>applied</c>.
/// </summary>
internal static class LeaveYearEndPosting
{
    public const string Source = "hrm.leave_year_end";
    public const string NotPreview = "errors.hrm.leave.year_end.not_preview";
    public const string Stale = "errors.hrm.leave.year_end.stale";
    public const string NotOver = "errors.hrm.leave.year_end.year_not_over";

    public static async Task<PeopleResult?> ApplyAsync(HrmDbContext db, Guid runId, long? rowVersion, DateTimeOffset now, Guid actor, DateOnly today, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.leave_year_end_runs where id = {runId} for update", ct).ConfigureAwait(false);
        var run = await db.Set<LeaveYearEndRun>().FirstOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return PeopleResult.NotFound;
        }

        if (run.Status != "preview")
        {
            return PeopleResult.Conflict(NotPreview);
        }

        if (rowVersion is { } v && v != run.RowVersion)
        {
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        var types = LeaveYearEndPlanner.Types(run.Summary);
        if (types.Any(t => t.YearEnd >= today))
        {
            return PeopleResult.Conflict(NotOver, new Dictionary<string, object?> { ["yearEnd"] = types.Max(t => t.YearEnd) });
        }

        // First every line is checked (a balance that moved since the preview makes it stale: nothing is posted), then everything is posted.
        var lines = LeaveYearEndPlanner.Lines(run.Summary);
        var checkedLines = new List<(YearEndLine Line, LotProjection Projection)>();
        foreach (var line in lines.Where(l => l.Status == LeaveYearEndPlanner.Ok))
        {
            var entries = await LeaveBalances.LinesAsync(db, line.EmploymentId, line.LeaveTypeId, run.LeaveYear, ct).ConfigureAwait(false);
            checkedLines.Add((line, LeaveLots.Project(entries, line.YearEnd)));
        }

        var stale = checkedLines.Count(c => c.Projection.Closing != c.Line.Closing);
        if (stale > 0)
        {
            return PeopleResult.Conflict(Stale, new Dictionary<string, object?> { ["stale"] = stale });
        }

        var posted = 0;
        foreach (var (line, projection) in checkedLines)
        {
            foreach (var expiry in projection.Expiries)
            {
                posted += await InsertAsync(db, run.CompanyId, line.EmploymentId, line.LeaveTypeId, run.LeaveYear, expiry.On, expiry.Kind, -expiry.Quantity, null, LeaveLots.ExpirySource,
                    LeaveLots.ExpirySourceId(expiry.LotId), null, "expired", now, actor, ct).ConfigureAwait(false);
            }

            var outId = YearEndRules.SourceId(run.Id, line.EmploymentId, line.LeaveTypeId, "out");
            if (line.Lapse != 0m)
            {
                posted += await InsertAsync(db, run.CompanyId, line.EmploymentId, line.LeaveTypeId, run.LeaveYear, line.YearEnd, "lapse", -line.Lapse, null, Source, outId, line.PolicyVersionId, "year_end", now, actor, ct).ConfigureAwait(false);
            }

            if (line.Encash != 0m)
            {
                posted += await InsertAsync(db, run.CompanyId, line.EmploymentId, line.LeaveTypeId, run.LeaveYear, line.YearEnd, "encashment", -line.Encash, null, Source, outId, line.PolicyVersionId, "year_end", now, actor, ct).ConfigureAwait(false);
            }

            if (line.CarryForward != 0m)
            {
                posted += await InsertAsync(db, run.CompanyId, line.EmploymentId, line.LeaveTypeId, run.LeaveYear, line.YearEnd, "carry_forward", -line.CarryForward, null, Source, outId, line.PolicyVersionId, "year_end_out", now, actor, ct).ConfigureAwait(false);
                posted += await InsertAsync(db, run.CompanyId, line.EmploymentId, line.LeaveTypeId, run.LeaveYear + 1, line.YearEnd.AddDays(1), "carry_forward", line.CarryForward, line.CarryExpiresOn, Source,
                    YearEndRules.SourceId(run.Id, line.EmploymentId, line.LeaveTypeId, "in"), line.PolicyVersionId, "year_end_in", now, actor, ct).ConfigureAwait(false);
            }
        }

        var summary = JsonNode.Parse(run.Summary.RootElement.GetRawText())!.AsObject();
        summary["confirmedAt"] = JsonValue.Create(now);
        summary["confirmedBy"] = JsonValue.Create(actor);
        summary["posted"] = JsonValue.Create(posted);
        run.Summary = JsonSerializer.SerializeToDocument(summary);
        run.Status = "applied";
        EmploymentApplier.Bump(run, now, actor);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return null;
    }

    private static Task<int> InsertAsync(HrmDbContext db, Guid company, Guid employment, Guid type, int year, DateOnly date, string kind, decimal quantity, DateOnly? expires, string source,
        Guid sourceId, Guid? policyVersion, string reason, DateTimeOffset now, Guid actor, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, expires_on, source_type, source_id,
                                          policy_version_id, reverses_entry_id, reason, created_at, created_by)
            values ({Guid.CreateVersion7(now)}, {company}, {employment}, {type}, {year}, {date}, {kind}, {quantity}, {expires}, {source}, {sourceId}, {policyVersion}, null, {reason}, {now}, {actor})
            on conflict (source_type, source_id, kind, leave_type_id) where source_id is not null do nothing
            """, ct);
}

/// <summary>Confirming or rolling back a year-end preview (FR-HRM-LVE-11): HR confirms (held for a second person while the setting says so) or rolls back.</summary>
internal sealed class LeaveYearEndService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    ISettings settings,
    IHeldChanges held,
    EmploymentApplier applier) : ILeaveYearEndService
{
    public const string HeldKind = "hrm.leave_year_end.confirm";
    public const string AlreadyHeld = "errors.hrm.leave.year_end.pending";

    public async Task<PeopleResult> ConfirmAsync(Guid runId, LeaveYearEndDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (await VisibleAsync(runId, cancellationToken).ConfigureAwait(false) is not { } run)
        {
            return await permissions.HasPermissionAsync(LeavePermissions.YearEndConfirm, cancellationToken).ConfigureAwait(false) ? PeopleResult.NotFound : PeopleResult.Forbidden();
        }

        if (run.Status != "preview")
        {
            return PeopleResult.Conflict(LeaveYearEndPosting.NotPreview);
        }

        var today = await applier.TodayAsync(run.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var types = LeaveYearEndPlanner.Types(run.Summary);
        if (types.Any(t => t.YearEnd >= today))
        {
            return PeopleResult.Conflict(LeaveYearEndPosting.NotOver, new Dictionary<string, object?> { ["yearEnd"] = types.Max(t => t.YearEnd) });
        }

        var hold = (await settings.GetAsync(HrmSettingKeys.HrmLeaveYearEndRequiresApproval, new SettingContext(run.CompanyId, null), cancellationToken).ConfigureAwait(false)).Value;
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        if (hold.ValueKind != JsonValueKind.False)
        {
            if ((await held.PendingAsync(HeldKind, run.Id, cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                return PeopleResult.Conflict(AlreadyHeld);
            }

            var payload = JsonSerializer.Serialize(new YearEndHeld(run.Id, decision.RowVersion ?? run.RowVersion), JsonSerializerOptions.Web);
            var requestId = await held.SubmitAsync(new HeldChangeRequest(HeldKind, LeaveYearEndRunAccess.Scope.Resource, run.Id, payload, string.IsNullOrWhiteSpace(decision.Reason) ? null : decision.Reason.Trim()),
                tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Pending, Body: new { id = run.Id, status = "pending", requestId });
        }

        if (await LeaveYearEndPosting.ApplyAsync(db, run.Id, decision.RowVersion, clock.UtcNow, actor.UserId, today, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return refused;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        var done = await db.Set<LeaveYearEndRun>().AsNoTracking().FirstAsync(r => r.Id == run.Id, cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { id = done.Id, status = done.Status, rowVersion = done.RowVersion });
    }

    public async Task<PeopleResult> RollbackAsync(Guid runId, LeaveYearEndDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (await VisibleAsync(runId, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return await permissions.HasPermissionAsync(LeavePermissions.YearEndConfirm, cancellationToken).ConfigureAwait(false) ? PeopleResult.NotFound : PeopleResult.Forbidden();
        }

        var run = await db.Set<LeaveYearEndRun>().FirstAsync(r => r.Id == found.Id, cancellationToken).ConfigureAwait(false);
        if (run.Status != "preview")
        {
            return PeopleResult.Conflict(LeaveYearEndPosting.NotPreview);
        }

        if (decision.RowVersion is { } v && v != run.RowVersion)
        {
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        run.Status = "rolled_back";
        EmploymentApplier.Bump(run, clock.UtcNow, actor.UserId);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { id = run.Id, status = run.Status, rowVersion = run.RowVersion });
    }

    private async Task<LeaveYearEndRun?> VisibleAsync(Guid id, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(LeavePermissions.YearEndConfirm, ct).ConfigureAwait(false))
        {
            return null;
        }

        var run = await db.Set<LeaveYearEndRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        return run is not null && await scope.AllowsAsync(run, LeaveYearEndRunAccess.Scope, "confirm", new DbContextScopeSource(db), ct).ConfigureAwait(false) ? run : null;
    }
}

/// <summary>The held confirmation: the run and the row version the maker saw.</summary>
internal sealed record YearEndHeld(Guid RunId, long RowVersion);

/// <summary>
/// Applies a held year-end confirmation when a second person approves it (FR-XM-32: the kernel refuses the maker). The approver needs
/// <c>hrm.leave_year_end_runs.approve</c> and the run's company in their data scope; everything is checked again on the approval's transaction.
/// </summary>
internal sealed class YearEndConfirmHandler(IServiceProvider services, IDataScopeFilter scope, IClock clock, ICurrentActor actor, ISettings settings) : IHeldChangeHandler
{
    public string Kind => LeaveYearEndService.HeldKind;

    public string ApprovePermission => LeavePermissions.YearEndApprove;

    public async Task<bool> VisibleAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var db = await LeaveOnTransaction.OpenAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var run = Read(change) is { } h ? await db.Set<LeaveYearEndRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == h.RunId, cancellationToken).ConfigureAwait(false) : null;
        return run is not null && await scope.AllowsAsync(run, LeaveYearEndRunAccess.Scope, "read", new DbContextScopeSource(db), cancellationToken).ConfigureAwait(false);
    }

    public async Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var db = await LeaveOnTransaction.OpenAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var run = Read(change) is { } h ? await db.Set<LeaveYearEndRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == h.RunId, cancellationToken).ConfigureAwait(false) : null;
        return run is null ? new HeldChangeProblem("errors.data.not_found") : run.Status != "preview" ? new HeldChangeProblem(LeaveYearEndPosting.NotPreview) : null;
    }

    public async Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read(change) is not { } h)
        {
            return new HeldChangeProblem("errors.data.invalid_request");
        }

        await using var db = await LeaveOnTransaction.OpenAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var company = await db.Set<LeaveYearEndRun>().AsNoTracking().Where(r => r.Id == h.RunId).Select(r => (Guid?)r.CompanyId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (company is null)
        {
            return new HeldChangeProblem("errors.data.not_found");
        }

        var today = await new EmploymentApplier(db, settings, PolicyResolver.For(db, services)).TodayAsync(company.Value, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var refused = await LeaveYearEndPosting.ApplyAsync(db, h.RunId, h.RowVersion, clock.UtcNow, actor.UserId, today, cancellationToken).ConfigureAwait(false);
        return refused is null ? null : new HeldChangeProblem(refused.Error ?? "errors.data.invalid_request");
    }

    public async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var db = await LeaveOnTransaction.OpenAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var run = Read(change) is { } h ? await db.Set<LeaveYearEndRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == h.RunId, cancellationToken).ConfigureAwait(false) : null;
        if (run is null)
        {
            return new HeldChangeDescription(null, []);
        }

        var totals = run.Summary.RootElement.TryGetProperty("totals", out var t) ? t : default;
        string Total(string name) => totals.ValueKind == JsonValueKind.Object && totals.TryGetProperty(name, out var v) ? v.GetRawText() : "0";
        var year = run.LeaveYear.ToString(CultureInfo.InvariantCulture);
        return new HeldChangeDescription(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Leave year-end " + year, ["ur"] = "رخصت کے سال کا اختتام " + year },
            [
                new HeldChangeDetail("hrm.leave.year_end.detail.lines", "text", Text: Total("lines")),
                new HeldChangeDetail("hrm.leave.year_end.detail.carry_forward", "text", Text: Total("carryForward")),
                new HeldChangeDetail("hrm.leave.year_end.detail.encash", "text", Text: Total("encash")),
                new HeldChangeDetail("hrm.leave.year_end.detail.lapse", "text", Text: Total("lapse")),
            ]);
    }

    private static YearEndHeld? Read(HeldChange change)
    {
        try
        {
            return JsonSerializer.Deserialize<YearEndHeld>(change.Payload, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
