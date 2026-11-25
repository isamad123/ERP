#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Infrastructure.Policies;

namespace Erp.UnitTests.Leave;

/// <summary>
/// The pure parts of the leave engine of slice S1.32b: counting the days of a request with the sandwich modes, half days and hours (FR-HRM-LVE-03,
/// -04; Build items 7 and 8, rulings Q4 and Q5), the request checks (LVE-03), expiring leave used first and expiring once (LVE-03, LVE-08), the
/// year-end split (LVE-11) and the pack leave type's shape (LVE-12). No statutory value is anywhere (AS-4).
/// </summary>
public sealed class LeaveRequestUnitTests
{
    private static readonly DateOnly Mon = new(2027, 3, 1); // a Monday

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>Mon to the next Mon: Wed a holiday, Sat and Sun off, the other days planned by nothing (counted as working).</summary>
    private static List<PlanDay> Week(decimal? hours = null) =>
        [.. Enumerable.Range(0, 8).Select(i => Mon.AddDays(i)).Select(d => new PlanDay(d,
            d == Mon.AddDays(2) ? PlanDayTypes.Holiday : d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? PlanDayTypes.WeeklyOff : PlanDayTypes.None, hours))];

    private static LeaveTypeRules Rules(string json) => LeavePolicy.Parse(JsonDocument.Parse($$"""{"types":[{{json}}]}""").RootElement).Rules.Values.Single();

    // ------------------------------------------------------------------ day counting and sandwich (FR-HRM-LVE-04, ruling Q5)

    [Theory]
    [InlineData("both", 6.0, "0.5L,1S,1L,1L,1S,1S,0.5L")]
    [InlineData("holidays", 4.0, "0.5L,1S,1L,1L,0O,0O,0.5L")]
    [InlineData("weekly_off", 5.0, "0.5L,0O,1L,1L,1S,1S,0.5L")]
    [InlineData("off", 3.0, "0.5L,0O,1L,1L,0O,0O,0.5L")]
    public void FR_HRM_LVE_04_a_half_day_request_across_a_holiday_and_a_weekend_counts_by_the_sandwich_mode(string mode, double total, string days)
    {
        var count = LeaveDayRules.Count("working_days", "half_day", Mon.AddDays(1), Mon.AddDays(7), LeaveParts.SecondHalf, LeaveParts.FirstHalf, null, mode, Week());
        Assert.True(count.Ok, count.Error);
        Assert.Equal((decimal)total, count.Total);
        Assert.Equal(days, string.Join(',', count.Days.Select(d => d.Quantity.ToString("0.#", CultureInfo.InvariantCulture) + d.Kind.ToUpperInvariant()[0])));
        Assert.Equal("hrm.leave.day.leave_unplanned", count.Days[0].Reason); // nothing plans the day: it counts, and the list says so
    }

    [Fact]
    public void FR_HRM_LVE_04_leading_and_trailing_off_days_never_count_and_calendar_days_count_every_day()
    {
        var trailing = LeaveDayRules.Count("working_days", "day", Mon.AddDays(3), Mon.AddDays(6), LeaveParts.Full, LeaveParts.Full, null, "both", Week());
        Assert.Equal(2m, trailing.Total);
        var leading = LeaveDayRules.Count("working_days", "day", Mon.AddDays(5), Mon.AddDays(7), LeaveParts.Full, LeaveParts.Full, null, "both", Week());
        Assert.Equal(1m, leading.Total);
        var calendar = LeaveDayRules.Count("calendar_days", "day", Mon, Mon.AddDays(6), LeaveParts.Full, LeaveParts.Full, null, "off", Week());
        Assert.Equal(7m, calendar.Total);
        Assert.Contains(calendar.Days, d => d.Reason == "hrm.leave.day.calendar_holiday");
        var offOnly = LeaveDayRules.Count("working_days", "day", Mon.AddDays(5), Mon.AddDays(6), LeaveParts.Full, LeaveParts.Full, null, "both", Week());
        Assert.Equal(LeaveDayRules.NoDays, offOnly.Error);
    }

