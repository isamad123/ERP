using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Contracts.Leave;
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
/// Leave without pay for payroll (contract <see cref="ILeaveDeductions"/>, slice S1.32 Build item 10, FR-HRM-LVE-07, FR-XM-26): the days of approved
/// requests (and of approved leave waiting for its cancellation) of types that are unpaid and a pay deduction, with the lock of each day's attendance
/// period. Hourly leave gives its hours and no share of a day.
/// </summary>
internal sealed class LeaveDeductionReader(HrmDbContext db, IAttendancePeriodLock locks) : ILeaveDeductions
{
    public async Task<IReadOnlyList<LeaveDeductionDay>> ForAsync(IReadOnlyCollection<Guid> employmentIds, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(employmentIds);
        var ids = employmentIds.Distinct().ToList();
        var rows = await (from d in db.Set<LeaveRequestDay>().AsNoTracking()
                          join r in db.Set<LeaveRequest>().AsNoTracking() on d.LeaveRequestId equals r.Id
                          join t in db.Set<LeaveType>().AsNoTracking() on r.LeaveTypeId equals t.Id
                          where ids.Contains(d.EmploymentId) && d.LeaveDate >= firstDay && d.LeaveDate <= lastDay && (r.Status == "approved" || r.Status == "cancel_requested")
                              && !d.IsPaid && t.IsUnpaidDeduction && d.Quantity > 0m // the day's own copy of "paid" (review S1.32b finding 11)
                          orderby d.EmploymentId, d.LeaveDate, r.Id
                          select new { d.EmploymentId, d.LeaveDate, RequestId = r.Id, TypeId = t.Id, t.Code, d.Quantity, Hours = t.Counting == "hours" })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var days = new List<LeaveDeductionDay>(rows.Count);
        foreach (var x in rows)
        {
            days.Add(new LeaveDeductionDay(x.EmploymentId, x.LeaveDate, x.RequestId, x.TypeId, x.Code, x.Hours ? 0m : x.Quantity, x.Hours ? x.Quantity : null,
                await locks.IsLockedAsync(x.EmploymentId, x.LeaveDate, cancellationToken).ConfigureAwait(false)));
        }

        return days;
    }
}

/// <summary>
/// Leave paid out for payroll (contract <see cref="ILeaveEncashments"/>, slice S1.32 Build items 12 and 14): the <c>encashment</c> entries dated in
/// the range, with the rate basis the policy version of the entry names for its type.
/// </summary>
internal sealed class LeaveEncashmentReader(HrmDbContext db) : ILeaveEncashments
{
    public async Task<IReadOnlyList<LeaveEncashment>> ForAsync(IReadOnlyCollection<Guid> employmentIds, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(employmentIds);
        var ids = employmentIds.Distinct().ToList();
        var rows = await (from x in db.Set<LeaveLedger>().AsNoTracking()
                          join t in db.Set<LeaveType>().AsNoTracking() on x.LeaveTypeId equals t.Id
                          where ids.Contains(x.EmploymentId) && x.Kind == "encashment" && x.ReversesEntryId == null && x.EntryDate >= firstDay && x.EntryDate <= lastDay
                          orderby x.EmploymentId, x.EntryDate, x.Id
                          select new { Entry = x, t.Code, t.Unit })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var versions = rows.Where(r => r.Entry.PolicyVersionId is not null).Select(r => r.Entry.PolicyVersionId!.Value).Distinct().ToList();
        var parameters = await db.Set<PolicyVersion>().AsNoTracking().Where(v => versions.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Parameters, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new LeaveEncashment(r.Entry.Id, r.Entry.EmploymentId, r.Entry.LeaveTypeId, r.Code, r.Unit, r.Entry.LeaveYear, r.Entry.EntryDate, -r.Entry.Quantity,
            r.Entry.PolicyVersionId is { } v && parameters.TryGetValue(v, out var p) ? LeavePolicy.Parse(p.RootElement).Rules.GetValueOrDefault(r.Code)?.EncashmentRateBasis : null,
            r.Entry.SourceType == LeaveYearEndPosting.Source ? "year_end" : r.Entry.SourceType == LeaveTransfers.Source ? "transfer" : "other", r.Entry.SourceId))];
    }
}

/// <summary>
/// Compensatory off earned (slice S1.32 Build item 11, FR-HRM-LVE-08, ruling Q6): on <c>hrm.attendance.comp_off_earned</c> one <c>comp_off_earned</c>
/// entry on the company's comp-off leave type (the one the event names, or the only active one), in the type's unit (days, half days, or hours from the
/// minutes), expiring after the policy's <c>compOffExpiryDays</c>. The same source earns once (the ledger's unique index). Without a comp-off type the
/// event fails and is tried again later (never dropped, the operations alert shows it) until HR creates one; HR can also grant by hand.
/// </summary>
public sealed class CompOffConsumer(IServiceProvider services, IClock clock) : IEventConsumer
{
    public const string SourceType = "hrm.attendance.comp_off";

