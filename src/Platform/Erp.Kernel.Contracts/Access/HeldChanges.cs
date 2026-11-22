using System.Data.Common;

namespace Erp.Kernel.Contracts.Access;

/// <summary>
/// A business module's change to hold for a second person (maker-checker, FR-KRN-IAM-14; slice S1.20a, CR-076 S1.20a detail d):
/// its kind (for example <c>hrm.person.merge</c>), the record it is about and what to apply, as the module's own JSON.
/// </summary>
/// <param name="Kind">The change kind; a registered <see cref="IHeldChangeHandler"/> with this kind applies it on approval.</param>
/// <param name="SubjectType">The table of the record (for example <c>hrm.persons</c>).</param>
/// <param name="SubjectId">The record.</param>
/// <param name="Payload">What to apply, as a JSON object the module's handler reads back.</param>
/// <param name="Reason">The requester's reason, shown to the approver.</param>
public sealed record HeldChangeRequest(string Kind, string SubjectType, Guid SubjectId, string Payload, string? Reason);

/// <summary>A pending held change as the handler sees it when it is checked, described or applied.</summary>
public sealed record HeldChange(Guid RequestId, string Kind, Guid RequestedBy, string Payload, string? Reason);

/// <summary>A refusal of a held change: a localization key, and whether it is a permission refusal (403) rather than a rule (400).</summary>
public sealed record HeldChangeProblem(string ErrorKey, bool Forbidden = false);

/// <summary>
/// One line of "what will change" in the approvals inbox (slice S1.12d): <see cref="Label"/> is a localization key; the value is
/// <see cref="Text"/>, <see cref="Date"/>, <see cref="Names"/> (a name in each language) or <see cref="Items"/>, as
/// <see cref="Type"/> says (<c>text</c>, <c>date</c>, <c>names</c>, <c>items</c>, <c>key</c>).
/// </summary>
public sealed record HeldChangeDetail(string Label, string Type, string? Text = null, DateOnly? Date = null, IReadOnlyDictionary<string, string>? Names = null, IReadOnlyList<string>? Items = null);

/// <summary>What a held change is about (by name in each language) and what it changes.</summary>
public sealed record HeldChangeDescription(IReadOnlyDictionary<string, string>? Subject, IReadOnlyList<HeldChangeDetail> Details);

/// <summary>Holds a module's change for a second person (the kernel's maker-checker requests).</summary>
public interface IHeldChanges
{
    /// <summary>
    /// Stores the change as a pending request in the caller's open transaction (so it is held only if the caller commits) and
    /// raises <c>iam.maker_checker_requested</c>. Returns the request id. A handler for the kind must be registered.
    /// </summary>
    Task<Guid> SubmitAsync(HeldChangeRequest request, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>
/// A business module's handler of one kind of held change. The kernel's approval checks the request (pending, not expired, not
/// decided by the person who asked: FR-XM-32) and that the approver holds <see cref="ApprovePermission"/>; the handler checks its own
/// rules and the approver's data scope, then applies the change in the approval's transaction. Rejecting needs the same permission.
/// </summary>
public interface IHeldChangeHandler
{
    string Kind { get; }

    /// <summary>The permission an approver (and a rejecter) of this kind must hold, instead of <c>iam.maker_checker.approve</c>.</summary>
    string ApprovePermission { get; }

    /// <summary>The refusal approving would give now, or null. Runs in a transaction that is rolled back.</summary>
    Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Checks again and applies the change in the approval's transaction; a refusal leaves everything as it was.</summary>
    Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken);

    /// <summary>The change in plain words for the approvals inbox.</summary>
    Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken);
}
