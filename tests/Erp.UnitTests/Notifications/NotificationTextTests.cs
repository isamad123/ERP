using System.Text.Json;
using Erp.Kernel.Contracts.Notifications;
using Erp.Kernel.Domain.Notifications;
using Erp.Kernel.Infrastructure.Notifications;

namespace Erp.UnitTests.Notifications;

/// <summary>Notification text, quiet hours, delivery policy and catalogue of slice S1.11b1 (Build items 8, 9 and 13).</summary>
public sealed class NotificationTextTests
{
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    private static readonly Dictionary<string, NotificationField> Fields = new(StringComparer.Ordinal)
    {
        ["amount"] = new("amount", "money", "salary"),
        ["employee"] = new("employee", "text", "personal"),
        ["document_type"] = new("document_type", "text", "general"),
    };

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void S111b1_GHR_06_a_salary_field_is_refused_in_every_channel_and_a_personal_field_in_email()
    {
        Assert.Equal(["errors.notification.field_salary_class"], NotificationText.Check("Pay is {amount}", Fields, "in_app"));
        Assert.Equal(["errors.notification.field_salary_class"], NotificationText.Check("Pay is {amount}", Fields, "email"));
        Assert.Empty(NotificationText.Check("About {employee}", Fields, "in_app"));
        Assert.Equal(["errors.notification.field_personal_in_email"], NotificationText.Check("About {employee}", Fields, "email"));
        Assert.Equal(["errors.notification.field_unknown"], NotificationText.Check("About {bogus}", Fields, "in_app"));
        Assert.Empty(NotificationText.Check("A {document_type} waits. No placeholder at {end", Fields, "email"));
    }

