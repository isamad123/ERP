using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.CodeGen;
using Erp.Hrm.Contracts.People;
using Erp.Hrm.Infrastructure.People;

namespace Erp.UnitTests.People;

/// <summary>Slice S1.20a: the provisional identifier catalogue (ruling Q3), name normalization and the HR texts and permissions.</summary>
public sealed partial class PeopleUnitTests
{
    private static readonly string Root = RepositoryRoot.Find(AppContext.BaseDirectory);

    [Theory]
    [InlineData("1234512345671", true)]
    [InlineData("12345-1234567-1", true)]
    [InlineData("12345-12345671", false)]
    [InlineData("1234-51234567-1", false)]
    [InlineData("123451234567", false)]
    [InlineData("12345-1234567-12", false)]
    [InlineData("abcde-1234567-1", false)]
    public void FR_HRM_EMP_05_Q3_a_CNIC_is_13_digits_in_the_dash_layout_only(string value, bool valid)
    {
        var cnic = PeopleCatalogue.Default.Find("cnic", "PK")!;
        Assert.Equal(valid, PeopleCatalogue.Default.Matches(cnic, value));
    }

    [Fact]
    public void FR_HRM_EMP_16_Q3_identifier_kinds_are_pack_data_marked_provisional_and_a_passport_is_any_countrys()
    {
        var kinds = PeopleCatalogue.Default.IdentifierKinds;
        Assert.Equal(["cnic", "nicop", "passport", "ntn", "eobi", "social_security"], kinds.Select(k => k.Code));
        Assert.All(kinds, k => Assert.Equal("provisional", k.Status));
        Assert.All(kinds, k => Assert.True(PeopleCatalogue.Default.Matches(k, k.Example), k.Code + " example"));
        Assert.All(kinds, k => Assert.Matches(@"\p{IsArabic}", k.Name["ur"]));
        Assert.Equal("*", PeopleCatalogue.Default.Find("passport", "GB")!.Country);
        Assert.Null(PeopleCatalogue.Default.Find("ntn", "GB"));
        Assert.False(PeopleCatalogue.Default.Find("ntn", "PK")!.HasExpiry);
    }

    [Fact]
    public void FR_HRM_EMP_18_the_blind_index_input_ignores_layout_and_case()
    {
        var cnic = PeopleCatalogue.Default.Find("cnic", "PK")!;
        Assert.Equal(PeopleCatalogue.Normalize("12345-1234567-1"), PeopleCatalogue.Normalize("1234512345671"));
        Assert.Equal("AB1234567", PeopleCatalogue.Normalize(" ab 1234-567 "));
        Assert.Equal("12345-1234567-1", PeopleCatalogue.Display(cnic, "1234512345671"));
        Assert.Equal(PeopleCatalogue.NormalizeName("  Muhammad  ALI", null, "khan."), PeopleCatalogue.NormalizeName("muhammad", "ali", "Khan"));
        Assert.NotEqual(PeopleCatalogue.NormalizeName("Ali", null, "Khan"), PeopleCatalogue.NormalizeName("Khan", null, "Ali"));
    }

    [Fact]
    public void FR_HRM_ROL_01_the_hand_written_permissions_match_the_manifest_and_every_text_exists_in_both_languages()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json")));
        var codes = manifest.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("code").GetString()!).ToList();
        // The manifest holds the person codes (S1.20a), the employment codes (S1.20b, EmploymentUnitTests checks their texts) and the position codes (S1.22a, PositionUnitTests).
        // The manifest holds the person codes (S1.20a), the employment codes (S1.20b, EmploymentUnitTests checks their texts), the policy codes
        // (S1.23a, PolicyUnitTests checks their texts), the pay codes (S1.34a) and the attendance codes (S1.27a, AttendanceUnitTests).
        Assert.Equal(PeoplePermissions.HandWritten.Concat(Erp.Hrm.Contracts.Employments.EmploymentPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Policies.PolicyPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Payroll.PayPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Positions.PositionPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Attendance.ShiftPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Attendance.HolidayPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Leave.LeavePermissions.HandWritten).Concat([Erp.Hrm.Contracts.Attendance.AttendancePermissions.EventsAppend, Erp.Hrm.Contracts.Attendance.AttendancePermissions.Punch, Erp.Hrm.Contracts.Attendance.AttendancePermissions.Kiosk, Erp.Hrm.Contracts.Attendance.AttendancePermissions.QrDisplay]).Concat(Erp.Hrm.Contracts.Attendance.DevicePermissions.HandWritten).Concat(Erp.Hrm.Contracts.Attendance.AttendanceEnginePermissions.HandWritten).Concat(Erp.Hrm.Contracts.Attendance.AttendanceRequestPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Attendance.AttendanceReportPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Attendance.AttendanceConsequencePermissions.HandWritten).Concat(Erp.Hrm.Contracts.Attendance.AttendanceLockPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Payroll.PayRunPermissions.HandWritten).Concat(Erp.Hrm.Contracts.Expenses.ExpensePermissions.HandWritten).Order(StringComparer.Ordinal), codes.Order(StringComparer.Ordinal));
        Assert.True(manifest.RootElement.GetProperty("permissions").EnumerateArray().Single(p => p.GetProperty("code").GetString() == Erp.Hrm.Contracts.Policies.PolicyPermissions.Approve).GetProperty("sensitive").GetBoolean());
        codes = [.. PeoplePermissions.HandWritten];
        Assert.True(manifest.RootElement.GetProperty("permissions").EnumerateArray().Single(p => p.GetProperty("code").GetString() == PeoplePermissions.IdentifiersReveal).GetProperty("sensitive").GetBoolean());

        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/People/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "people.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "people.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(codes, c => Assert.True(en.ContainsKey("permissions." + c), c));

        // Every hrm error key the person code answers with has a text (generic keys come from the kernel catalogues).
        var sources = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories).Where(f => f.Contains("People", StringComparison.Ordinal));
        var used = sources.SelectMany(f => HrmKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), k));
    }

    [GeneratedRegex("\"(errors\\.hrm\\.[a-z_.]+)\"")]
    private static partial Regex HrmKey();
}
