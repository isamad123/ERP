namespace Erp.Hrm.Contracts.Attendance;

/// <summary>
/// Permissions of the attendance requests (slice S1.30a, CR-090; FR-HRM-ATT-11, ATT-14, GHR-09). The generated writes of
/// <c>hrm.regularization_requests</c> and <c>hrm.duty_requests</c> are closed: the HR request service makes them and the workflow decides them.
/// Who a request may be made for is the role's data scope: an employee for themselves, a manager for their team, HR for the company; a request
/// made for another person records the acting user (<c>created_by</c>). Reads stay the generated <c>&lt;table&gt;.read</c>.
/// </summary>
public static class AttendanceRequestPermissions
{
    public const string RegularizationCreate = "hrm.regularization_requests.create";

    public const string DutyCreate = "hrm.duty_requests.create";

    public const string RegularizationRead = "hrm.regularization_requests.read";

    public const string DutyRead = "hrm.duty_requests.read";

    /// <summary>Every code declared in permissions.json for the requests (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [RegularizationCreate, DutyCreate];
}

/// <summary>Events of the attendance requests in <c>hrm.outbox_messages</c> (slice S1.30a).</summary>
public static class AttendanceRequestEvents
{
    public const string RegularizationApproved = "hrm.regularization.approved";

    public const string DutyApproved = "hrm.duty_request.approved";

    /// <summary>Owner type of the proof files of a regularization request (slice S1.30b, R-161; <c>IAttachmentOwner</c>).</summary>
    public const string RegularizationOwnerType = "hrm.regularization_requests";

    /// <summary>Owner type of the proof files of a duty request (slice S1.30b, R-161; <c>IAttachmentOwner</c>).</summary>
    public const string DutyOwnerType = "hrm.duty_requests";

    public const string RegularizationDocumentType = "hrm.regularization";

    public const string DutyDocumentType = "hrm.duty_request";
}
