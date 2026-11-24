using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Policies;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// The walk both leave jobs share: the employments of the run's company (or of the customer), or the one named by the parameter
/// <c>employment_id</c>, in id order, in chunks; each chunk is one transaction that also saves the checkpoint, so a stopped run continues
/// after the last employment it finished (FR-KRN-JOB-02). Cancelled employments are skipped; a separated one only while its end is recent
/// enough to fall in the horizon. Each employment is worked out with the company's today (setting <c>org.time_zone</c>).
/// </summary>
internal static class LeaveJobWalk
{
    private const int Chunk = 100;

    public static async Task RunAsync(IJobExecution job, IClock clock, Func<HrmDbContext, Employment, IReadOnlyList<LeaveType>, DateOnly, EntitlementPlanner, LeaveRecompute, CancellationToken, Task> each, CancellationToken cancellationToken)
    {
        var settings = job.Services.GetRequiredService<ISettings>();
        var locks = job.Services.GetRequiredService<IAttendancePeriodLock>();
        var now = clock.UtcNow;
        var oldest = DateOnly.FromDateTime(now.UtcDateTime).AddYears(-2);
        Guid? only = job.Parameters.ValueKind == JsonValueKind.Object && job.Parameters.TryGetProperty("employment_id", out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out var id) ? id : null;
        Guid? after = job.Checkpoint is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("after", out var a) && Guid.TryParse(a.GetString(), out var last) ? last : null;
        while (true)
        {
            var read = 0;
            var cursor = after;
            var checkpoint = new WalkCheckpoint { After = cursor?.ToString("D", CultureInfo.InvariantCulture) };
            await job.CommitChunkAsync(async (connection, transaction, ct) =>
            {
                await using var db = new HrmDbContext(job.Services.GetRequiredService<DbContextOptions<HrmDbContext>>());
                db.Database.SetDbConnection(connection, contextOwnsConnection: false);
                await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
                var resolver = PolicyResolver.For(db, job.Services);
                var policies = new LeavePolicyReader(resolver);
                var years = new LeaveYearSetting(settings);
                var planner = new EntitlementPlanner(db, policies, years, locks, new WorkedTimeReader(db));
                var recompute = new LeaveRecompute(db, planner, policies);
                var applier = new EmploymentApplier(db, settings, resolver);
                var query = db.Set<Employment>().AsNoTracking().Where(e => e.Status != "cancelled" && (e.SeparationDate == null || e.SeparationDate >= oldest));
                if (job.CompanyId is { } company)
                {
                    query = query.Where(e => e.CompanyId == company);
                }

                if (only is { } single)
                {
                    query = query.Where(e => e.Id == single);
                }

                if (cursor is { } from)
                {
                    query = query.Where(e => e.Id.CompareTo(from) > 0);
                }

                var page = await query.OrderBy(e => e.Id).Take(Chunk).ToListAsync(ct).ConfigureAwait(false);
                read = page.Count;
                var companies = page.Select(e => e.CompanyId).Distinct().ToList();
                var types = (await db.Set<LeaveType>().AsNoTracking().Where(t => companies.Contains(t.CompanyId) && !t.IsDeleted).OrderBy(t => t.Code).ToListAsync(ct).ConfigureAwait(false))
                    .GroupBy(t => t.CompanyId).ToDictionary(g => g.Key, g => (IReadOnlyList<LeaveType>)[.. g]);
                var todays = new Dictionary<Guid, DateOnly>();
                foreach (var e in page)
                {
                    after = e.Id;
                    checkpoint.After = e.Id.ToString("D", CultureInfo.InvariantCulture);
                    if (!types.TryGetValue(e.CompanyId, out var companyTypes))
                    {
                        continue;
                    }

                    if (!todays.TryGetValue(e.CompanyId, out var today))
                    {
                        todays[e.CompanyId] = today = await applier.TodayAsync(e.CompanyId, now, ct).ConfigureAwait(false);
                    }

                    await each(db, e, companyTypes, today, planner, recompute, ct).ConfigureAwait(false);
                }
            }, checkpoint, null, cancellationToken).ConfigureAwait(false); // serialized after the work ran: the last employment of the chunk
            if (read < Chunk)
            {
                return;
            }
        }
    }
}

/// <summary>The checkpoint of a leave job: the last employment finished (read back as <c>after</c>).</summary>
internal sealed class WalkCheckpoint
{
    [System.Text.Json.Serialization.JsonPropertyName("after")]
    public string? After { get; set; }
}

/// <summary>
/// The job <c>hrm.leave_accrual</c> (slice S1.32 Build item 3, FR-HRM-LVE-02): monthly and on demand, posts the grants and accruals the leave
/// policies call for and the ledger does not yet hold, each employment, leave type and period once (the database's unique index; a repeated
/// or resumed run posts nothing twice). Entries are made by the job actor.
/// </summary>
internal sealed class LeaveAccrualJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.leave_accrual";

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var actor = job.Services.GetRequiredService<ICurrentActor>().UserId;
        var posted = 0;
        var employments = 0;
        await LeaveJobWalk.RunAsync(job, clock, async (db, e, types, today, planner, _, ct) =>
        {
            var plan = await planner.PlanAsync(e, [.. types.Where(t => t.IsActive)], today, ct).ConfigureAwait(false);
            posted += await AccrualPoster.PostAsync(db, e, plan, clock.UtcNow, actor, ct).ConfigureAwait(false);
            employments++;
        }, cancellationToken).ConfigureAwait(false);
        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{posted} leave entries posted for {employments} employments."), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The job <c>hrm.leave_recompute</c> (slice S1.32 Build item 6, FR-HRM-LVE-14): monthly and on demand, recomputes every employment's balances
/// from the ledger and the policies and logs each difference with the entry and policy version that explain it. It changes nothing.
/// </summary>
internal sealed class LeaveRecomputeJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.leave_recompute";

    private const int MostLines = 500;

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var lines = new List<string>();
        var differences = 0;
        var employments = 0;
        await LeaveJobWalk.RunAsync(job, clock, async (_, e, types, today, _, recompute, ct) =>
        {
            var report = await recompute.RunAsync(e, types, today, ct).ConfigureAwait(false);
            differences += report.Differences.Count;
            employments++;
            lines.AddRange(report.Differences.Select(d => LeaveRecomputeService.Line(e, d)));
        }, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines.Take(MostLines))
        {
            await job.LogAsync("warning", line, cancellationToken).ConfigureAwait(false);
        }

        await job.LogAsync(differences > 0 ? "warning" : "info",
            string.Create(CultureInfo.InvariantCulture, $"{differences} differences in the leave balances of {employments} employments; nothing was changed."), cancellationToken).ConfigureAwait(false);
    }
}