    [Fact]
    public void S111b1_KRN_LOC_07_dates_and_numbers_follow_the_recipients_language_and_a_missing_value_is_empty()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["day"] = Json("\"2026-11-30\""),
            ["used"] = Json("45000"),
            ["share"] = Json("12.5"),
            ["ok"] = Json("true"),
        };
        Assert.Equal("On 30 Nov 2026: 45,000, 12.5, yes[]", NotificationText.Render("On {day}: {used}, {share}, {ok}[{missing}]", values, "en"));
        Assert.Equal("30 نومبر 2026 / ہاں", NotificationText.Render("{day} / {ok}", values, "ur"));
    }

    [Fact]
    public void S111b1_KRN_NTF_04_quiet_hours_past_midnight_hold_until_they_end_and_hours_outside_them_send_at_once()
    {
        var (from, to) = (new TimeOnly(22, 0), new TimeOnly(7, 0));
        var at2330 = new DateTimeOffset(2026, 11, 18, 18, 30, 0, TimeSpan.Zero); // 23:30 in Karachi (UTC+5)
        var (quiet, ends) = NotificationText.QuietPeriod(at2330, Karachi, from, to);
        Assert.True(quiet);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 2, 0, 0, TimeSpan.Zero), ends); // 07:00 next day

        var at0300 = new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.Zero); // 03:00 in Karachi
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 2, 0, 0, TimeSpan.Zero), NotificationText.QuietPeriod(at0300, Karachi, from, to).EndsAt); // 07:00 the same local day
        Assert.False(NotificationText.QuietPeriod(new DateTimeOffset(2026, 11, 18, 5, 0, 0, TimeSpan.Zero), Karachi, from, to).Quiet); // 10:00
        Assert.False(NotificationText.QuietPeriod(at2330, Karachi, from, from).Quiet); // no window
        Assert.True(NotificationText.QuietPeriod(new DateTimeOffset(2026, 11, 18, 6, 0, 0, TimeSpan.Zero), Karachi, new TimeOnly(9, 0), new TimeOnly(17, 0)).Quiet); // 11:00 in a daytime window
        Assert.True(NotificationText.TryTime("07:05", out _));
        Assert.False(NotificationText.TryTime("7:05", out _));
        Assert.False(NotificationText.TryTime("25:00", out _));
    }

    [Fact]
    public void S111b1_KRN_NTF_04_mandatory_email_ignores_preferences_and_quiet_hours_and_other_email_follows_them()
    {
        var night = new DateTimeOffset(2026, 11, 18, 18, 30, 0, TimeSpan.Zero);
        var (from, to) = (new TimeOnly(22, 0), new TimeOnly(7, 0));
        Assert.Equal(EmailTiming.Now, NotificationPolicy.Email(true, false, night, Karachi, from, to).Timing);
        Assert.Equal(EmailTiming.Skip, NotificationPolicy.Email(false, false, night, Karachi, from, to).Timing);
        var held = NotificationPolicy.Email(false, true, night, Karachi, from, to);
        Assert.Equal(EmailTiming.Held, held.Timing);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 2, 0, 0, TimeSpan.Zero), held.SendAt);
        Assert.Equal(EmailTiming.Now, NotificationPolicy.Email(false, true, night, Karachi, null, null).Timing);
    }

    [Fact]
    public void S111b1_HRM_NTF_02_the_kernel_catalogue_is_valid_and_every_event_has_an_english_and_an_urdu_text_and_a_link()
    {
        var catalogue = new NotificationCatalog(Array.Empty<NotificationManifestSource>());
        Assert.True(catalogue.All.Count >= 20);
        Assert.All(catalogue.All, e =>
        {
            Assert.Empty(NotificationCatalog.Check(e));
            Assert.Matches(@"\p{IsArabic}", e.Texts["ur"].Title);
            // Every event has a deep link, except the identity e-mails (S1.11b2, P5), whose link is the encrypted secret.
            Assert.True(e.Direct || e.LinkProperty is not null || e.LinkTemplate is not null);
        });
        Assert.Equal(["workflow.task_assigned"], catalogue.ForSource("kernel.workflow.task_assigned").Select(e => e.Code));
        Assert.Equal(3, catalogue.ForSource("kernel.workflow.instance_completed").Count());
        Assert.True(catalogue.Find("licence.locked")!.Mandatory);
        Assert.False(catalogue.Find("licence.limit_warning")!.Mandatory);
    }

    [Fact]
    public void S111b1_GHR_06_a_catalogue_text_that_uses_a_salary_field_or_misses_urdu_is_refused()
    {
        var good = NotificationCatalog.Parse(NotificationCatalog.KernelJson).First(e => e.Code == "workflow.task_assigned");
        var salary = good with { Fields = [.. good.Fields, new NotificationField("net_pay", "money", "salary")], Texts = new Dictionary<string, NotificationTextSpec> { ["en"] = new("Pay {net_pay}", "x"), ["ur"] = good.Texts["ur"] } };
        Assert.Contains(NotificationCatalog.Check(salary), p => p.Contains("field_salary_class", StringComparison.Ordinal));
        var noUrdu = good with { Texts = new Dictionary<string, NotificationTextSpec> { ["en"] = good.Texts["en"] } };
        Assert.Contains(NotificationCatalog.Check(noUrdu), p => p.Contains("ur", StringComparison.Ordinal));
        Assert.Contains(NotificationCatalog.Check(good with { Group = "sports" }), p => p.StartsWith("group", StringComparison.Ordinal));
        Assert.Contains(NotificationCatalog.Check(good with { LinkProperty = null, LinkTemplate = null }), p => p.Contains("deep link", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => new NotificationCatalog([good, good]));
    }

    [Fact]
    public void S111b1_the_notification_texts_exist_in_english_and_urdu()
    {
        var folder = Path.Combine(RepositoryRoot(), "src/Platform/Erp.Kernel.Contracts/Notifications/Localization");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "notifications.en.json")))!;
        var ur = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, "notifications.ur.json")))!;
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ur.Keys.Order(StringComparer.Ordinal));
        Assert.All(NotificationPermissions.All, p => Assert.Contains(p.DescriptionKey, en.Keys));
        var errors = typeof(NotificationErrors).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);
        Assert.All(errors, key => Assert.Contains(key, en.Keys));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Erp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
