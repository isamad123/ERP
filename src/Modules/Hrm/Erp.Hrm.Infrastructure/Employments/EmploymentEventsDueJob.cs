using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// The daily job <c>hrm.employment_events_due</c> (slice S1.20 ruling Q12): approved events whose date has come (in the company's
/// time zone) become effective, an employment whose joining date has come moves from <c>pre_joining</c> to <c>active</c>, and each
/// such event raises <c>hrm.employee_changed</c> once. Reads were already as of the date; consumers act only from it. Chunks of
/// 200 events, each in its own transaction with the checkpoint; the change log names the job actor.
/// </summary>
internal sealed class EmploymentEventsDueJob(IClock clock) : IJobHandler
{
    public const string JobCode = "hrm.employment_events_due";

    private const int Chunk = 200;

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var settings = job.Services.GetRequiredService<ISettings>();
        var actor = job.Services.GetRequiredService<ICurrentActor>();
        var done = 0;
        // A cursor over (date, id) pages past events not yet due in their company's time zone (review S1.20b finding 11), so one
        // company's events a day ahead never hold back another company's due ones.
        (DateOnly Date, Guid Id)? after = null;
        while (true)
        {
            var moved = 0;
            var read = 0;
            var pageStart = after; // a retried chunk reads the same page again
            await job.CommitChunkAsync(async (connection, transaction, ct) =>
            {
                await using var db = new HrmDbContext(job.Services.GetRequiredService<DbContextOptions<HrmDbContext>>());
                db.Database.SetDbConnection(connection, contextOwnsConnection: false);
                await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
                var applier = new EmploymentApplier(db, settings);
                var now = clock.UtcNow;

                // A day ahead of UTC at most: the company's own date decides below.
                var horizon = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
                var query = db.Set<EmploymentEvent>().AsNoTracking().Where(e => e.Status == "approved" && e.EffectiveDate <= horizon);
                if (pageStart is { } cursor)
                {
                    query = query.Where(e => e.EffectiveDate > cursor.Date || (e.EffectiveDate == cursor.Date && e.Id.CompareTo(cursor.Id) > 0));
                }

                var due = await query.OrderBy(e => e.EffectiveDate).ThenBy(e => e.Id).Take(Chunk).ToListAsync(ct).ConfigureAwait(false);
                read = due.Count;
                var todays = new Dictionary<Guid, DateOnly>();
                foreach (var e in due)
                {
                    after = (e.EffectiveDate, e.Id);

                    if (!todays.TryGetValue(e.CompanyId, out var today))
                    {
                        todays[e.CompanyId] = today = await applier.TodayAsync(e.CompanyId, now, ct).ConfigureAwait(false);
                    }

                    if (e.EffectiveDate > today)
                    {
                        continue;
                    }

                    // The employment's lock, as the change service and the approval take it (review S1.20b finding 6); the event is
                    // read again under it and moved only if it is still approved.
                    await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {e.EmploymentId} for update", ct).ConfigureAwait(false);
                    var employment = await db.Set<Employment>().FirstAsync(x => x.Id == e.EmploymentId, ct).ConfigureAwait(false);
                    var events = await applier.EventsAsync(employment.Id, ct).ConfigureAwait(false);
                    var tracked = events.First(x => x.Id == e.Id);
                    await db.Entry(tracked).ReloadAsync(ct).ConfigureAwait(false);
                    if (tracked.Status != "approved")
                    {
                        continue;
                    }

                    tracked.Status = "effective";
                    EmploymentApplier.Bump(tracked, now, actor.UserId);
                    await applier.ApplyEmploymentFieldsAsync(employment, tracked, today, ct).ConfigureAwait(false);
                    if (db.Entry(employment).State == EntityState.Modified)
                    {
                        employment.RowVersion++;
                        EmploymentApplier.Stamp(employment, now, actor.UserId);
                    }

                    applier.RaiseChanged(employment, tracked, events, now, actor.UserId);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    moved++;
                }
            }, null, null, cancellationToken).ConfigureAwait(false);
            done += moved;
            if (read < Chunk)
            {
                break;
            }
        }

        await job.LogAsync("info", $"{done} employment events took effect.", cancellationToken).ConfigureAwait(false);
    }
}
