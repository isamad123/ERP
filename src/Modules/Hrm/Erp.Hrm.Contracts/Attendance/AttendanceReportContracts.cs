namespace Erp.Hrm.Contracts.Attendance;

/// <summary>
/// Permissions of the live attendance dashboard and the standard attendance reports (slice S1.30b, CR-090 S1.30b detail; FR-HRM-ATT-18, ATT-19, RPT-05,
/// SSP-02). The permission opens the screen or report; which rows appear is the data scope of the caller's <c>hrm.attendance_days.read</c> grant (and
/// <c>hrm.attendance_events.read</c>, <c>hrm.attendance_exceptions.read</c>, <c>hrm.attendance_devices.read</c> for the reports over those tables), so a
/// manager sees the team and HR the company. No report has a pay or salary column (FR-HRM-GHR-06).
/// </summary>
public static class AttendanceReportPermissions
{
    public const string Dashboard = "hrm.attendance_dashboard.read";

    public const string Reports = "hrm.attendance_reports.read";

    /// <summary>Every code declared in permissions.json for the dashboard and reports (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [Dashboard, Reports];
}

/// <summary>The standard attendance reports (FR-HRM-ATT-19): the code in the URL, and the code an export of the report carries.</summary>
public static class AttendanceReportCodes
{
    public const string MusterDaily = "muster_daily";
    public const string MusterMonthly = "muster_monthly";
    public const string Absenteeism = "absenteeism";
    public const string LateEarly = "late_early";
    public const string PunchLog = "punch_log";
    public const string DeviceSync = "device_sync";
    public const string ExceptionAgeing = "exception_ageing";

    /// <summary>Every report built in 1.0. Overtime follows S1.29b's overtime entries (open item).</summary>
    public static IReadOnlyList<string> All { get; } = [MusterDaily, MusterMonthly, Absenteeism, LateEarly, PunchLog, DeviceSync, ExceptionAgeing];

    /// <summary>The export type of a report (the key the export centre and the export button use).</summary>
    public static string ExportType(string code) => "hrm.rpt_att_" + code;
}

/// <summary>What a person on the live dashboard is doing now (FR-HRM-ATT-18, ruling Q2 of the slice).</summary>
public static class DashboardStatuses
{
    /// <summary>Punched in today (late people are present too).</summary>
    public const string Present = "present";

    /// <summary>Punched in after the shift start plus the grace of the attendance policy.</summary>
    public const string Late = "late";

    /// <summary>The shift has ended and there is no punch, no approved leave and no approved duty.</summary>
    public const string Absent = "absent";

    public const string OnLeave = "on_leave";

    public const string OnDuty = "on_duty";

    /// <summary>A shift today whose start plus the grace has passed and no punch yet.</summary>
    public const string NotYetPunched = "not_yet_punched";

    /// <summary>A shift today that has not reached its start plus grace.</summary>
    public const string NotDue = "not_due";

    /// <summary>No working shift today (weekly off, holiday or no published roster).</summary>
    public const string NotScheduled = "not_scheduled";

    /// <summary>A shift today but no attendance policy: lateness cannot be judged (no number has a default).</summary>
    public const string NoPolicy = "no_policy";

    public static IReadOnlyList<string> All { get; } = [Present, Late, Absent, OnLeave, OnDuty, NotYetPunched, NotDue, NotScheduled, NoPolicy];
}