    [Theory]
    [InlineData("day", "first_half", "first_half", 0, LeaveDayRules.HalfNotAllowed)]
    [InlineData("half_day", "first_half", "second_half", 0, LeaveDayRules.PartInvalid)] // one day: one half
    [InlineData("half_day", "first_half", "full", 1, LeaveDayRules.PartInvalid)] // a longer leave cannot start with the morning
    [InlineData("half_day", "full", "second_half", 1, LeaveDayRules.PartInvalid)] // nor end with the afternoon
    [InlineData("half_day", "middle", "full", 0, LeaveDayRules.PartInvalid)]
    public void FR_HRM_LVE_04_parts_of_a_day_follow_the_type_and_the_shape_of_the_request(string unit, string startPart, string endPart, int extraDays, string error)
    {
        var count = LeaveDayRules.Count("working_days", unit, Mon, Mon.AddDays(extraDays), startPart, endPart, null, "off", Week());
        Assert.Equal(error, count.Error);
    }

    [Fact]
    public void FR_HRM_LVE_04_hourly_leave_is_one_working_day_and_at_most_its_hours()
    {
        Assert.Equal(3m, LeaveDayRules.Count("hours", "hour", Mon, Mon, "full", "full", 3m, "off", Week(8m)).Total);
        Assert.Equal(LeaveDayRules.HoursTooMany, LeaveDayRules.Count("hours", "hour", Mon, Mon, "full", "full", 8.5m, "off", Week(8m)).Error);
        Assert.Equal(LeaveDayRules.HoursUnknown, LeaveDayRules.Count("hours", "hour", Mon, Mon, "full", "full", 2m, "off", Week()).Error); // no number is filled in
        Assert.Equal(LeaveDayRules.NotWorkingDay, LeaveDayRules.Count("hours", "hour", Mon.AddDays(2), Mon.AddDays(2), "full", "full", 2m, "off", Week(8m)).Error);
        Assert.Equal(LeaveDayRules.HoursOneDay, LeaveDayRules.Count("hours", "hour", Mon, Mon.AddDays(1), "full", "full", 2m, "off", Week(8m)).Error);
        Assert.Equal(LeaveDayRules.HoursRequired, LeaveDayRules.Count("hours", "hour", Mon, Mon, "full", "full", 1.255m, "off", Week(8m)).Error);
        Assert.Equal(LeaveDayRules.HoursNotUsed, LeaveDayRules.Count("working_days", "day", Mon, Mon, "full", "full", 2m, "off", Week(8m)).Error);
        Assert.Equal(LeaveDayRules.TooLong, LeaveDayRules.Count("calendar_days", "day", Mon, Mon.AddDays(400), "full", "full", null, "off", []).Error);
    }

    // ------------------------------------------------------------------ request checks (FR-HRM-LVE-03)

    [Fact]
    public void FR_HRM_LVE_03_eligibility_notice_longest_and_combining_follow_the_policy_and_a_rule_not_set_allows()
    {
        var open = Rules("""{"leaveType":"ANNUAL","entitlement":{"model":"manual"}}""");
        Assert.Null(LeaveRequestChecks.Eligibility(open, null, 0, onProbation: true));
        Assert.Null(LeaveRequestChecks.NoticeCheck(open, Mon, Mon));
        Assert.Null(LeaveRequestChecks.LongestCheck(open, 365m));

        var strict = Rules("""{"leaveType":"MATERN","entitlement":{"model":"manual"},"minNoticeDays":7,"maxConsecutiveDays":3,"eligibility":{"genders":["female"],"minServiceMonths":6,"duringProbation":false},"notCombinedWith":["ANNUAL"]}""");
        Assert.Equal(LeaveRequestChecks.Gender, LeaveRequestChecks.Eligibility(strict, "male", 12, false));
        Assert.Equal(LeaveRequestChecks.Tenure, LeaveRequestChecks.Eligibility(strict, "female", 5, false));
        Assert.Equal(LeaveRequestChecks.Probation, LeaveRequestChecks.Eligibility(strict, "female", 12, true));
        Assert.Null(LeaveRequestChecks.Eligibility(strict, "female", 6, false));
        Assert.Equal(LeaveRequestChecks.Notice, LeaveRequestChecks.NoticeCheck(strict, Mon, Mon.AddDays(6)));
        Assert.Null(LeaveRequestChecks.NoticeCheck(strict, Mon, Mon.AddDays(7)));
        Assert.Equal(LeaveRequestChecks.Longest, LeaveRequestChecks.LongestCheck(strict, 3.5m));
        Assert.Equal(LeaveRequestChecks.Combined, LeaveRequestChecks.CombinedCheck(strict, [("annual", [])]));
        Assert.Equal(LeaveRequestChecks.Combined, LeaveRequestChecks.CombinedCheck(open, [("MATERN", ["ANNUAL"])])); // either side's rule
        Assert.Null(LeaveRequestChecks.CombinedCheck(open, [("CASUAL", [])]));
    }

