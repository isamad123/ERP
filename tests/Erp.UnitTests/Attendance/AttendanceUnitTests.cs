using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;

namespace Erp.UnitTests.Attendance;

/// <summary>Pure rules of attendance capture (slice S1.27a): network ranges, QR periods, duplicate window, texts and permissions.</summary>
public sealed partial class AttendanceUnitTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
            {
                dir = dir.Parent;
            }

            return dir!.FullName;
        }
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.0/24", true)]
    [InlineData("203.0.114.9", "203.0.113.0/24", false)]
    [InlineData("10.1.2.3", "10.1.2.3", true)]
    [InlineData("::ffff:203.0.113.9", "203.0.113.0/24", true)]
    [InlineData("2001:db8::1", "2001:db8::/32", true)]
    [InlineData("203.0.113.9", "not a range", false)]
    public void FR_HRM_ATT_01_an_address_is_allowed_only_inside_a_listed_range(string address, string range, bool expected) =>
        Assert.Equal(expected, AttendanceRules.AddressAllowed(address, [range]));

    [Fact]
    public void FR_HRM_ATT_01_no_ranges_means_anywhere_and_an_unknown_address_is_refused_when_ranges_exist()
    {
        Assert.True(AttendanceRules.AddressAllowed(null, []));
        Assert.False(AttendanceRules.AddressAllowed(null, ["10.0.0.0/8"]));
        Assert.True(AttendanceRules.IsRange("10.0.0.0/8"));
        Assert.False(AttendanceRules.IsRange("10.0.0.0/99"));
    }

    [Fact]
    public void FR_HRM_ATT_01_a_qr_code_is_taken_in_its_own_period_and_the_one_before_only()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_030);
        var period = AttendanceRules.QrPeriod(now, 60);
        Assert.Equal(1_800_000_030 / 60, period);
        Assert.True(AttendanceRules.QrPeriodAccepted(period, period));
        Assert.True(AttendanceRules.QrPeriodAccepted(period - 1, period));
        Assert.False(AttendanceRules.QrPeriodAccepted(period - 2, period));
        Assert.False(AttendanceRules.QrPeriodAccepted(period + 1, period));
        Assert.True(AttendanceRules.QrPeriodEnd(period, 60) > now);
        Assert.Throws<ArgumentOutOfRangeException>(() => AttendanceRules.QrPeriod(now, 0));
    }

    [Fact]
    public void FR_HRM_ATT_03_the_duplicate_window_works_both_ways_and_zero_switches_it_off()
    {
        var t = DateTimeOffset.UtcNow;
        Assert.True(AttendanceRules.WithinWindow(t, t.AddSeconds(60), 60));
        Assert.True(AttendanceRules.WithinWindow(t, t.AddSeconds(-30), 60));
        Assert.False(AttendanceRules.WithinWindow(t, t.AddSeconds(61), 60));
        Assert.False(AttendanceRules.WithinWindow(t, t, 0));
    }

    [Fact]
    public void FR_HRM_DAT_02_the_stored_key_names_source_and_scope()
    {
        Assert.Equal("web:abc:k1", AttendanceRules.StoredKey("web", "abc", "k1"));
        Assert.NotEqual(AttendanceRules.StoredKey("web", "a", "k"), AttendanceRules.StoredKey("qr", "a", "k"));
    }

    [Fact]
    public void FR_HRM_ATT_01_the_attendance_permissions_and_every_text_exist_in_both_languages()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Infrastructure/Access/permissions.json")));
        var declared = manifest.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("code").GetString()!).ToHashSet(StringComparer.Ordinal);
        string[] codes = [AttendancePermissions.EventsAppend, AttendancePermissions.Punch, AttendancePermissions.Kiosk, AttendancePermissions.QrDisplay];
        Assert.All(codes, c => Assert.Contains(c, declared));

        var folder = Path.Combine(Root, "src/Modules/Hrm/Erp.Hrm.Contracts/Attendance/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "attendance.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "attendance.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(ur.Values, v => Assert.Matches(@"\p{IsArabic}", v));
        Assert.All(codes, c => Assert.True(en.ContainsKey("permissions." + c), c));

        var sources = Directory.GetFiles(Path.Combine(Root, "src/Modules/Hrm"), "*.cs", SearchOption.AllDirectories).Where(f => f.Contains("Attendance", StringComparison.Ordinal) && !f.Contains("Generated", StringComparison.Ordinal));
        var used = sources.SelectMany(f => AttendanceKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), k));
    }

    [GeneratedRegex("\"(errors\\.hrm\\.attendance\\.[a-z_]+)\"")]
    private static partial Regex AttendanceKey();
}
