using System.ComponentModel.DataAnnotations.Schema;

namespace Erp.Hrm.Infrastructure.Leave.Reports;

// The rows of the read-only leave reporting views of slice S1.33b (rules.yaml functions hrm.rpt_leave_*). Each class mirrors the columns of one view, by name; none has a
// pay or salary member (FR-HRM-GHR-06; a unit test reads the classes and the catalogue). Quantities are exact decimals in the leave type's unit.

internal sealed class LeaveBalanceRow
{
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("employment_id")] public Guid EmploymentId { get; set; }
    [Column("employee_no")] public string EmployeeNo { get; set; } = string.Empty;
    [Column("first_name")] public string FirstName { get; set; } = string.Empty;
    [Column("last_name")] public string? LastName { get; set; }
    [Column("org_unit_id")] public Guid? OrgUnitId { get; set; }
    [Column("branch_id")] public Guid? BranchId { get; set; }
    [Column("location_id")] public Guid? LocationId { get; set; }
    [Column("leave_type_id")] public Guid LeaveTypeId { get; set; }
    [Column("leave_type_code")] public string LeaveTypeCode { get; set; } = string.Empty;
    [Column("unit")] public string Unit { get; set; } = string.Empty;
    [Column("leave_year")] public int LeaveYear { get; set; }
    [Column("added")] public decimal Added { get; set; }
    [Column("used")] public decimal Used { get; set; }
    [Column("adjusted")] public decimal Adjusted { get; set; }
    [Column("removed")] public decimal Removed { get; set; }
    [Column("balance")] public decimal Balance { get; set; }
}

internal sealed class LeaveTakenRow
{
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("employment_id")] public Guid EmploymentId { get; set; }
    [Column("employee_no")] public string EmployeeNo { get; set; } = string.Empty;
    [Column("first_name")] public string FirstName { get; set; } = string.Empty;
    [Column("last_name")] public string? LastName { get; set; }
    [Column("org_unit_id")] public Guid? OrgUnitId { get; set; }
    [Column("branch_id")] public Guid? BranchId { get; set; }
    [Column("location_id")] public Guid? LocationId { get; set; }
    [Column("leave_type_id")] public Guid LeaveTypeId { get; set; }
    [Column("leave_type_code")] public string LeaveTypeCode { get; set; } = string.Empty;
    [Column("unit")] public string Unit { get; set; } = string.Empty;
    [Column("month")] public DateOnly Month { get; set; }
    [Column("requests")] public long Requests { get; set; }
    [Column("taken")] public decimal Taken { get; set; }
    [Column("unpaid")] public decimal Unpaid { get; set; }
}

internal sealed class LeavePendingRow
{
    [Column("request_id")] public Guid RequestId { get; set; }
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("request_no")] public string RequestNo { get; set; } = string.Empty;
    [Column("employment_id")] public Guid EmploymentId { get; set; }
    [Column("employee_no")] public string EmployeeNo { get; set; } = string.Empty;
    [Column("first_name")] public string FirstName { get; set; } = string.Empty;
    [Column("last_name")] public string? LastName { get; set; }
    [Column("org_unit_id")] public Guid? OrgUnitId { get; set; }
    [Column("branch_id")] public Guid? BranchId { get; set; }
    [Column("location_id")] public Guid? LocationId { get; set; }
    [Column("leave_type_id")] public Guid LeaveTypeId { get; set; }
    [Column("leave_type_code")] public string LeaveTypeCode { get; set; } = string.Empty;
    [Column("unit")] public string Unit { get; set; } = string.Empty;
    [Column("start_date")] public DateOnly StartDate { get; set; }
    [Column("end_date")] public DateOnly EndDate { get; set; }
    [Column("days")] public decimal Days { get; set; }
    [Column("status")] public string Status { get; set; } = string.Empty;
    [Column("requested_at")] public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Whole days since the request was made (company date); filled in after the rows are read, not a column of the view.</summary>
    [NotMapped] public int AgeDays { get; set; }
}
