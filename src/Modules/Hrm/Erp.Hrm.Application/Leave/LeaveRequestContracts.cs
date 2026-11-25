using System.Text.Json;
using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Leave;

/// <summary>
/// A leave request to work out, save or change (slice S1.32 Build items 7 and 9; FR-HRM-LVE-04). Without <see cref="EmploymentId"/> it is the
/// signed-in employee's own leave in the leave type's company; with one, HR applies for that employee (their scope decides; the acting user is
/// recorded, GHR-09). Parts default to <c>full</c> and the end to the start. <see cref="Hours"/> only for a type counted in hours.
/// <see cref="Submit"/> (default true) sends it for approval at once; false keeps a draft, for instance to attach a document first.
/// </summary>
public sealed record LeaveRequestInput(
    Guid? EmploymentId,
    Guid? LeaveTypeId,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? StartPart,
    string? EndPart,
    decimal? Hours,
    string? Reason,
    bool? Submit = null,
    JsonElement? Custom = null,
    long? RowVersion = null);

/// <summary>Submit a draft (or a request returned for correction) with the row version read.</summary>
public sealed record LeaveSubmitInput(long? RowVersion);

/// <summary>Cancel a draft or approved leave (FR-HRM-LVE-06), with a reason and the row version read.</summary>
public sealed record LeaveCancelInput(string? Reason, long? RowVersion);

/// <summary>
/// Leave requests (slice S1.32 Build items 7 to 9; FR-HRM-LVE-04 to -06, -16, FR-XM-26, FR-XM-32, GHR-09). Every answer is as for the person
/// services. The approval is the kernel workflow's (document types <c>hrm.leave_request</c> and <c>hrm.leave_cancellation</c>).
/// </summary>
public interface ILeaveRequestService
{
    /// <summary>The days, the total and the balance before and after, without saving (the balance impact before submitting, LVE-04).</summary>
    Task<PeopleResult> PreviewAsync(LeaveRequestInput input, CancellationToken cancellationToken);

    /// <summary>Saves a request as a draft or sends it for approval (numbered by the kernel numbering service when saved).</summary>
    Task<PeopleResult> CreateAsync(LeaveRequestInput input, CancellationToken cancellationToken);

    /// <summary>Changes a draft (dates, parts, hours, reason, type).</summary>
    Task<PeopleResult> UpdateAsync(Guid requestId, LeaveRequestInput input, CancellationToken cancellationToken);

    /// <summary>Sends a draft for approval, or a request returned for correction back to its route.</summary>
    Task<PeopleResult> SubmitAsync(Guid requestId, LeaveSubmitInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a draft, or approved leave: at once and the balance restored by reversal, or, when the policy says cancellations need approval,
    /// after that approval. A request waiting for approval is withdrawn through the approvals (<c>/api/wf/instances/{id}/withdraw</c>).
    /// </summary>
    Task<PeopleResult> CancelAsync(Guid requestId, LeaveCancelInput input, CancellationToken cancellationToken);

    /// <summary>Asks to change approved leave: a new request linked to the old one; once it is approved the old one is cancelled by reversal.</summary>
    Task<PeopleResult> ChangeAsync(Guid requestId, LeaveRequestInput input, CancellationToken cancellationToken);

    /// <summary>A request with its days (sandwich days marked), files, the ledger entries it made and who applied.</summary>
    Task<PeopleResult> DetailsAsync(Guid requestId, CancellationToken cancellationToken);
}

/// <summary>Confirm or roll back a year-end preview (slice S1.32 Build item 12, FR-HRM-LVE-11).</summary>
public sealed record LeaveYearEndDecision(string? Reason, long? RowVersion);

/// <summary>The leave year-end (FR-HRM-LVE-11): the preview is the job <c>hrm.leave_year_end</c>; reads are the generated <c>/api/hrm/leave-year-end-runs</c>.</summary>
public interface ILeaveYearEndService
{
    /// <summary>Posts the previewed carry-forward, payout and lapse, or holds the confirmation for a second person when the setting says so.</summary>
    Task<PeopleResult> ConfirmAsync(Guid runId, LeaveYearEndDecision decision, CancellationToken cancellationToken);

    /// <summary>Rolls back a preview: nothing was posted, nothing is.</summary>
    Task<PeopleResult> RollbackAsync(Guid runId, LeaveYearEndDecision decision, CancellationToken cancellationToken);
}

/// <summary>Which leave type of the new company receives the balance of a type of the old one (FR-XM-30).</summary>
public sealed record LeaveTransferMap(Guid? FromLeaveTypeId, Guid? ToLeaveTypeId);

/// <summary>The leave types HR maps for a transfer whose types had no match by code.</summary>
public sealed record LeaveTransferMapInput(IReadOnlyList<LeaveTransferMap>? Maps);

/// <summary>The leave side of a transfer between companies (slice S1.32 Build item 14, FR-XM-30, CR-095).</summary>
public interface ILeaveTransferService
{
    /// <summary>What the transfer did with each leave type of the old employment, and the types still waiting for HR's mapping.</summary>
    Task<PeopleResult> GetAsync(Guid employmentId, CancellationToken cancellationToken);

    /// <summary>Maps waiting types and posts their carry-over (only for the choice <c>carry</c>).</summary>
    Task<PeopleResult> MapAsync(Guid employmentId, LeaveTransferMapInput input, CancellationToken cancellationToken);
}