    [Fact]
    public void FR_HRM_LVE_05_the_policy_names_hr_approval_cancellation_approval_and_comp_off_expiry_and_whether_carry_forward_and_encashment_are_set()
    {
        var rules = Rules("""{"leaveType":"COMP","entitlement":{"model":"manual"},"hrApproval":true,"cancellationApproval":true,"compOffExpiryDays":30,"carryForward":{},"encashment":{"rateBasis":"basic"}}""");
        Assert.True(rules.HrApproval);
        Assert.True(rules.CancellationApproval);
        Assert.Equal(30, rules.CompOffExpiryDays);
        Assert.True(rules.HasCarryForward);
        Assert.Null(rules.CarryForwardCap);
        Assert.True(rules.HasEncashment);
        var bare = Rules("""{"leaveType":"X","entitlement":{"model":"manual"}}""");
        Assert.False(bare.HrApproval || bare.CancellationApproval || bare.HasCarryForward || bare.HasEncashment);
        Assert.Null(bare.CompOffExpiryDays);
    }

    // ------------------------------------------------------------------ expiring leave (FR-HRM-LVE-03, -08)

    [Fact]
    public void FR_HRM_LVE_08_leave_taken_uses_the_lot_expiring_first_and_what_is_left_expires_the_day_after_its_last_day()
    {
        var comp = Guid.CreateVersion7();
        var carried = Guid.CreateVersion7();
        var lines = new List<LotLine>
        {
            new(Guid.CreateVersion7(), D("2027-01-01"), 10m, null, null, null, "grant"),
            new(carried, D("2027-01-01"), 5m, D("2027-03-31"), null, null, "carry_forward"),
            new(comp, D("2027-02-01"), 2m, D("2027-03-02"), null, null, "comp_off_earned"),
            new(Guid.CreateVersion7(), D("2027-02-10"), -3m, null, null, null, "usage"), // takes the comp-off (expires first) then 1 carried
        };
        var p = LeaveLots.Project(lines, D("2027-12-31"));
        Assert.DoesNotContain(p.Expiries, x => x.LotId == comp); // used up before it expired
        var lapse = Assert.Single(p.Expiries, x => x.LotId == carried);
        Assert.Equal((D("2027-04-01"), 4m, "lapse"), (lapse.On, lapse.Quantity, lapse.Kind));
        Assert.Equal(10m, p.Closing);
        Assert.Contains((D("2027-02-10"), 14m), p.Running);
        Assert.Equal(10m, p.LowestFrom(D("2027-02-10"))); // the lowest from that day on counts the expiry still to come
        Assert.Equal(17m, p.LowestFrom(D("2027-02-01")) + 7m);

        // An expiry already posted closes its own lot: nothing more is projected, the balance is the same.
        var posted = lines.Append(new LotLine(Guid.CreateVersion7(), D("2027-04-01"), -4m, null, null, carried, "lapse")).ToList();
        var again = LeaveLots.Project(posted, D("2027-12-31"));
        Assert.DoesNotContain(again.Expiries, x => x.LotId == carried);
        Assert.Equal(10m, again.Closing);
    }

    [Fact]
    public void FR_HRM_LVE_08_comp_off_expires_as_comp_off_expired_a_lot_past_the_year_end_is_left_to_the_year_end_and_a_reversed_lot_does_not_expire()
    {
        var comp = Guid.CreateVersion7();
        var late = Guid.CreateVersion7();
        var grant = Guid.CreateVersion7();
        var p = LeaveLots.Project(
        [
            new(comp, D("2027-03-01"), 1m, D("2027-03-31"), null, null, "comp_off_earned"),
            new(late, D("2027-12-01"), 1m, D("2028-01-30"), null, null, "comp_off_earned"),
            new(grant, D("2027-05-01"), 2m, D("2027-06-30"), null, null, "grant"),
            new(Guid.CreateVersion7(), D("2027-05-02"), -2m, null, grant, null, "grant"),
        ], D("2027-12-31"));
        var expiry = Assert.Single(p.Expiries);
        Assert.Equal((comp, D("2027-04-01"), 1m, "comp_off_expired"), (expiry.LotId, expiry.On, expiry.Quantity, expiry.Kind));
        Assert.Equal(1m, p.Closing); // the December comp-off is still there at the year-end
    }

