using System.ComponentModel.DataAnnotations.Schema;

namespace Erp.Hrm.Infrastructure.Attendance.Reports;

// The rows of the read-only reporting views of slice S1.30b (rules.yaml functions hrm.rpt_att_*). Each class mirrors the columns of one view, by name; none has a pay
// or salary member (FR-HRM-GHR-06; a unit test reads the classes and the catalogue). The members mapped from "on_date" are the report's date column for the date filter.

internal sealed class MusterRow
{
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("employment_id")] public Guid EmploymentId { get; set; }
    [Column("employee_no")] public string EmployeeNo { get; set; } = string.Empty;
    [Column("first_name")] public string FirstName { get; set; } = string.Empty;
    [Column("last_name")] public string? LastName { get; set; }
    [Column("org_unit_id")] public Guid? OrgUnitId { get; set; }
    [Column("branch_id")] public Guid? BranchId { get; set; }
    [Column("location_id")] public Guid? LocationId { get; set; }
    [Column("day_id")] public Guid DayId { get; set; }
    [Column("work_date")] public DateOnly WorkDate { get; set; }
    [Column("shift_id")] public Guid? ShiftId { get; set; }
    [Column("day_type")] public string DayType { get; set; } = string.Empty;
    [Column("result")] public string Result { get; set; } = string.Empty;
    [Column("first_in")] public DateTimeOffset? FirstIn { get; set; }
    [Column("last_out")] public DateTimeOffset? LastOut { get; set; }
    [Column("worked_minutes")] public int WorkedMinutes { get; set; }
    [Column("late_minutes")] public int LateMinutes { get; set; }
    [Column("early_minutes")] public int EarlyMinutes { get; set; }
    [Column("overtime_minutes")] public int OvertimeMinutes { get; set; }
}

internal sealed class MonthlyRow
{
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("employment_id")] public Guid EmploymentId { get; set; }
    [Column("employee_no")] public string EmployeeNo { get; set; } = string.Empty;
    [Column("first_name")] public string FirstName { get; set; } = string.Empty;
    [Column("last_name")] public string? LastName { get; set; }
    [Column("org_unit_id")] public Guid? OrgUnitId { get; set; }
    [Column("branch_id")] public Guid? BranchId { get; set; }
    [Column("location_id")] public Guid? LocationId { get; set; }
    [Column("month")] public DateOnly Month { get; set; }
    [Column("present_days")] public long PresentDays { get; set; }
    [Column("absent_days")] public long AbsentDays { get; set; }
    [Column("late_days")] public long LateDays { get; set; }
    [Column("half_days")] public long HalfDays { get; set; }
    [Column("leave_days")] public long LeaveDays { get; set; }
    [Column("holiday_days")] public long HolidayDays { get; set; }
    [Column("weekly_off_days")] public long WeeklyOffDays { get; set; }
    [Column("duty_days")] public long DutyDays { get; set; }
    [Column("incomplete_days")] public long IncompleteDays { get; set; }
    [Column("worked_minutes")] public long WorkedMinutes { get; set; }
    [Column("late_minutes")] public long LateMinutes { get; set; }
    [Column("early_minutes")] public long EarlyMinutes { get; set; }
    [Column("overtime_minutes")] public long OvertimeMinutes { get; set; }
}

internal sealed class AbsenteeismRow
{
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("employment_id")] public Guid EmploymentId { get; set; }
    [Column("employee_no")] public string EmployeeNo { get; set; } = string.Empty;
    [Column("first_name")] public string FirstName { get; set; } = string.Empty;
    [Column("last_name")] public string? LastName { get; set; }
    [Column("org_unit_id")] public Guid? OrgUnitId { get; set; }
    [Column("branch_id")] public Guid? BranchId { get; set; }
    [Column("location_id")] public Guid? LocationId { get; set; }
    [Column("month")] public DateOnly Month { get; set; }
    [Column("scheduled_days")] public long ScheduledDays { get; set; }
    [Column("absent_days")] public long AbsentDays { get; set; }
    [Column("half_days")] public long HalfDays { get; set; }
    [Column("late_days")] public long LateDays { get; set; }
    [Column("absence_percent")] public decimal? AbsencePercent { get; set; }
}

