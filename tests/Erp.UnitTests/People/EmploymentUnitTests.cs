using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.CodeGen;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Infrastructure.Employments;

namespace Erp.UnitTests.People;

/// <summary>
/// Slice S1.20b: the assignment timeline the events call for (FR-HRM-EMP-03, FR-XM-05, GHR-03), the backdated change check
/// (GHR-04, ruling Q6 as corrected by CR-096, BR-02), the provisional catalogue and the HR texts and permissions.
/// </summary>
public sealed partial class EmploymentUnitTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);

    private static readonly DateOnly Jan1 = new(2026, 1, 1);

    private static JsonObject Full(string title) => new()
    {
        [AssignmentKeys.OrgUnit] = Guid.Empty.ToString(), [AssignmentKeys.Branch] = Guid.Empty.ToString(), [AssignmentKeys.Title] = title,
        [AssignmentKeys.CostAllocation] = new JsonArray(new JsonObject { ["cost_center_ref"] = "CC", ["pct"] = 1m }),
        [AssignmentKeys.Fte] = 1m, [AssignmentKeys.IsActing] = false,
    };

    private static TimelineEvent Ev(string kind, DateOnly date, JsonObject? assignment, string status = "effective", Guid? corrects = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), kind, date, status, corrects, assignment);

    [Fact]
    public void FR_HRM_EMP_03_GHR_03_a_change_between_two_rows_splits_them_and_carries_forward_only_the_fields_it_sets()
    {
        var hire = Ev("hire", Jan1, Full("Officer"));
        var promotion = Ev("promotion", new DateOnly(2026, 6, 1), new JsonObject { [AssignmentKeys.Title] = "Senior officer" });
        var grade = Ev("grade_change", new DateOnly(2026, 3, 1), new JsonObject { [AssignmentKeys.Grade] = Guid.NewGuid().ToString() }); // recorded last, dated between

        var rows = EmploymentTimeline.Plan([hire, promotion, grade], out var problem);

        Assert.Null(problem);
        Assert.Equal([Jan1, new DateOnly(2026, 3, 1), new DateOnly(2026, 6, 1)], rows.Select(r => r.ValidFrom));
        Assert.Equal([new DateOnly(2026, 3, 1), new DateOnly(2026, 6, 1), (DateOnly?)null], rows.Select(r => r.ValidTo));
        Assert.Equal("Officer", rows[1].Values[AssignmentKeys.Title]!.GetValue<string>());
        Assert.Equal(grade.Assignment![AssignmentKeys.Grade]!.GetValue<string>(), rows[2].Values[AssignmentKeys.Grade]!.GetValue<string>()); // carried forward
        Assert.Equal("Senior officer", rows[2].Values[AssignmentKeys.Title]!.GetValue<string>());
        Assert.Equal(promotion.Id, rows[2].EventId);
    }

    [Fact]
    public void FR_XM_05_a_correction_replaces_the_event_it_names_without_editing_it_and_a_pending_or_cancelled_event_counts_for_nothing()
    {
        var hire = Ev("hire", Jan1, Full("Officer"));
        var promotion = Ev("promotion", new DateOnly(2026, 6, 1), new JsonObject { [AssignmentKeys.Title] = "Manager" });
        var correction = Ev("data_correction", new DateOnly(2026, 4, 1), new JsonObject { [AssignmentKeys.Title] = "Manager" }, corrects: promotion.Id);
        var pending = Ev("transfer", new DateOnly(2026, 5, 1), new JsonObject { [AssignmentKeys.Title] = "Elsewhere" }, status: "pending_approval");
        var cancelled = Ev("demotion", new DateOnly(2026, 2, 1), new JsonObject { [AssignmentKeys.Title] = "Clerk" }, status: "cancelled");

        var rows = EmploymentTimeline.Plan([hire, promotion, correction, pending, cancelled], out _);

        // The as-of read: the old title the day before the corrected date, the new one from it; the promotion's own date is gone.
        Assert.Equal("Officer", EmploymentTimeline.On(rows, new DateOnly(2026, 3, 31))!.Values[AssignmentKeys.Title]!.GetValue<string>());
        Assert.Equal("Manager", EmploymentTimeline.On(rows, new DateOnly(2026, 4, 1))!.Values[AssignmentKeys.Title]!.GetValue<string>());
        Assert.Equal(2, rows.Count);
        Assert.Equal(correction.Id, rows[1].EventId);
        Assert.Contains(promotion.Id, EmploymentTimeline.Replaced([hire, promotion, correction]));

        // A correction still waiting for approval replaces nothing yet.
        Assert.Empty(EmploymentTimeline.Replaced([hire, promotion, correction with { Status = "pending_approval" }]));
    }

    [Fact]
    public void FR_XM_05_review_2_a_correction_of_a_correction_still_in_the_future_never_brings_back_the_event_the_first_one_replaced()
    {
        var hire = Ev("hire", Jan1, Full("Officer"));
        var promotion = Ev("promotion", new DateOnly(2026, 11, 1), new JsonObject { [AssignmentKeys.Title] = "Manager" });
        // C1 moved the promotion to 1 January 2027 (still in the future, so approved); C2 then moved it to 1 December and C1 was
        // cancelled on the spot (it never takes effect).
        var c1 = Ev("data_correction", new DateOnly(2027, 1, 1), new JsonObject { [AssignmentKeys.Title] = "Manager" }, status: "cancelled", corrects: promotion.Id);
        var c2 = Ev("data_correction", new DateOnly(2026, 12, 1), new JsonObject { [AssignmentKeys.Title] = "Manager" }, corrects: c1.Id);

        var events = new[] { hire, promotion, c1, c2 };
        var rows = EmploymentTimeline.Plan(events, out _);
        Assert.Equal("Officer", EmploymentTimeline.On(rows, new DateOnly(2026, 11, 20))!.Values[AssignmentKeys.Title]!.GetValue<string>());
        Assert.Equal("Manager", EmploymentTimeline.On(rows, new DateOnly(2026, 12, 1))!.Values[AssignmentKeys.Title]!.GetValue<string>());
        var by = EmploymentTimeline.ReplacedBy(events);
        Assert.Equal(c1.Id, by[promotion.Id]);
        Assert.Equal(c2.Id, by[c1.Id]);

        // A correction that never counted (rejected: cancelled and named by nobody) replaces nothing.
        Assert.Empty(EmploymentTimeline.Replaced([hire, promotion, c1]));
    }

    [Fact]
    public void FR_HRM_EMP_03_two_events_on_one_date_share_one_row_owned_by_the_last_and_nothing_starts_without_a_full_assignment()
    {
        var hire = Ev("hire", Jan1, Full("Officer"));
        var first = Ev("transfer", new DateOnly(2026, 2, 1), new JsonObject { [AssignmentKeys.Title] = "A" });
        var second = Ev("promotion", new DateOnly(2026, 2, 1), new JsonObject { [AssignmentKeys.Fte] = 0.5m });
        var rows = EmploymentTimeline.Plan([hire, first, second], out _);
        Assert.Equal(2, rows.Count);
        Assert.Equal(second.Id, rows[1].EventId);
        Assert.Equal("A", rows[1].Values[AssignmentKeys.Title]!.GetValue<string>());
        Assert.Equal(0.5m, rows[1].Values[AssignmentKeys.Fte]!.GetValue<decimal>());

        // A change dated before the (corrected) hire leaves the first day without job details.
        var moved = Ev("data_correction", new DateOnly(2026, 3, 1), Full("Officer"), corrects: hire.Id);
        Assert.Empty(EmploymentTimeline.Plan([hire, first, moved], out var problem));
        Assert.Equal(EmploymentTimeline.NoStartingAssignment, problem);
    }

    [Fact]
    public async Task GHR_04_Q6_with_no_module_registered_the_check_says_nothing_is_affected_and_a_current_change_asks_nobody()
    {
        var change = new EmploymentChange(Guid.NewGuid(), Guid.NewGuid(), "promotion", Jan1, Jan1, IsTermination: false);
        var report = await new EmploymentChangeCheck([]).RunAsync(change, new DateOnly(2026, 2, 1), CancellationToken.None);
        Assert.True(report.Backdated);
        Assert.Equal(ImpactReport.NothingAffected, report.Summary);
        Assert.Empty(report.Flagged);

        var module = new FakeModule(new AffectedPeriod("payroll", Jan1, new DateOnly(2026, 1, 31), Locked: true));
        var current = await new EmploymentChangeCheck([module]).RunAsync(change with { EffectiveDate = new DateOnly(2026, 2, 1), EarliestDate = new DateOnly(2026, 2, 1) }, new DateOnly(2026, 2, 1), CancellationToken.None);
        Assert.False(current.Backdated);
        Assert.Equal(ImpactReport.NotBackdated, current.Summary);
        Assert.Equal(0, module.Calls);
    }

    [Fact]
    public async Task GHR_04_CR_096_BR_02_a_change_in_a_locked_period_is_flagged_never_refused_and_only_a_termination_there_needs_an_override()
    {
        var locked = new AffectedPeriod("payroll", Jan1, new DateOnly(2026, 1, 31), Locked: true);
        var open = new AffectedPeriod("attendance", new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), Locked: false);
        var before = new AffectedPeriod("payroll", new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31), Locked: true);
        var check = new EmploymentChangeCheck([new FakeModule(locked, open, before)]);
        var promotion = new EmploymentChange(Guid.NewGuid(), Guid.NewGuid(), "promotion", new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15), IsTermination: false);

        var report = await check.RunAsync(promotion, new DateOnly(2026, 3, 1), CancellationToken.None);
        Assert.Equal(ImpactReport.PeriodsFlagged, report.Summary);
        Assert.Equal([locked, open], report.Flagged); // the December period ends before the earliest affected day
        Assert.False(report.TerminationNeedsOverride);

        var termination = await check.RunAsync(promotion with { Kind = "separation", IsTermination = true }, new DateOnly(2026, 3, 1), CancellationToken.None);
        Assert.True(termination.TerminationNeedsOverride);

        // BR-02: a termination before the last attendance needs the override too, even with no locked period.
        var attendance = new EmploymentChangeCheck([new FakeModule([], new DateOnly(2026, 2, 20))]);
        Assert.True((await attendance.RunAsync(promotion with { IsTermination = true }, new DateOnly(2026, 3, 1), CancellationToken.None)).TerminationNeedsOverride);
        Assert.False((await attendance.RunAsync(promotion with { IsTermination = true, EffectiveDate = new DateOnly(2026, 2, 25) }, new DateOnly(2026, 3, 1), CancellationToken.None)).TerminationNeedsOverride);
    }

    [Fact]
    public void FR_HRM_EMP_02_FR_HRM_EMP_03_the_catalogue_is_provisional_data_with_reasons_for_every_kind_and_the_employee_types_of_the_spec()
    {
        var catalogue = EmploymentCatalogue.Default;
        Assert.Equal("provisional", catalogue.Status);
        Assert.Equal(["permanent", "contract", "probation", "intern", "daily_wage", "consultant", "part_time"], catalogue.EmployeeTypes.Select(t => t.Code));
        Assert.All(EmploymentCatalogue.ChangeKinds.Append("hire"), k => Assert.NotEmpty(catalogue.Kinds.Single(x => x.Code == k).Reasons));
        Assert.All(catalogue.Kinds.SelectMany(k => k.Reasons.Select(r => r.Name["ur"]).Append(k.Name["ur"])).Concat(catalogue.EmployeeTypes.Select(t => t.Name["ur"])), u => Assert.Matches(@"\p{IsArabic}", u));
        Assert.True(catalogue.IsReason("promotion", "merit"));
        Assert.False(catalogue.IsReason("promotion", "new_hire"));
    }

    [Fact]
    public void FR_HRM_ROL_01_the_employment_permissions_and_every_text_exist_in_both_languages()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json")));
        var declared = manifest.RootElement.GetProperty("permissions").EnumerateArray().ToDictionary(p => p.GetProperty("code").GetString()!, p => p.GetProperty("sensitive").GetBoolean());
        Assert.All(EmploymentPermissions.HandWritten, c => Assert.True(declared.ContainsKey(c), c));
        Assert.True(declared[EmploymentPermissions.NumberOverride]);
        Assert.True(declared[EmploymentPermissions.UserLink]);

        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Employments/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "employment.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "employment.ur.json")))!;
        var people = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/People/Localization/people.en.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(EmploymentPermissions.HandWritten, c => Assert.True(en.ContainsKey("permissions." + c), c));

        // Every hrm key the employment code answers with has a text (person keys in people.en.json, generic ones in the kernel's).
        var sources = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories).Where(f => f.Contains("Employments", StringComparison.Ordinal));
        var used = sources.SelectMany(f => HrmKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k) || people.ContainsKey(k), k));
    }

    [GeneratedRegex("\"((?:errors\\.hrm|hrm\\.impact)\\.[a-z_.]+)\"")]
    private static partial Regex HrmKey();

    private sealed class FakeModule(IReadOnlyList<AffectedPeriod> periods, DateOnly? lastAttendance) : IEmploymentChangeImpact
    {
        public FakeModule(params AffectedPeriod[] periods)
            : this((IReadOnlyList<AffectedPeriod>)periods, null)
        {
        }

        public int Calls { get; private set; }

        public string ModuleCode => "test";

        public Task<EmploymentImpactAnswer> AnalyseAsync(EmploymentChange change, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new EmploymentImpactAnswer(ModuleCode, periods, lastAttendance, []));
        }
    }
}
