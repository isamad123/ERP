using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Leave;

/// <summary>
/// A leave type to create or change (slice S1.32 Build item 1, FR-HRM-LVE-01). On a change only the fields sent change; <see cref="IsActive"/>
/// false retires the type (never deleted), true brings it back.
/// </summary>
public sealed record LeaveTypeRequest(
    Guid? CompanyId,
    string? Code,
    Dictionary<string, string>? NameI18n,
    bool? IsPaid,
    string? Counting,
    string? Unit,
    bool? RequiresDocument,
    bool? IsCompOff,
    bool? IsUnpaidDeduction,
    bool? IsActive,
    long? RowVersion);

/// <summary>
/// A manual ledger entry (Build item 4): an opening balance, a grant or an adjustment of one leave type, dated, with a reason. The quantity is
/// in the type's unit; positive adds, negative takes away.
/// </summary>
public sealed record LeavePostRequest(Guid? LeaveTypeId, string? Kind, decimal? Quantity, DateOnly? EntryDate, DateOnly? ExpiresOn, string? Reason);

/// <summary>The reversal of one entry (FR-XM-06): dated on the entry's own date unless another date of the same leave year is given.</summary>
public sealed record LeaveReverseRequest(DateOnly? EntryDate, string? Reason);

/// <summary>Leave types (Build item 1): create and change; reads are the generated <c>/api/hrm/leave-types</c>.</summary>
public interface ILeaveTypeService
{
    Task<PeopleResult> CreateAsync(LeaveTypeRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid leaveTypeId, LeaveTypeRequest request, CancellationToken cancellationToken);
}

/// <summary>The leave ledger and balances (Build items 4 and 5).</summary>
public interface ILeaveLedgerService
{
    /// <summary>Posts an opening balance, grant or adjustment for an employment (HR, within the caller's data scope).</summary>
    Task<PeopleResult> PostAsync(Guid employmentId, LeavePostRequest request, CancellationToken cancellationToken);

    /// <summary>Reverses one entry by a new entry of the opposite quantity (FR-XM-06).</summary>
    Task<PeopleResult> ReverseAsync(Guid entryId, LeaveReverseRequest request, CancellationToken cancellationToken);

    /// <summary>The balances of an employment as of a date (default: the company's today), each with the entries that make it.</summary>
    Task<PeopleResult> BalancesAsync(Guid employmentId, DateOnly? asOf, Guid? leaveTypeId, CancellationToken cancellationToken);

    /// <summary>The signed-in employee's own balances: the employment comes from the token, never from the request.</summary>
    Task<PeopleResult> MyBalancesAsync(Guid? companyId, DateOnly? asOf, Guid? leaveTypeId, CancellationToken cancellationToken);
}

/// <summary>The recompute of balances from the ledger and the policies (Build item 6, FR-HRM-LVE-14): differences listed, nothing changed.</summary>
public interface ILeaveRecomputeService
{
    Task<PeopleResult> RecomputeAsync(Guid employmentId, Guid? leaveTypeId, CancellationToken cancellationToken);
}
