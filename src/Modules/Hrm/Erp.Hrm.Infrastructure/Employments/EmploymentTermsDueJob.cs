using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// The daily job <c>hrm.employment_terms_due</c> (slice S1.20 Build item 13; FR-HRM-EMP-09, FR-HRM-EMP-10): active contracts whose
/// end date has passed (in the company's time zone) become <c>expired</c>; a contract within its renewal notice days of its end
/// (its own number, else <c>hrm.contracts.renewal_notice_days</c>) raises <c>hrm.contract_ending</c>, and an open probation within
/// <c>hrm.probation.reminder_days</c> of its last day (the extension's when extended) raises <c>hrm.probation_ending</c>, both for the
/// line manager and the company's HR administrators (the HR notification catalogue). A reminder is raised once per end date: an
/// extension or a renewal with a new date reminds again. Only current employments are reminded about. The change log names the
/// job actor.
/// </summary>
internal sealed class EmploymentTermsDueJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.employment_terms_due";

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    public string Code => JobCode;

    /// <summary>Rows per chunk; each chunk is its own transaction (review S1.20c finding 10).</summary>
    private const int Chunk = 500;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var settings = job.Services.GetRequiredService<ISettings>();
        var actor = job.Services.GetRequiredService<ICurrentActor>();
        var now = clock.UtcNow;
        var todays = new Dictionary<Guid, DateOnly>();
        var days = new Dictionary<(string Key, Guid Company), int>();
        var (expired, contracts, probations) = (0, 0, 0);

        // Contracts: expiry and renewal reminders, by id in chunks.
        var horizon = DateOnly.FromDateTime(now.UtcDateTime).AddDays(366);
        Guid? after = null;
        while (true)
        {
            var read = 0;
            var pageStart = after; // a retried chunk reads the same page again
            var (e1, c1) = (0, 0);
            await job.CommitChunkAsync(async (connection, transaction, ct) =>
            {
                (e1, c1) = (0, 0);
                var (db, applier) = Open(job, settings, connection);
                await using var _ = db;
                await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
                var query = db.Set<Contract>().Where(c => c.Status == "active" && c.EndDate != null && c.EndDate <= horizon);
                if (pageStart is { } cursor)
                {
                    query = query.Where(c => c.Id.CompareTo(cursor) > 0);
                }

                var page = await query.OrderBy(c => c.Id).Take(Chunk).ToListAsync(ct).ConfigureAwait(false);
                read = page.Count;
                foreach (var c in page)
                {
                    after = c.Id;
                    var today = await TodayAsync(applier, todays, c.CompanyId, now, ct).ConfigureAwait(false);
                    var end = c.EndDate!.Value;
                    if (end < today)
                    {
                        c.Status = "expired";
                        EmploymentApplier.Bump(c, now, actor.UserId);
                        e1++;
                        continue;
                    }

                    var notice = c.RenewalNoticeDays ?? await DaysAsync(settings, days, "hrm.contracts.renewal_notice_days", c.CompanyId, 30, ct).ConfigureAwait(false);
                    if (today >= end.AddDays(-notice) && await RemindAsync(db, applier, EmploymentEvents.ContractEnding, "hrm.contract", c.Id, c.EmploymentId, c.CompanyId, end, today, now, actor.UserId, ct).ConfigureAwait(false))
                    {
                        c1++;
                    }
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }, null, null, cancellationToken).ConfigureAwait(false);
            (expired, contracts) = (expired + e1, contracts + c1);
            if (read < Chunk)
            {
                break;
            }
        }

        // Probation: reminders before the last day, by id in chunks.
        after = null;
        while (true)
        {
            var read = 0;
            var pageStart = after;
            var p1 = 0;
            await job.CommitChunkAsync(async (connection, transaction, ct) =>
            {
                p1 = 0;
                var (db, applier) = Open(job, settings, connection);
                await using var _ = db;
                await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
                var query = db.Set<ProbationRecord>().AsNoTracking().Where(p => p.Status == "in_progress" || p.Status == "extended");
                if (pageStart is { } cursor)
                {
                    query = query.Where(p => p.Id.CompareTo(cursor) > 0);
                }

                var page = await query.OrderBy(p => p.Id).Take(Chunk).ToListAsync(ct).ConfigureAwait(false);
                read = page.Count;
                foreach (var p in page)
                {
                    after = p.Id;
                    var today = await TodayAsync(applier, todays, p.CompanyId, now, ct).ConfigureAwait(false);
                    var last = p.ExtendedTo ?? p.EndDate;
                    var lead = await DaysAsync(settings, days, "hrm.probation.reminder_days", p.CompanyId, 14, ct).ConfigureAwait(false);
                    if (today <= last && today >= last.AddDays(-lead)
                        && await RemindAsync(db, applier, EmploymentEvents.ProbationEnding, "hrm.probation_record", p.Id, p.EmploymentId, p.CompanyId, last, today, now, actor.UserId, ct).ConfigureAwait(false))
                    {
                        p1++;
                    }
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }, null, null, cancellationToken).ConfigureAwait(false);
            probations += p1;
            if (read < Chunk)
            {
                break;
            }
        }

        await job.LogAsync("info", $"{expired} contracts expired, {contracts} contract and {probations} probation reminders raised.", cancellationToken).ConfigureAwait(false);
    }

    private static (HrmDbContext Db, EmploymentApplier Applier) Open(IJobExecution job, ISettings settings, System.Data.Common.DbConnection connection)
    {
        var db = new HrmDbContext(job.Services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(connection, contextOwnsConnection: false);
        return (db, new EmploymentApplier(db, settings));
    }

    /// <summary>The company's business date, read once per company and run.</summary>
    private static async Task<DateOnly> TodayAsync(EmploymentApplier applier, Dictionary<Guid, DateOnly> todays, Guid company, DateTimeOffset now, CancellationToken ct)
    {
        if (!todays.TryGetValue(company, out var today))
        {
            todays[company] = today = await applier.TodayAsync(company, now, ct).ConfigureAwait(false);
        }

        return today;
    }

    /// <summary>A number of days from a company setting, read once per company and run.</summary>
    private static async Task<int> DaysAsync(ISettings settings, Dictionary<(string, Guid), int> cache, string key, Guid company, int fallback, CancellationToken ct)
    {
        if (!cache.TryGetValue((key, company), out var days))
        {
            var value = (await settings.GetAsync(key, new SettingContext(company, null), ct).ConfigureAwait(false)).Value;
            cache[(key, company)] = days = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : fallback;
        }

        return days;
    }

    /// <summary>
    /// Raises one reminder for an end date of a current employment, unless one was raised for that end date already: the outbox keeps
    /// delivered events for 30 days, so a notice period longer than that may remind again after 30 days (R-225). Payload in snake_case,
    /// ids, codes and dates only: the notification texts and the deep link come from the HR catalogue.
    /// </summary>
    private static async Task<bool> RemindAsync(HrmDbContext db, EmploymentApplier applier, string type, string aggregate, Guid id, Guid employmentId, Guid companyId, DateOnly end,
        DateOnly today, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var endText = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var already = await db.Database.SqlQuery<bool>($"select exists (select 1 from hrm.outbox_messages where event_type = {type} and aggregate_id = {id} and payload->>'end_date' = {endText}) as \"Value\"").SingleAsync(ct).ConfigureAwait(false);
        if (already)
        {
            return false;
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, ct).ConfigureAwait(false);
        if (employment is null || !CurrentStatuses.Contains(employment.Status))
        {
            return false;
        }

        // The line manager on the day, through the manager's user link (R-220: without a link only HR is reminded).
        var manager = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employmentId && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo > today))
            .Select(a => a.ManagerEmploymentId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var managerUser = manager is { } m
            ? await db.Set<Employment>().AsNoTracking().Where(e => e.Id == m).Select(e => e.UserRef).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [aggregate == "hrm.contract" ? "contract_id" : "probation_id"] = id,
            ["employment_id"] = employmentId,
            ["company_id"] = companyId,
            ["employee_no"] = employment.EmployeeNo,
            ["end_date"] = endText,
            ["manager_user_id"] = managerUser,
            ["link"] = $"/hrm/employments/{employmentId}",
        };
        applier.RaiseFor(type, aggregate, id, now, actor, payload);
        return true;
    }
}
