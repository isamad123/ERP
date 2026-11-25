using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Leave;
using Erp.Kernel.Contracts.Attendance;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// What follows from the day results the engine has just written (slice S1.29b, Build items 7, 9 and 10; FR-HRM-ATT-12, ATT-13, LVE-08, FR-XM-26),
/// in the engine's own transaction. <b>Overtime:</b> each written day keeps one overtime entry in step with its overtime minutes: a day type with
/// post-approval gets a worked-out entry (<c>calculated</c>, to be claimed) or loses one nobody claimed; a claimed, asked-for or approved entry
/// follows the day (its minutes, day row and multiplier); an approved pre-approval the policy converts earns comp-off once the day shows the minutes
/// worked. <b>Lateness:</b> each calendar month touched is planned again with <see cref="LatenessAndOvertime.Plan"/>: a penalty due and missing is
/// applied (leave deducted, a pay deduction input, or a warning), an applied one no longer due is cancelled (its leave given back), one whose action
/// changed is cancelled and applied anew, and one still due follows its day. Waived penalties and days of locked periods are never touched.
/// </summary>
internal sealed class AttendanceConsequences(HrmDbContext db, IServiceProvider services, IAttendancePeriodLock locks)
{
    public const string ConvertedReason = "comp_off";

    private readonly Dictionary<Guid, AttendancePolicy?> policies = [];

    /// <summary>Applies the consequences of the written days of <paramref name="runs"/>.</summary>
    public async Task AfterAsync(Employment e, IReadOnlyList<DayRun> runs, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(runs);
        var written = runs.Where(r => r.Status == DayRun.Written && r.DayId is not null && r.Outcome is not null).ToList();
        if (written.Count == 0)
        {
            return;
        }

        foreach (var run in written)
        {
            await OvertimeAsync(e, run.Date, run.DayId!.Value, run.Outcome!, now, actor, ct).ConfigureAwait(false);
        }

        foreach (var month in written.Select(r => LatenessAndOvertime.Month(r.Date).First).Distinct().Order())
        {
            await PenaltiesAsync(e, month, now, actor, ct).ConfigureAwait(false);
        }

        db.ChangeTracker.Clear();
    }

    /// <summary>The attendance policy of a stored version (cached); null when the version cannot be read.</summary>
    public async Task<AttendancePolicy?> PolicyAsync(Guid versionId, CancellationToken ct)
    {
        if (policies.TryGetValue(versionId, out var known))
        {
            return known;
        }

        var parameters = await db.Set<PolicyVersion>().AsNoTracking().Where(v => v.Id == versionId).Select(v => v.Parameters).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var policy = parameters is null ? null : AttendancePolicy.Read(parameters.RootElement);
        policies[versionId] = policy;
        return policy;
    }

    // ------------------------------------------------------------------ overtime (Build items 9 and 10)

