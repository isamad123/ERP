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

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var settings = job.Services.GetRequiredService<ISettings>();
        var actor = job.Services.GetRequiredService<ICurrentActor>();
        var (expired, contracts, probations) = (0, 0, 0);
        await job.CommitChunkAsync(async (connection, transaction, ct) =>
        {
            (expired, contracts, probations) = (0, 0, 0);
            await using var db = new HrmDbContext(job.Services.GetRequiredService<DbContextOptions<HrmDbContext>>());
            db.Database.SetDbConnection(connection, contextOwnsConnection: false);
            await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
            var applier = new EmploymentApplier(db, settings);
            var now = clock.UtcNow;
            var todays = new Dictionary<Guid, DateOnly>();
            async Task<DateOnly> TodayAsync(Guid company)
            {
                if (!todays.TryGetValue(company, out var today))
                {
                    todays[company] = today = await applier.TodayAsync(company, now, ct).ConfigureAwait(false);
                }

                return today;
            }

            async Task<int> DaysAsync(string key, Guid company, int fallback)
            {
                var value = (await settings.GetAsync(key, new SettingContext(company, null), ct).ConfigureAwait(false)).Value;
                return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : fallback;
            }

            // Contracts: expiry and renewal reminders.
            var horizon = DateOnly.FromDateTime(now.UtcDateTime).AddDays(366);
            var active = await db.Set<Contract>().Where(c => c.Status == "active" && c.EndDate != null && c.EndDate <= horizon).OrderBy(c => c.EndDate).ThenBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
            foreach (var c in active)
            {
                var today = await TodayAsync(c.CompanyId).ConfigureAwait(false);
                var end = c.EndDate!.Value;
                if (end < today)
                {
                    c.Status = "expired";
                    EmploymentApplier.Bump(c, now, actor.UserId);
                    expired++;
                    continue;
                }

                var notice = c.RenewalNoticeDays ?? await DaysAsync("hrm.contracts.renewal_notice_days", c.CompanyId, 30).ConfigureAwait(false);
                if (today >= end.AddDays(-notice) && await RemindAsync(db, applier, EmploymentEvents.ContractEnding, "hrm.contract", c.Id, c.EmploymentId, c.CompanyId, end, today, now, actor.UserId, ct).ConfigureAwait(false))
                {
                    contracts++;
                }
            }

            // Probation: reminders before the last day.
            var open = await db.Set<ProbationRecord>().AsNoTracking().Where(p => p.Status == "in_progress" || p.Status == "extended").OrderBy(p => p.Id).ToListAsync(ct).ConfigureAwait(false);
            foreach (var p in open)
            {
                var today = await TodayAsync(p.CompanyId).ConfigureAwait(false);
                var last = p.ExtendedTo ?? p.EndDate;
                var lead = await DaysAsync("hrm.probation.reminder_days", p.CompanyId, 14).ConfigureAwait(false);
                if (today <= last && today >= last.AddDays(-lead)
                    && await RemindAsync(db, applier, EmploymentEvents.ProbationEnding, "hrm.probation_record", p.Id, p.EmploymentId, p.CompanyId, last, today, now, actor.UserId, ct).ConfigureAwait(false))
                {
                    probations++;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }, null, null, cancellationToken).ConfigureAwait(false);
        await job.LogAsync("info", $"{expired} contracts expired, {contracts} contract and {probations} probation reminders raised.", cancellationToken).ConfigureAwait(false);
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