    // ------------------------------------------------------------------ year-end (FR-HRM-LVE-11)

    [Theory]
    [InlineData("""{"carryForward":{"cap":5,"expiryMonths":3},"encashment":{"cap":2}}""", 10, 5, 2, 3, "2028-03-31")]
    [InlineData("""{"carryForward":{},"encashment":{"cap":2}}""", 10, 10, 0, 0, null)]
    [InlineData("""{"encashment":{}}""", 10, 0, 10, 0, null)]
    [InlineData("""{}""", 10, 0, 0, 10, null)]
    [InlineData("""{"carryForward":{"cap":5}}""", 3, 3, 0, 0, null)]
    [InlineData("""{"carryForward":{"cap":5}}""", -2, -2, 0, 0, null)] // leave taken in advance stays owed
    public void FR_HRM_LVE_11_the_year_end_carries_forward_up_to_the_cap_then_pays_out_up_to_its_cap_and_lapses_the_rest(string extra, int closing, int carry, int encash, int lapse, string? expires)
    {
        var json = JsonNode.Parse(extra)!.AsObject();
        json["leaveType"] = "ANNUAL";
        json["entitlement"] = new JsonObject { ["model"] = "manual" };
        var split = YearEndRules.Split(closing, Rules(json.ToJsonString()), D("2028-01-01"));
        Assert.Equal((closing, carry, encash, lapse), ((int)split.Closing, (int)split.CarryForward, (int)split.Encash, (int)split.Lapse));
        Assert.Equal(expires is null ? null : D(expires), split.CarryExpiresOn);
        Assert.Equal(split.Closing, split.CarryForward + split.Encash + split.Lapse);
    }

    [Fact]
    public void FR_HRM_LVE_11_year_end_entries_have_one_source_per_run_employment_type_and_side()
    {
        var (run, e, t) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.Equal(YearEndRules.SourceId(run, e, t, "out"), YearEndRules.SourceId(run, e, t, "out"));
        Assert.NotEqual(YearEndRules.SourceId(run, e, t, "out"), YearEndRules.SourceId(run, e, t, "in"));
        Assert.NotEqual(YearEndRules.SourceId(run, e, t, "out"), YearEndRules.SourceId(Guid.CreateVersion7(), e, t, "out"));
    }

    // ------------------------------------------------------------------ pack leave types (FR-HRM-LVE-12, ruling Q2)

    [Fact]
    public void FR_HRM_LVE_12_a_pack_leave_type_is_checked_before_install_and_runs_before_the_policies_that_name_it()
    {
        var handlers = HrmPackHandlers.Default;
        var leave = Assert.Single(handlers, h => h.ObjectType == "leave_type");
        Assert.True(leave.Order < Assert.Single(handlers, h => h.ObjectType == "policy").Order);
        var ok = JsonNode.Parse("""{"name":{"en":"Test leave","ur":"آزمائشی رخصت"},"counting":"working_days","unit":"day","isPaid":true,"requiresDocument":false,"isCompOff":false,"isUnpaidDeduction":false}""")!.AsObject();
        Assert.Null(leave.Validate("TEST_LEAVE", ok));
        Assert.NotNull(leave.Validate("bad code!", ok));
        var hours = (JsonObject)ok.DeepClone();
        hours["counting"] = "hours";
        Assert.NotNull(leave.Validate("T", hours)); // counted in hours goes with the unit hour
        var paidDeduction = (JsonObject)ok.DeepClone();
        paidDeduction["isUnpaidDeduction"] = true;
        Assert.NotNull(leave.Validate("T", paidDeduction));
        var days = (JsonObject)ok.DeepClone();
        days["days"] = 14;
        Assert.NotNull(leave.Validate("T", days)); // no entitlement in a type: that is a policy (and no statutory value is shipped)
        Assert.Equal(ok.ToJsonString(), leave.Normalize(ok).ToJsonString());
    }
}
