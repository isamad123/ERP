using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// The daily job <c>hrm.pay_cutoff_reminders</c> (slice S1.34a Build item 3; FR-HRM-PAY-38): a period that has not opened or is open
/// and whose cut-off falls within <c>hrm.pay.cutoff_reminder_days</c> days (today included, in the company's business date) raises
/// <c>hrm.pay.cutoff_due</c> for the company's payroll officers. A reminder is raised once per period and cut-off date: moving the
/// date by hand reminds again. Switched-off pay groups are not reminded about. The change log names the job actor.
/// </summary>
internal sealed class PayCutoffReminderJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.pay_cutoff_reminders";
    public const string SettingKey = "hrm.pay.cutoff_reminder_days";

    private const int Chunk = 500;

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var settings = job.Services.GetRequiredService<ISettings>();
        var actor = job.Services.GetRequiredService<ICurrentActor>();
        var now = clock.UtcNow;
        var todays = new Dictionary<Guid, DateOnly>();
        var leads = new Dictionary<Guid, int>();
        var raised = 0;

        // A day either side of the UTC date covers every time zone; the company's own date decides below.
        var utcToday = DateOnly.FromDateTime(now.UtcDateTime);
        var from = utcToday.AddDays(-1);
        var until = utcToday.AddDays(31 + 1 + 1);
        Guid? after = null;
        while (true)
        {
            var read = 0;
            var pageStart = after;
            var count = 0;
            await job.CommitChunkAsync(async (connection, transaction, ct) =>
            {
                count = 0;
                await using var db = new HrmDbContext(job.Services.GetRequiredService<DbContextOptions<HrmDbContext>>());
                db.Database.SetDbConnection(connection, contextOwnsConnection: false);
                await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
                var applier = new EmploymentApplier(db, settings, Policies.PolicyResolver.For(db, job.Services));
                var query = db.Set<PayPeriod>().AsNoTracking().Where(p => (p.Status == "future" || p.Status == "open") && p.CutOffDate >= from && p.CutOffDate <= until);
                if (pageStart is { } cursor)
                {
                    query = query.Where(p => p.Id.CompareTo(cursor) > 0);
                }

                var page = await query.OrderBy(p => p.Id).Take(Chunk).ToListAsync(ct).ConfigureAwait(false);
                read = page.Count;
                var groups = await db.Set<PayGroup>().AsNoTracking().Where(g => page.Select(p => p.PayGroupId).Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct).ConfigureAwait(false);
                foreach (var p in page)
                {
                    after = p.Id;
                    if (!groups.TryGetValue(p.PayGroupId, out var group) || !group.IsActive || group.IsDeleted)
                    {
                        continue;
                    }

                    if (!todays.TryGetValue(p.CompanyId, out var today))
                    {
                        todays[p.CompanyId] = today = await applier.TodayAsync(p.CompanyId, now, ct).ConfigureAwait(false);
                    }

                    if (!leads.TryGetValue(p.CompanyId, out var lead))
                    {
                        var value = (await settings.GetAsync(SettingKey, new SettingContext(p.CompanyId, null), ct).ConfigureAwait(false)).Value;
                        leads[p.CompanyId] = lead = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 3;
                    }

                    if (today > p.CutOffDate || today < p.CutOffDate.AddDays(-lead))
                    {
                        continue;
                    }

                    var cutOff = p.CutOffDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var already = await db.Database.SqlQuery<bool>($"select exists (select 1 from hrm.outbox_messages where event_type = {PayEvents.CutoffDue} and aggregate_id = {p.Id} and payload->>'cut_off_date' = {cutOff}) as \"Value\"").SingleAsync(ct).ConfigureAwait(false);
                    if (already)
                    {
                        continue;
                    }

                    applier.RaiseFor(PayEvents.CutoffDue, PayEvents.AggregateType, p.Id, now, actor.UserId, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["pay_period_id"] = p.Id,
                        ["pay_group_id"] = p.PayGroupId,
                        ["pay_group_code"] = group.Code,
                        ["period_no"] = p.PeriodNo,
                        ["cut_off_date"] = cutOff,
                        ["company_id"] = p.CompanyId,
                        ["link"] = "/payroll/calendar",
                    });
                    count++;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }, null, null, cancellationToken).ConfigureAwait(false);
            raised += count;
            if (read < Chunk)
            {
                break;
            }
        }

        await job.LogAsync("info", $"{raised} cut-off reminders raised.", cancellationToken).ConfigureAwait(false);
    }
}