    public string Name => "hrm.leave_comp_off";

    public bool Accepts(string eventType) => eventType == LeaveEvents.CompOffEarned;

    public async Task HandleAsync(OutboxEvent message, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(transaction);
        var p = message.Payload;
        var employmentId = p.GetProperty("employmentId").GetGuid();
        var workDate = DateOnly.Parse(p.GetProperty("workDate").GetString()!, CultureInfo.InvariantCulture);
        var sourceId = p.TryGetProperty("sourceId", out var s) && s.ValueKind == JsonValueKind.String && Guid.TryParse(s.GetString(), out var sid) ? sid : message.Id;
        var days = p.TryGetProperty("days", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDecimal() : 0m;
        var minutes = p.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDecimal() : 0m;
        var code = p.TryGetProperty("leaveTypeCode", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var actor = message.Headers.ValueKind == JsonValueKind.Object && message.Headers.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.String && Guid.TryParse(a.GetString(), out var g) ? g : Guid.Empty;
        await using var db = await LeaveOnTransaction.OpenAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var e = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (e is null || e.Status == "cancelled")
        {
            return;
        }

        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => t.CompanyId == e.CompanyId && t.IsCompOff && t.IsActive && !t.IsDeleted).ToListAsync(cancellationToken).ConfigureAwait(false);
        var type = code is null ? (types.Count == 1 ? types[0] : null) : types.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
        if (type is null)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"compensatory off of employment {employmentId:D} for {workDate:yyyy-MM-dd}: the company has {types.Count} active comp-off leave types{(code is null ? string.Empty : " and none with the code " + code)}; create one (or name it), and the event is tried again"));
        }

        var quantity = type.Unit switch
        {
            "hour" => decimal.Round(minutes / 60m, 2),
            "half_day" => decimal.Floor(days * 2m) / 2m,
            _ => decimal.Floor(days),
        };
        if (quantity <= 0m)
        {
            return;
        }

        var policy = await new LeavePolicyReader(PolicyResolver.For(db, services)).AtAsync(e.Id, workDate, cancellationToken).ConfigureAwait(false);
        var rules = policy?.For(type.Code);
        var year = await new LeaveYearSetting(services.GetRequiredService<ISettings>()).YearAsync(e.CompanyId, rules, workDate, cancellationToken).ConfigureAwait(false);
        var expires = rules?.CompOffExpiryDays is { } n ? workDate.AddDays(n) : (DateOnly?)null;
        var entryDate = workDate;

        // Work in a leave year its year-end has closed earns into the next open year, from its first day, with the same expiry (review S1.32b finding 6).
        while (await LeaveYearEnds.ClosedAsync(db, e.CompanyId, type.Id, year.Year, cancellationToken).ConfigureAwait(false))
        {
            year = LeaveYears.For(year.Year + 1, year.Start.Month);
            entryDate = year.Start;
        }

        if (expires is { } last && last < entryDate)
        {
            return; // already expired before it could be used
        }

        var now = clock.UtcNow;
        await LeaveLedgerStore.LockAsync(db, e.Id, type.Id, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, expires_on, source_type, source_id,
                                          policy_version_id, reverses_entry_id, reason, created_at, created_by)
            values ({Guid.CreateVersion7(now)}, {e.CompanyId}, {e.Id}, {type.Id}, {year.Year}, {entryDate}, 'comp_off_earned', {quantity},
                    {expires}, {SourceType}, {sourceId}, {policy?.Resolution.Policy?.VersionId}, null, 'comp_off', {now}, {actor})
            on conflict (source_type, source_id, kind, leave_type_id) where source_id is not null do nothing
            """, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The job <c>hrm.leave_expiry</c> (slice S1.32 Build items 11 and 12; FR-HRM-LVE-03, -08): daily, posts what is left of each expiring lot
/// (carried-forward leave, compensatory off, a grant with an expiry date) the day after its last usable day: <c>lapse</c>, or <c>comp_off_expired</c>
/// for compensatory off, as <see cref="LeaveLots"/> works it out. Each lot expires once (source: the lot); a lot whose expiry falls after its leave
/// year's end is left to the year-end. Nothing is posted into a locked attendance period's day or a closed leave year.
/// </summary>
internal sealed class LeaveExpiryJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.leave_expiry";

    /// <summary>How far back the job looks for lots that expired (a stopped job catches up within this; a year-end posts what is still due).</summary>
    public const int LookBackDays = 400;

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var actor = job.Services.GetRequiredService<ICurrentActor>().UserId;
        var settings = job.Services.GetRequiredService<ISettings>();
        var locks = job.Services.GetRequiredService<IAttendancePeriodLock>();
        var posted = 0;
        await job.CommitChunkAsync(async (connection, transaction, ct) =>
        {
            await using var db = await LeaveOnTransaction.OpenAsync(job.Services, transaction, ct).ConfigureAwait(false);
            var applier = new EmploymentApplier(db, settings, PolicyResolver.For(db, job.Services));
            var policies = new LeavePolicyReader(PolicyResolver.For(db, job.Services));
            var years = new LeaveYearSetting(settings);
            var newest = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(1);
            // A lot used up before its expiry never gets an entry: one that expired and was posted more than the look-back ago is not looked at again
            // (review S1.32b finding 14); a lot posted late (an old expiry, posted recently) is still found by its posting time.
            var oldest = newest.AddDays(-LookBackDays);
            var postedSince = clock.UtcNow.AddDays(-LookBackDays);
            var lots = db.Set<LeaveLedger>().AsNoTracking().Where(x => x.ExpiresOn != null && x.ExpiresOn < newest && (x.ExpiresOn >= oldest || x.CreatedAt >= postedSince) && x.Quantity > 0m && x.ReversesEntryId == null
                && !db.Set<LeaveLedger>().Any(y => y.SourceType == LeaveLots.ExpirySource && y.SourceId == x.Id));
            if (job.CompanyId is { } company)
            {
                lots = lots.Where(x => x.CompanyId == company);
            }

            var groups = await lots.Select(x => new { x.CompanyId, x.EmploymentId, x.LeaveTypeId, x.LeaveYear, x.EntryDate }).ToListAsync(ct).ConfigureAwait(false);
            var todays = new Dictionary<Guid, DateOnly>();
            foreach (var g in groups.GroupBy(x => (x.CompanyId, x.EmploymentId, x.LeaveTypeId, x.LeaveYear)))
            {
                if (!todays.TryGetValue(g.Key.CompanyId, out var today))
                {
                    todays[g.Key.CompanyId] = today = await applier.TodayAsync(g.Key.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
                }

                var type = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == g.Key.LeaveTypeId, ct).ConfigureAwait(false);
                var first = g.Min(x => x.EntryDate);
                var rules = (await policies.AtAsync(g.Key.EmploymentId, first, ct).ConfigureAwait(false))?.For(type.Code);
                var span = await years.YearAsync(g.Key.CompanyId, rules, first, ct).ConfigureAwait(false);
                span = span.Year == g.Key.LeaveYear ? span : LeaveYears.For(g.Key.LeaveYear, span.Start.Month);
                if (await LeaveYearEnds.ClosedAsync(db, g.Key.CompanyId, g.Key.LeaveTypeId, g.Key.LeaveYear, ct).ConfigureAwait(false))
                {
                    continue;
                }

                await LeaveLedgerStore.LockAsync(db, g.Key.EmploymentId, g.Key.LeaveTypeId, ct).ConfigureAwait(false);
                var entries = await LeaveBalances.LinesAsync(db, g.Key.EmploymentId, g.Key.LeaveTypeId, g.Key.LeaveYear, ct).ConfigureAwait(false);
                foreach (var expiry in LeaveLots.Project(entries, span.End).Expiries.Where(x => x.On <= today))
                {
                    if (await locks.IsLockedAsync(g.Key.EmploymentId, expiry.On, ct).ConfigureAwait(false))
                    {
                        continue;
                    }

                    posted += await db.Database.ExecuteSqlInterpolatedAsync($"""
                        insert into hrm.leave_ledger (id, company_id, employment_id, leave_type_id, leave_year, entry_date, kind, quantity, expires_on, source_type, source_id,
                                                      policy_version_id, reverses_entry_id, reason, created_at, created_by)
                        values ({Guid.CreateVersion7(clock.UtcNow)}, {g.Key.CompanyId}, {g.Key.EmploymentId}, {g.Key.LeaveTypeId}, {g.Key.LeaveYear}, {expiry.On}, {expiry.Kind}, {-expiry.Quantity},
                                null, {LeaveLots.ExpirySource}, {LeaveLots.ExpirySourceId(expiry.LotId)}, null, null, 'expired', {clock.UtcNow}, {actor})
                        on conflict (source_type, source_id, kind, leave_type_id) where source_id is not null do nothing
                        """, ct).ConfigureAwait(false);
                }
            }
        }, null, 100m, cancellationToken).ConfigureAwait(false);
        await job.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"{posted} expired leave entries posted."), cancellationToken).ConfigureAwait(false);
    }
}