    private async Task OvertimeAsync(Employment e, DateOnly date, Guid dayId, DayOutcome outcome, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var day = await db.Set<AttendanceDay>().AsNoTracking().Where(d => d.Id == dayId && d.WorkDate == date).Select(d => new { d.PolicyVersionId }).FirstAsync(ct).ConfigureAwait(false);
        var policy = day.PolicyVersionId is { } versionId ? await PolicyAsync(versionId, ct).ConfigureAwait(false) : null;
        var rule = policy?.OvertimeFor(outcome.DayType);
        var entries = await db.Set<OvertimeEntry>().Where(x => x.EmploymentId == e.Id && x.WorkDate == date).ToListAsync(ct).ConfigureAwait(false);
        var active = entries.FirstOrDefault(x => x.Status != "rejected");
        var minutes = outcome.OvertimeMinutes;
        if (active is null)
        {
            // Overtime claimed after the day gets a worked-out entry; a rejected claim is not raised again for the same day.
            if (minutes > 0 && rule is { Approval: OvertimeRule.Post } && entries.Count == 0)
            {
                db.Add(new OvertimeEntry
                {
                    Id = Guid.CreateVersion7(now), CompanyId = e.CompanyId, EmploymentId = e.Id, WorkDate = date, DayType = outcome.DayType, Minutes = minutes,
                    RateMultiplier = rule.Multiplier, AttendanceDayId = dayId, PolicyVersionId = day.PolicyVersionId, Status = "calculated",
                    CreatedAt = now, CreatedBy = actor, UpdatedAt = now, UpdatedBy = actor, RowVersion = 1,
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return;
        }

        if (active.Status is "paid" or "converted_to_comp_off")
        {
            return; // decided and used: a later change of the day is a retroactive matter (S1.31)
        }

        if (active.Status == "calculated" && (minutes == 0 || rule is not { Approval: OvertimeRule.Post }))
        {
            db.Remove(active); // its overtime went away (or the policy no longer has it claimed afterwards) before anyone claimed it
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        if (active.Minutes != minutes || active.AttendanceDayId != dayId || active.DayType != outcome.DayType || (rule is not null && active.RateMultiplier != rule.Multiplier))
        {
            active.Minutes = minutes;
            active.AttendanceDayId = dayId;
            active.DayType = outcome.DayType;
            if (rule is not null)
            {
                active.RateMultiplier = rule.Multiplier;
                active.PolicyVersionId = day.PolicyVersionId;
            }

            Employments.EmploymentApplier.Bump(active, now, actor);
        }

        // An approved pre-approval the policy gives as comp-off earns it once the day shows the minutes worked (never more than approved).
        if (active.Status == "approved" && active.RequestedMinutes is not null && minutes > 0 && rule is { Compensation: OvertimeRule.CompOff })
        {
            Convert(active, Math.Min(minutes, active.ApprovedMinutes ?? 0), rule, now, actor);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns approved overtime into compensatory off (LVE-08, S1.32 Q6): the entry becomes <c>converted_to_comp_off</c> and the event
    /// <c>hrm.attendance.comp_off_earned</c> is written in the same transaction (the leave engine posts the earning once per entry).
    /// </summary>
    public static void Convert(HrmDbContext db, OvertimeEntry entry, int minutes, OvertimeRule rule, DateTimeOffset now, Guid actor)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(rule);
        entry.Status = "converted_to_comp_off";
        Employments.EmploymentApplier.Bump(entry, now, actor);
        db.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            EventType = LeaveEvents.CompOffEarned,
            AggregateType = AttendanceEngineEvents.AggregateType,
            AggregateId = entry.EmploymentId,
            Payload = JsonSerializer.SerializeToDocument(new
            {
                employmentId = entry.EmploymentId,
                companyId = entry.CompanyId,
                workDate = entry.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                days = LatenessAndOvertime.CompOffDays(minutes, rule),
                minutes,
                sourceId = entry.Id,
                leaveTypeCode = rule.CompOffLeaveTypeCode,
            }, JsonSerializerOptions.Web),
            Headers = JsonSerializer.SerializeToDocument(new { actor }, JsonSerializerOptions.Web),
        });
    }

    private void Convert(OvertimeEntry entry, int minutes, OvertimeRule rule, DateTimeOffset now, Guid actor) => Convert(db, entry, minutes, rule, now, actor);

    // ------------------------------------------------------------------ lateness (Build item 7)

    private async Task PenaltiesAsync(Employment e, DateOnly month, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var (first, last) = LatenessAndOvertime.Month(month);
        var rows = await db.Set<AttendanceDay>().AsNoTracking()
            .Where(d => d.EmploymentId == e.Id && d.WorkDate >= first && d.WorkDate <= last && d.SupersededAt == null && d.PolicyVersionId != null)
            .Select(d => new { d.Id, d.WorkDate, d.PeriodId, d.PolicyVersionId, d.Result, d.LateMinutes, d.EarlyMinutes })
            .ToListAsync(ct).ConfigureAwait(false);
        var days = new List<PenaltyDay>(rows.Count);
        foreach (var r in rows)
        {
            var policy = await PolicyAsync(r.PolicyVersionId!.Value, ct).ConfigureAwait(false);
            days.Add(new PenaltyDay(r.WorkDate, r.Id, r.PeriodId, r.PolicyVersionId.Value, r.Result, r.LateMinutes, r.EarlyMinutes, policy?.LatePenalty, policy?.EarlyPenalty));
        }

        var planned = LatenessAndOvertime.Plan(days);
        var existing = await db.Set<AttendancePenalty>()
            .Where(p => p.EmploymentId == e.Id && p.WorkDate >= first && p.WorkDate <= last && p.Status != "cancelled")
            .ToListAsync(ct).ConfigureAwait(false);
        var locked = new Dictionary<DateOnly, bool>();
        async Task<bool> LockedAsync(DateOnly d)
        {
            if (!locked.TryGetValue(d, out var value))
            {
                locked[d] = value = await locks.IsLockedAsync(e.Id, d, ct).ConfigureAwait(false);
            }

            return value;
        }

        foreach (var p in existing.Where(p => p.Status == "applied"))
        {
            if (await LockedAsync(p.WorkDate).ConfigureAwait(false))
            {
                continue; // FR-XM-26: a locked day's penalty never changes
            }

            var due = planned.FirstOrDefault(x => x.Day.Date == p.WorkDate && x.Kind == p.Kind);
            if (due is not null && await SameEffectAsync(e, p, due, ct).ConfigureAwait(false))
            {
                Follow(p, due, now, actor);
                continue;
            }

            if (!await LeavePenaltyUsage.ReversibleAsync(db, p.LeaveEntryId, ct).ConfigureAwait(false))
            {
                continue; // its leave year is closed: the deduction stands (R-313)
            }

            p.LeaveReversalId = await LeavePenaltyUsage.ReverseAsync(db, p.LeaveEntryId, "attendance_penalty_cancelled", now, actor, ct).ConfigureAwait(false);
            p.Status = "cancelled";
            Employments.EmploymentApplier.Bump(p, now, actor);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        foreach (var due in planned)
        {
            // One applied or waived penalty per day and kind (a waived one is the record and stays); one cancelled above is replaced.
            if (existing.Exists(p => p.WorkDate == due.Day.Date && p.Kind == due.Kind && p.Status != "cancelled"))
            {
                continue;
            }

            if (await LockedAsync(due.Day.Date).ConfigureAwait(false))
            {
                continue;
            }

            await ApplyAsync(e, due, now, actor, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Whether an applied penalty already does what the plan says (same action, same leave type and units): then it only follows its day.</summary>
    private async Task<bool> SameEffectAsync(Employment e, AttendancePenalty p, PlannedPenalty due, CancellationToken ct)
    {
        if (p.Action != due.Rule.Action)
        {
            return false;
        }

        if (due.Rule.Action != PenaltyRule.Leave)
        {
            return true;
        }

        if (p.LeaveQuantity != due.Rule.LeaveUnits || p.LeaveEntryId is null)
        {
            return false;
        }

        var type = await LeaveTypeAsync(e, due.Rule.LeaveTypeCode!, ct).ConfigureAwait(false);
        return type == p.LeaveTypeId;
    }

    private async Task<Guid?> LeaveTypeAsync(Employment e, string code, CancellationToken ct)
    {
        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => t.CompanyId == e.CompanyId && t.IsActive && !t.IsDeleted).Select(t => new { t.Id, t.Code }).ToListAsync(ct).ConfigureAwait(false);
        return types.Find(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static void Follow(AttendancePenalty p, PlannedPenalty due, DateTimeOffset now, Guid actor)
    {
        var explanation = JsonSerializer.SerializeToDocument(Explanation(LatenessAndOvertime.Explain(due, KeyOf(p))));
        if (p.Minutes == due.Minutes && p.Occurrence == due.Occurrence && p.AttendanceDayId == due.Day.DayId && p.PeriodId == due.Day.PeriodId
            && p.PolicyVersionId == due.Day.PolicyVersionId && JsonElement.DeepEquals(p.Explanation.RootElement, explanation.RootElement))
        {
            return;
        }

        p.Minutes = due.Minutes;
        p.Occurrence = due.Occurrence;
        p.AttendanceDayId = due.Day.DayId;
        p.PeriodId = due.Day.PeriodId;
        p.PolicyVersionId = due.Day.PolicyVersionId;
        p.Explanation = explanation;
        Employments.EmploymentApplier.Bump(p, now, actor);
    }

    /// <summary>The problem key a penalty's explanation carries (a missing leave type or a closed year), so a followed penalty keeps it.</summary>
    private static string? KeyOf(AttendancePenalty p)
    {
        var key = p.Explanation.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
        return key is LatenessAndOvertime.PenaltyNoLeaveType or LatenessAndOvertime.PenaltyYearClosed ? key : null;
    }

    private async Task ApplyAsync(Employment e, PlannedPenalty due, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var id = Guid.CreateVersion7(now);
        PenaltyLeavePosting? leave = null;
        if (due.Rule.Action == PenaltyRule.Leave)
        {
            leave = await LeavePenaltyUsage.PostAsync(db, services, e, id, due.Day.Date, due.Rule.LeaveTypeCode!, due.Rule.LeaveUnits!.Value, now, actor, ct).ConfigureAwait(false);
        }

        db.Add(new AttendancePenalty
        {
            Id = id, CompanyId = e.CompanyId, EmploymentId = e.Id, WorkDate = due.Day.Date, Kind = due.Kind, Minutes = due.Minutes, Occurrence = due.Occurrence,
            Action = due.Rule.Action, LeaveTypeId = leave?.LeaveTypeId, LeaveQuantity = due.Rule.Action == PenaltyRule.Leave ? due.Rule.LeaveUnits : null,
            LeaveEntryId = leave?.EntryId, AttendanceDayId = due.Day.DayId, PeriodId = due.Day.PeriodId, PolicyVersionId = due.Day.PolicyVersionId,
            Explanation = JsonSerializer.SerializeToDocument(Explanation(LatenessAndOvertime.Explain(due, leave?.ProblemKey))), Status = "applied",
            CreatedAt = now, CreatedBy = actor, UpdatedAt = now, UpdatedBy = actor, RowVersion = 1,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A penalty's explanation: the line's key and values, as a day's explanation stores them (translated at display).</summary>
    public static System.Text.Json.Nodes.JsonObject Explanation(ExplanationLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var whole = new DayExplanation(line.Key, line, [], null).ToJson();
        return new System.Text.Json.Nodes.JsonObject { ["key"] = whole["key"]!.DeepClone(), ["params"] = whole["params"]!.DeepClone() };
    }
}
