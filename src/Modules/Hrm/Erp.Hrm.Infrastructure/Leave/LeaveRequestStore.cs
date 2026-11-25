using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Attendance;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>The entries of one employment, leave type and leave year as the expiry simulation reads them (<see cref="LeaveLots"/>).</summary>
internal static class LeaveBalances
{
    public static async Task<List<LotLine>> LinesAsync(HrmDbContext db, Guid employmentId, Guid leaveTypeId, int leaveYear, CancellationToken ct)
    {
        var rows = await db.Set<LeaveLedger>().AsNoTracking()
            .Where(x => x.EmploymentId == employmentId && x.LeaveTypeId == leaveTypeId && x.LeaveYear == leaveYear)
            .Select(x => new { x.Id, x.EntryDate, x.Quantity, x.ExpiresOn, x.ReversesEntryId, x.SourceType, x.SourceId, x.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(x => new LotLine(x.Id, x.EntryDate, x.Quantity, x.ExpiresOn, x.ReversesEntryId, x.SourceType == LeaveLots.ExpirySource ? x.SourceId : null, x.Kind))];
    }
}

/// <summary>
/// The usage of an approved request in the ledger (slice S1.32 Build items 7 and 9; FR-HRM-LVE-06, FR-XM-06): one <c>usage</c> entry per request,
/// dated on its first day, of the request's total in the type's unit, naming the request and the policy version; a cancellation or a change
/// reverses it by one <c>usage_reversal</c>. The ledger's unique index (source, kind, type) posts each once.
/// </summary>
internal static class LeaveUsage
{
    public const string Source = "hrm.leave_request";

    public static Task<LeaveLedger?> FindAsync(HrmDbContext db, Guid requestId, CancellationToken ct) =>
        db.Set<LeaveLedger>().AsNoTracking().FirstOrDefaultAsync(x => x.SourceType == Source && x.SourceId == requestId && x.Kind == "usage" && x.ReversesEntryId == null, ct);

    /// <summary>The quantity a request takes from the ledger: its hours for a type counted in hours, otherwise its days.</summary>
    public static decimal Quantity(LeaveRequest r, LeaveType t) => t.Counting == "hours" ? r.Hours ?? 0m : r.Days;

    public static LeaveLedger Usage(LeaveRequest r, LeaveType t, int leaveYear, Guid? policyVersionId, DateTimeOffset now, Guid actor) => new()
    {
        Id = Guid.CreateVersion7(now), CompanyId = r.CompanyId, EmploymentId = r.EmploymentId, LeaveTypeId = r.LeaveTypeId, LeaveYear = leaveYear, EntryDate = r.StartDate,
        Kind = "usage", Quantity = -Quantity(r, t), SourceType = Source, SourceId = r.Id, PolicyVersionId = policyVersionId, Reason = r.RequestNo, CreatedAt = now, CreatedBy = actor,
    };

    /// <summary>The reversal of a request's usage, dated <paramref name="on"/> (same leave year); null when it has no usage or it is already reversed.</summary>
    public static async Task<LeaveLedger?> ReversalAsync(HrmDbContext db, LeaveRequest r, DateOnly? on, string reason, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var usage = await FindAsync(db, r.Id, ct).ConfigureAwait(false);
        if (usage is null || await db.Set<LeaveLedger>().AsNoTracking().AnyAsync(x => x.ReversesEntryId == usage.Id, ct).ConfigureAwait(false))
        {
            return null;
        }

        return new LeaveLedger
        {
            Id = Guid.CreateVersion7(now), CompanyId = usage.CompanyId, EmploymentId = usage.EmploymentId, LeaveTypeId = usage.LeaveTypeId, LeaveYear = usage.LeaveYear,
            EntryDate = on is { } d && d < usage.EntryDate ? d : usage.EntryDate, Kind = "usage_reversal", Quantity = -usage.Quantity, SourceType = Source, SourceId = r.Id,
            PolicyVersionId = usage.PolicyVersionId, ReversesEntryId = usage.Id, Reason = reason, CreatedAt = now, CreatedBy = actor,
        };
    }

    /// <summary>Sets the status of a request and of its days (the days carry a copy, read by the database's daily total and by attendance).</summary>
    public static async Task SetStatusAsync(HrmDbContext db, LeaveRequest r, string status, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        r.Status = status;
        EmploymentApplier.Bump(r, now, actor);
        var days = await db.Set<LeaveRequestDay>().Where(d => d.LeaveRequestId == r.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var d in days.Where(d => d.RequestStatus != status))
        {
            d.RequestStatus = status;
            EmploymentApplier.Bump(d, now, actor);
        }
    }

    /// <summary>Asks the attendance engine to work out the request's days again (S1.29a contract; the event is saved with the change).</summary>
    public static void Requeue(HrmDbContext db, LeaveRequest r, string reason, DateTimeOffset now, Guid actor) =>
        AttendanceReprocessing.Add(db, r.EmploymentId, r.StartDate, r.EndDate, reason, now, actor);
}

/// <summary>Leave years closed by a confirmed year-end (slice S1.32 Build item 12): nothing more is posted to them for the types the run covered.</summary>
internal static class LeaveYearEnds
{
    public static async Task<bool> ClosedAsync(HrmDbContext db, Guid companyId, Guid leaveTypeId, int leaveYear, CancellationToken ct)
    {
        var type = leaveTypeId.ToString("D", CultureInfo.InvariantCulture);
        return await db.Database.SqlQuery<bool>($"""
            select exists (select 1 from hrm.leave_year_end_runs r
                           where r.company_id = {companyId} and r.leave_year = {leaveYear} and r.status = 'applied'
                             and r.summary -> 'types' @> jsonb_build_array(jsonb_build_object('leaveTypeId', {type}::text))) as "Value"
            """).SingleAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>The values a leave request gives its approval route (fields of the document types in <c>workflow.json</c>).</summary>
internal static class LeaveWorkflowValues
{
    public const string RequestType = "hrm.leave_request";
    public const string CancellationType = "hrm.leave_cancellation";
    public const string OwnerType = "hrm.leave_requests";

    public static Dictionary<string, JsonElement> Of(LeaveRequest r, Employment e, LeaveType t, LeaveTypeRules? rules, bool onBehalf)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["company"] = JsonSerializer.SerializeToElement(r.CompanyId.ToString()),
            ["employee_no"] = JsonSerializer.SerializeToElement(e.EmployeeNo),
            ["request_no"] = JsonSerializer.SerializeToElement(r.RequestNo),
            ["leave_type"] = JsonSerializer.SerializeToElement(t.Code),
            ["start_date"] = JsonSerializer.SerializeToElement(r.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["end_date"] = JsonSerializer.SerializeToElement(r.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["days"] = JsonSerializer.SerializeToElement(r.Days),
            ["is_paid"] = JsonSerializer.SerializeToElement(t.IsPaid),
            ["hr_approval"] = JsonSerializer.SerializeToElement(rules?.HrApproval ?? false),
            ["on_behalf"] = JsonSerializer.SerializeToElement(onBehalf),
        };
        if (e.UserRef is { } user)
        {
            values["employee"] = JsonSerializer.SerializeToElement(user.ToString());
        }

        return values;
    }
}
