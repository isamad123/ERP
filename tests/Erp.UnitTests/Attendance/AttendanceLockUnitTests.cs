using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.UnitTests.Attendance;

/// <summary>
/// Pure rules of slice S1.31: the checks of a retroactive adjustment (FR-XM-26), the corrected day of each kind (FR-HRM-LVE-07: only the cancelled request's
/// share of a day's leave goes), the summaries kept as JSON and read back, the status history that only grows (FR-XM-06), the three workflow document
/// types (FR-XM-32: the employee is a subject of an adjustment), and the texts in English and Urdu.
/// </summary>
public sealed class AttendanceLockUnitTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static AttendanceAdjustmentRequestInput Input(string? kind, Guid? leave = null, params AdjustedDayInput[] days) =>
        new() { EmploymentId = Guid.CreateVersion7(), Kind = kind, LeaveRequestId = leave, Days = days, Reason = "Device lost the punches" };

    [Fact]
    public void FR_XM_26_an_adjustment_request_is_checked_for_kind_days_minutes_results_and_the_leave_request()
    {
        Assert.Empty(AttendanceLockRules.CheckAdjustment(Input("attendance", null, new AdjustedDayInput { Date = Monday, Result = "present", WorkedMinutes = 480 })));
        Assert.Empty(AttendanceLockRules.CheckAdjustment(Input("leave_cancellation", Guid.CreateVersion7())));
        Assert.Empty(AttendanceLockRules.CheckAdjustment(Input("overtime", null, new AdjustedDayInput { Date = Monday, OvertimeMinutes = 0 })));

        Assert.Equal([AttendanceLockRules.Required], AttendanceLockRules.CheckAdjustment(Input(null))["kind"]);
        Assert.Equal([AttendanceLockRules.KindUnknown], AttendanceLockRules.CheckAdjustment(Input("salary"))["kind"]);
        Assert.Equal([AttendanceLockRules.DaysRequired], AttendanceLockRules.CheckAdjustment(Input("attendance"))["days"]);
        Assert.Equal([AttendanceLockRules.LeaveRequestRequired], AttendanceLockRules.CheckAdjustment(Input("leave_cancellation"))["leaveRequestId"]);
        Assert.Equal([AttendanceLockRules.LeaveRequestNotAllowed], AttendanceLockRules.CheckAdjustment(Input("attendance", Guid.CreateVersion7(), new AdjustedDayInput { Date = Monday, Result = "absent" }))["leaveRequestId"]);

        var errors = AttendanceLockRules.CheckAdjustment(Input("attendance", null,
            new AdjustedDayInput { Date = Monday, Result = "asleep", WorkedMinutes = 1441, LateMinutes = -1 },
            new AdjustedDayInput { Date = Monday, Result = "present" },
            new AdjustedDayInput { Result = "present" }));
        Assert.Equal([AttendanceLockRules.ResultUnknown], errors["days[0].result"]);
        Assert.Equal([AttendanceLockRules.MinutesOutOfRange], errors["days[0].workedMinutes"]);
        Assert.Equal([AttendanceLockRules.MinutesOutOfRange], errors["days[0].lateMinutes"]);
        Assert.Equal([AttendanceLockRules.DayTwice], errors["days[1].date"]);
        Assert.Equal([AttendanceLockRules.Required], errors["days[2].date"]);
        Assert.Equal([AttendanceLockRules.Required], AttendanceLockRules.CheckAdjustment(Input("attendance", null, new AdjustedDayInput { Date = Monday }))["days[0].result"]);
        Assert.Equal([AttendanceLockRules.Required], AttendanceLockRules.CheckAdjustment(Input("overtime", null, new AdjustedDayInput { Date = Monday }))["days[0].overtimeMinutes"]);
        var many = Enumerable.Range(0, AttendanceLockRules.MostDays + 1).Select(i => new AdjustedDayInput { Date = Monday.AddDays(i), Result = "present" }).ToArray();
        Assert.Equal([AttendanceLockRules.TooManyDays], AttendanceLockRules.CheckAdjustment(Input("attendance", null, many))["days"]);

        Assert.Equal([AttendanceLockRules.Required], AttendanceLockRules.CheckReason(" ")["reason"]);
        Assert.Equal([AttendanceLockRules.ReasonTooLong], AttendanceLockRules.CheckReason(new string('x', AttendanceLockRules.ReasonLength + 1))["reason"]);
    }

    [Fact]
    public void FR_XM_26_FR_HRM_LVE_07_the_corrected_day_changes_only_what_the_kind_states()
    {
        var old = new AdjustedDaySummary("late", 450, 25, 0, 60, 0.5m, 0.5m);

        // An attendance correction states the result and the minutes it gives; the leave and what it does not state stay.
        Assert.Equal(old with { Result = "present", LateMinutes = 0 }, AttendanceLockRules.Corrected("attendance", old, new AdjustedDayInput { Date = Monday, Result = "present", LateMinutes = 0 }));
        // An overtime correction states the payable overtime only.
        Assert.Equal(old with { OvertimeMinutes = 90 }, AttendanceLockRules.Corrected("overtime", old, new AdjustedDayInput { Date = Monday, OvertimeMinutes = 90 }));
        // A leave cancellation removes the cancelled request's share of the day's leave (another half day stays), never below nothing.
        Assert.Equal(old with { UnpaidLeave = 0m }, AttendanceLockRules.Corrected("leave_cancellation", old, null, unpaidShare: 0.5m));
        Assert.Equal(old with { Result = "present", PaidLeave = 0m }, AttendanceLockRules.Corrected("leave_cancellation", old, new AdjustedDayInput { Date = Monday, Result = "present" }, paidShare: 1m));
    }

    [Fact]
    public void FR_XM_06_summaries_read_back_as_written_and_the_status_history_only_grows()
    {
        var days = new[] { (Monday.AddDays(1), new AdjustedDaySummary(null, 0, 0, 0, 0, 1m, 0m)), (Monday, new AdjustedDaySummary("absent", 0, 0, 0, 0, 0m, 0m)) };
        using var json = AttendanceLockRules.ToJson(days);
        var read = AttendanceLockRules.FromJson(json);
        Assert.Equal([Monday, Monday.AddDays(1)], read.Select(d => d.Date)); // in date order
        Assert.Equal(days.OrderBy(d => d.Item1).Select(d => d.Item2), read.Select(d => d.Day));
        Assert.Empty(AttendanceLockRules.FromJson(JsonDocument.Parse("{}")));

        var by = Guid.CreateVersion7();
        using var first = AttendanceLockRules.Append(null, AttendanceLockRules.HistoryEntry("submitted", new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), by));
        using var second = AttendanceLockRules.Append(first, AttendanceLockRules.HistoryEntry("approved", new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), by, "agreed"));
        Assert.Equal(1, first.RootElement.GetArrayLength());
        Assert.Equal(["submitted", "approved"], second.RootElement.EnumerateArray().Select(e => e.GetProperty("status").GetString()));
        Assert.Equal(first.RootElement[0].GetRawText(), second.RootElement[0].GetRawText()); // the earlier entry is kept as it was
        Assert.Equal("2026-11-02T05:00:00.000000Z", second.RootElement[1].GetProperty("at").GetString());
    }

    [Fact]
    public void FR_XM_32_FR_HRM_ATT_21_the_lock_reopen_and_adjustment_document_types_route_to_someone_else()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/workflow.json")));
        var types = manifest.RootElement.GetProperty("document_types").EnumerateArray().ToDictionary(t => t.GetProperty("code").GetString()!, t => t);
        foreach (var code in new[] { AttendanceLockEvents.LockDocumentType, AttendanceLockEvents.ReopenDocumentType, AttendanceLockEvents.AdjustmentDocumentType })
        {
            var type = types[code];
            Assert.True(type.GetProperty("requires_approval").GetBoolean(), code);
            Assert.False(type.GetProperty("bulk_approval").GetBoolean(), code);
            Assert.NotEmpty(type.GetProperty("default_route").GetProperty("steps").EnumerateArray());
        }

        Assert.Equal("payroll_manager", types[AttendanceLockEvents.ReopenDocumentType].GetProperty("default_route").GetProperty("steps")[0].GetProperty("approver").GetProperty("role").GetString());
        Assert.Equal(["employee"], types[AttendanceLockEvents.AdjustmentDocumentType].GetProperty("subject_fields").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(AttendanceLockRules.Kinds, types[AttendanceLockEvents.AdjustmentDocumentType].GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "kind").GetProperty("choices").EnumerateArray().Select(c => c.GetString()));

        using var readiness = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/pay-readiness.json")));
        Assert.Contains(readiness.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("code").GetString() == "attendance_locked" && i.GetProperty("slice").GetString() == "S1.31");
    }

    [Fact]
    public void Q6_every_lock_and_adjustment_text_exists_in_English_and_Urdu()
    {
        var en = Texts("en");
        var ur = Texts("ur");
        Assert.All(AttendanceLockPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c) && ur.ContainsKey("permissions." + c), c));
        var used = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("Attendance", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(errors\\.hrm\\.attendance\\.(?:lock|adjustment)\\.[a-z_]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(used.Count >= 30, string.Join(", ", used));
        Assert.All(used, k => Assert.True(en.ContainsKey(k) && ur.ContainsKey(k), k));
        Assert.All(used, k => Assert.Matches(@"\p{IsArabic}", ur[k]));
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
    }

    private static Dictionary<string, string> Texts(string lang) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, $"src/Modules/Hrm/Erp.Hrm.Contracts/Attendance/Localization/attendance.{lang}.json")))!;

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}