internal sealed class LateEarlyRow
{
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("employment_id")] public Guid EmploymentId { get; set; }
    [Column("employee_no")] public string EmployeeNo { get; set; } = string.Empty;
    [Column("first_name")] public string FirstName { get; set; } = string.Empty;
    [Column("last_name")] public string? LastName { get; set; }
    [Column("org_unit_id")] public Guid? OrgUnitId { get; set; }
    [Column("branch_id")] public Guid? BranchId { get; set; }
    [Column("location_id")] public Guid? LocationId { get; set; }
    [Column("day_id")] public Guid DayId { get; set; }
    [Column("work_date")] public DateOnly WorkDate { get; set; }
    [Column("shift_id")] public Guid? ShiftId { get; set; }
    [Column("result")] public string Result { get; set; } = string.Empty;
    [Column("first_in")] public DateTimeOffset? FirstIn { get; set; }
    [Column("last_out")] public DateTimeOffset? LastOut { get; set; }
    [Column("late_minutes")] public int LateMinutes { get; set; }
    [Column("early_minutes")] public int EarlyMinutes { get; set; }
}

internal sealed class PunchRow
{
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("employment_id")] public Guid EmploymentId { get; set; }
    [Column("employee_no")] public string EmployeeNo { get; set; } = string.Empty;
    [Column("first_name")] public string FirstName { get; set; } = string.Empty;
    [Column("last_name")] public string? LastName { get; set; }
    [Column("org_unit_id")] public Guid? OrgUnitId { get; set; }
    [Column("branch_id")] public Guid? BranchId { get; set; }
    [Column("location_id")] public Guid? LocationId { get; set; }
    [Column("event_id")] public Guid EventId { get; set; }
    [Column("event_date")] public DateOnly EventDate { get; set; }
    [Column("event_at")] public DateTimeOffset EventAt { get; set; }
    [Column("direction")] public string Direction { get; set; } = string.Empty;
    [Column("source")] public string Source { get; set; } = string.Empty;
    [Column("device_id")] public Guid? DeviceId { get; set; }
    [Column("device_serial_no")] public string? DeviceSerialNo { get; set; }
    [Column("received_at")] public DateTimeOffset ReceivedAt { get; set; }
    [Column("is_duplicate")] public bool IsDuplicate { get; set; }
    [Column("supersedes_event_id")] public Guid? SupersedesEventId { get; set; }
}

internal sealed class DeviceRow
{
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("device_id")] public Guid DeviceId { get; set; }
    [Column("location_id")] public Guid LocationId { get; set; }
    [Column("serial_no")] public string SerialNo { get; set; } = string.Empty;
    [Column("model")] public string? Model { get; set; }
    [Column("status")] public string Status { get; set; } = string.Empty;
    [Column("last_seen_at")] public DateTimeOffset? LastSeenAt { get; set; }
    [Column("last_sync_at")] public DateTimeOffset? LastSyncAt { get; set; }
    [Column("last_error")] public string? LastError { get; set; }
    [Column("sync_cursor_at")] public DateTimeOffset? SyncCursorAt { get; set; }
    [Column("open_unmapped_punches")] public long OpenUnmappedPunches { get; set; }
}

internal sealed class ExceptionRow
{
    [Column("company_id")] public Guid CompanyId { get; set; }
    [Column("employment_id")] public Guid EmploymentId { get; set; }
    [Column("employee_no")] public string EmployeeNo { get; set; } = string.Empty;
    [Column("first_name")] public string FirstName { get; set; } = string.Empty;
    [Column("last_name")] public string? LastName { get; set; }
    [Column("org_unit_id")] public Guid? OrgUnitId { get; set; }
    [Column("branch_id")] public Guid? BranchId { get; set; }
    [Column("location_id")] public Guid? LocationId { get; set; }
    [Column("exception_id")] public Guid ExceptionId { get; set; }
    [Column("work_date")] public DateOnly WorkDate { get; set; }
    [Column("kind")] public string Kind { get; set; } = string.Empty;
    [Column("status")] public string Status { get; set; } = string.Empty;
    [Column("attendance_day_id")] public Guid? AttendanceDayId { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whole days from the work date to the company's today (set after reading; not a view column).</summary>
    [NotMapped] public int AgeDays { get; set; }
}
