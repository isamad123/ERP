#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using static Erp.IntegrationTests.Packs.PackTestSupport;
using static Erp.IntegrationTests.Policies.PolicyTestSupport;

namespace Erp.IntegrationTests.Packs;

/// <summary>
/// The cases of the S1.23b review (`docs/reviews/S1.23b-review.md`) that the first tests missed: FR-KRN-CFG-02 and FR-HRM-POL-06 for pack
/// policies (an HR version dated in the future is the customer's; the pack's own version written for tomorrow is not), "take the pack
/// value" under HR's own permission, scope and approval setting, held actions that keep what was asked (FR-XM-32), retiring and leaving
/// objects in place, one transaction per pack, and an object the customer already had.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PackReviewTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Packs = "/api/cfg/packs";
    private AccessHarness _h = null!;
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    /// <summary>The company's date (Asia/Karachi) on the harness clock; the harness's own <c>Today</c> is the UTC date, a day behind after 19:00 UTC.</summary>
    private DateOnly CompanyToday => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);


    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await KeyAsync(fx);
    }

    public ValueTask DisposeAsync()
    {
        _h.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private async Task<(string Code, Guid Admin, Guid Hr)> CustomerAsync(string prefix)
    {
        var code = await _h.ProvisionAsync(prefix + Guid.NewGuid().ToString("N")[..4], Ct);
        return (code, await _h.UserAsync(Ct, code, "tenant_admin"), await _h.UserAsync(Ct, code, "hr_admin"));
    }

    private async Task<T> ScalarAsync<T>(string code, string sql, params object[] values)
    {
        await using var c = await _h.OwnerAsync(code, Ct);
        return await AccessHarness.ScalarAsync<T>(c, sql, Ct, values);
    }

    private async Task ExecAsync(string code, string sql, params object?[] values)
    {
        await using var c = await _h.OwnerAsync(code, Ct);
        await AccessHarness.ExecAsync(c, sql, Ct, values);
    }

    private static TestObject Policy(string key, int days) => new("policy", key, new { name = new { en = "Pack leave" }, parameters = new { days } });

    private static string PolicyKey(string id) => "test_leave:PK" + Code(id)[2..10].ToUpperInvariant();

    private async Task InstallAsync(string code, Guid admin, string id)
    {
        var installed = await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code);
        Assert.True(installed.Status == HttpStatusCode.OK, installed.Body);
    }

    /// <summary>HR's own version of the pack policy, activated from <paramref name="from"/> (S1.23a: a draft of a pack policy is an override).</summary>
    private async Task HrVersionAsync(string code, Guid hr, Guid policy, int days, DateOnly from)
    {
        var draft = await _h.PostAsync(hr, $"/api/hrm/policies/{policy}/versions", new { parameters = new { days } }, Ct, code);
        Assert.True(draft.Status == HttpStatusCode.Created, draft.Body);
        var activated = await _h.PostAsync(hr, $"/api/hrm/policy-versions/{draft.Json.GetProperty("id").GetGuid()}/activate",
            new { effectiveFrom = Iso(from), rowVersion = draft.Json.GetProperty("rowVersion").GetInt64() }, Ct, code);
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Body);
    }

    private async Task<int> ChangedAsync(string code, Guid admin, string id)
    {
        var items = await _h.GetAsync(admin, $"{Packs}/{id}/items?changed=true", Ct, code);
        Assert.True(items.Status == HttpStatusCode.OK, items.Body);
        return items.Json.GetProperty("total").GetInt32();
    }

    // ------------------------------------------------------------------ review findings 1 and 3

    [Fact]
    public async Task FR_KRN_CFG_02_FR_HRM_POL_06_an_upgrade_keeps_an_HR_version_dated_in_the_future_and_a_pending_draft_does_not_count()
    {
        var id = NewId();
        var key = PolicyKey(id);
        await PublishAsync(fx, id, "1.0.0", [Policy(key, 10)]);
        var (code, admin, hr) = await CustomerAsync("f");
        await InstallAsync(code, admin, id);
        var policy = await ScalarAsync<Guid>(code, "select id from hrm.policies where source = 'pack'");

        var draft = await _h.PostAsync(hr, $"/api/hrm/policies/{policy}/versions", new { parameters = new { days = 15 } }, Ct, code);
        Assert.True(draft.Status == HttpStatusCode.Created, draft.Body);
        Assert.Equal(0, await ChangedAsync(code, admin, id)); // a draft is not a version
        var activated = await _h.PostAsync(hr, $"/api/hrm/policy-versions/{draft.Json.GetProperty("id").GetGuid()}/activate",
            new { effectiveFrom = Iso(CompanyToday.AddDays(30)), rowVersion = draft.Json.GetProperty("rowVersion").GetInt64() }, Ct, code);
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Body);
        Assert.Equal(1, await ChangedAsync(code, admin, id)); // HR's version from day +30 is the customer's value today

        await PublishAsync(fx, id, "1.0.1", [Policy(key, 12)]);
        var upgraded = await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { version = "1.0.1" }, Ct, code);
        Assert.True(upgraded.Status == HttpStatusCode.OK, upgraded.Body);
        Assert.Equal((0, 1), (upgraded.Json.GetProperty("result").GetProperty("updated").GetInt32(), upgraded.Json.GetProperty("result").GetProperty("kept").GetInt32()));
        Assert.Equal(1L, await ScalarAsync<long>(code, "select count(*) from hrm.policy_versions where policy_id = $1 and parameters = '{\"days\": 15}'::jsonb and valid_to is null", policy));
        Assert.Equal(0L, await ScalarAsync<long>(code, "select count(*) from hrm.policy_versions where policy_id = $1 and parameters = '{\"days\": 12}'::jsonb", policy));
    }

    [Fact]
    public async Task FR_KRN_CFG_02_a_pack_version_written_for_tomorrow_is_not_counted_as_a_customer_change_on_the_following_days()
    {
        var id = NewId();
        var key = PolicyKey(id);
        await PublishAsync(fx, id, "1.0.0", [Policy(key, 10)]);
        var (code, admin, _) = await CustomerAsync("t");
        await InstallAsync(code, admin, id);
        await PublishAsync(fx, id, "1.0.1", [Policy(key, 12)]);
        var same = await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { version = "1.0.1" }, Ct, code);
        Assert.Equal(1, same.Json.GetProperty("result").GetProperty("updated").GetInt32()); // starts tomorrow (a version in force is never edited)

        _h.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, await ChangedAsync(code, admin, id));
        await PublishAsync(fx, id, "1.0.2", [Policy(key, 14)]);
        var next = await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { version = "1.0.2" }, Ct, code);
        Assert.True(next.Status == HttpStatusCode.OK, next.Body);
        Assert.Equal((1, 0), (next.Json.GetProperty("result").GetProperty("updated").GetInt32(), next.Json.GetProperty("result").GetProperty("kept").GetInt32()));
    }

    // ------------------------------------------------------------------ review findings 2 and 4

    [Fact]
    public async Task FR_HRM_POL_06_FR_XM_32_taking_the_pack_value_of_a_policy_needs_HRs_right_and_waits_for_HRs_approval_setting()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0", [Policy(PolicyKey(id), 10)]);
        var (code, admin, hr) = await CustomerAsync("g");
        await InstallAsync(code, admin, id);
        var policy = await ScalarAsync<Guid>(code, "select id from hrm.policies where source = 'pack'");
        var company = await ScalarAsync<Guid>(code, "select company_id from hrm.policies where id = $1", policy);
        await HrVersionAsync(code, hr, policy, 15, CompanyToday.AddDays(1));
        var item = await ScalarAsync<Guid>(code, "select id from cfg.pack_items where pack_id = $1", id);

        // The tenant administrator holds the pack permissions but not HR's: refused.
        var refused = await _h.PostAsync(admin, $"{Packs}/items/{item}/take-pack-value", new { }, Ct, code);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);

        // A person with both rights, while HR's own approval setting is on for the company: held for a second person.
        var both = await _h.RoleAsync(Ct, ["cfg.installed_packs.read", "cfg.installed_packs.update", "cfg.installed_packs.approve", "hrm.policies.read", "hrm.policies.manage"], [("hrm.policies", "all")], code: code);
        var maker = await _h.UserAsync(Ct, code);
        var checker = await _h.UserAsync(Ct, code);
        await _h.AssignAsync(maker, both, null, _h.Today.AddDays(-1), null, Ct, code);
        await _h.AssignAsync(checker, both, null, _h.Today.AddDays(-1), null, Ct, code);
        await ExecAsync(code, """
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, 'hrm.policies.requires_approval', 'company', $2, 'true', now(), $3, now(), $3)
            """, Guid.CreateVersion7(), company, BaselineSeed.SystemUserId);
        var held = await _h.PostAsync(maker, $"{Packs}/items/{item}/take-pack-value", new { reason = "back to the pack" }, Ct, code);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var request = held.Json.GetProperty("requestId").GetGuid();

        // The approver sees the object and both values (finding 4).
        var pending = await _h.GetAsync(checker, "/api/iam/maker-checker-requests/pending", Ct, code);
        Assert.True(pending.Status == HttpStatusCode.OK, pending.Body);
        Assert.Contains("packs.held.detail.customer_value", pending.Body, StringComparison.Ordinal);
        Assert.Contains("packs.held.detail.pack_value", pending.Body, StringComparison.Ordinal);

        // The tenant administrator holds the pack approval right but not HR's: may not approve.
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(admin, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "x" }, Ct, code)).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await _h.PostAsync(maker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct, code)).Status);
        var approved = await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "agreed" }, Ct, code);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("{\"days\": 10}", await ScalarAsync<string>(code,
            "select parameters::text from hrm.policy_versions where policy_id = $1 and status <> 'draft' order by valid_from desc limit 1", policy));
        Assert.Equal(0, await ChangedAsync(code, admin, id));
    }

    [Fact]
    public async Task FR_XM_32_a_held_upgrade_installs_the_version_that_was_asked_for_and_a_held_take_is_refused_once_the_offered_value_changed()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0", Content(id));
        var (code, admin, _) = await CustomerAsync("h");
        var second = await _h.UserAsync(Ct, code, "tenant_admin");
        await InstallAsync(code, admin, id);
        await ExecAsync(code, "update cfg.option_list_items i set label_i18n = '{\"en\": \"Partner\"}' from cfg.option_lists l where l.id = i.list_id and l.code = $1 and i.code = 'spouse'", Code(id));
        await PublishAsync(fx, id, "1.0.1", Content(id, itemLabel: "Spouse one"));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { version = "1.0.1" }, Ct, code)).Status);
        var item = await ScalarAsync<Guid>(code, "select id from cfg.pack_items where pack_id = $1 and object_type = 'option_list_item' and object_key like '%:spouse'", id);

        await ExecAsync(code, """
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, 'cfg.packs.requires_approval', 'tenant', null, 'true', now(), $2, now(), $2)
            """, Guid.CreateVersion7(), BaselineSeed.SystemUserId);
        var take = await _h.PostAsync(admin, $"{Packs}/items/{item}/take-pack-value", new { }, Ct, code);
        Assert.True(take.Status == HttpStatusCode.Accepted, take.Body);
        await PublishAsync(fx, id, "1.0.2", Content(id, itemLabel: "Spouse two"));
        var upgrade = await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { }, Ct, code); // no version named: the newest, 1.0.2, is fixed now
        Assert.True(upgrade.Status == HttpStatusCode.Accepted, upgrade.Body);
        await PublishAsync(fx, id, "1.0.3", Content(id, itemLabel: "Spouse three"));

        // The upgrade approved after 1.0.3 was published installs 1.0.2, which the requester asked for.
        var approved = await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{upgrade.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct, code);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("1.0.2", await ScalarAsync<string>(code, "select version from cfg.installed_packs where pack_id = $1", id));

        // The take asked for "Spouse one"; the upgrade now offers "Spouse two": refused, the customer's value stays.
        var stale = await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{take.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct, code);
        Assert.NotEqual(HttpStatusCode.OK, stale.Status);
        Assert.Contains("errors.packs.changed_since_request", stale.Body, StringComparison.Ordinal);
        Assert.Equal("Partner", await ScalarAsync<string>(code, "select i.label_i18n->>'en' from cfg.option_list_items i join cfg.option_lists l on l.id = i.list_id where l.code = $1 and i.code = 'spouse'", Code(id)));
    }

    // ------------------------------------------------------------------ review findings 7 and 10

    [Fact]
    public async Task FR_KRN_CFG_02_objects_that_leave_the_pack_are_retired_or_left_in_place_when_still_in_use()
    {
        var id = NewId();
        var cal = Code(id) + "-cal";
        var key = PolicyKey(id);
        var future = CompanyToday.AddYears(2);
        var past = CompanyToday.AddYears(-2);
        await PublishAsync(fx, id, "1.0.0",
        [
            new("setting", "cfg.numbering.fiscal_year_start_month", new { value = 7 }),
            new("label", "labels.test." + Code(id), new { texts = new { en = "Pack term" } }),
            new("holiday_calendar", cal, new { name = new { en = "Holidays" } }),
            new("holiday", cal + ":" + Iso(future), new { name = new { en = "Future day" }, kind = "public" }),
            new("holiday", cal + ":" + Iso(past), new { name = new { en = "Past day" }, kind = "public" }),
            Policy(key, 10),
        ]);
        var (code, admin, hr) = await CustomerAsync("r");
        await InstallAsync(code, admin, id);
        var policy = await ScalarAsync<Guid>(code, "select id from hrm.policies where source = 'pack'");
        var company = await ScalarAsync<Guid>(code, "select company_id from hrm.policies where id = $1", policy);
        var set = await _h.PostAsync(hr, $"{Sets}/activate", new
        {
            companyId = company, policyType = "test_leave", effectiveFrom = Iso(CompanyToday), basedOn = (string?)null,
            assignments = new[] { new { policyId = policy, precedence = 1, rule = new { conditions = Array.Empty<object>() } } },
        }, Ct, code);
        Assert.True(set.Status == HttpStatusCode.OK, set.Body);

        await PublishAsync(fx, id, "1.0.1", [new("holiday_calendar", cal, new { name = new { en = "Holidays" } })]);
        var upgraded = await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { }, Ct, code);
        Assert.True(upgraded.Status == HttpStatusCode.OK, upgraded.Body);
        var report = upgraded.Json.GetProperty("result");
        Assert.Equal((3, 2), (report.GetProperty("retired").GetInt32(), report.GetProperty("leftInPlace").GetInt32()));
        Assert.Equal(0L, await ScalarAsync<long>(code, "select count(*) from cfg.setting_values where setting_key = 'cfg.numbering.fiscal_year_start_month' and layer = 'pack'"));
        Assert.Equal(0L, await ScalarAsync<long>(code, "select count(*) from cfg.labels where key = $1", "labels.test." + Code(id)));
        Assert.Equal(0L, await ScalarAsync<long>(code, "select count(*) from ref.holidays where holiday_date = $1", future));
        Assert.Equal(1L, await ScalarAsync<long>(code, "select count(*) from ref.holidays where holiday_date = $1", past));
        Assert.True(await ScalarAsync<bool>(code, "select is_active from hrm.policies where id = $1", policy));
        Assert.Equal(2L, await ScalarAsync<long>(code, "select count(*) from cfg.pack_items where pack_id = $1 and retired_in is null and object_type in ('holiday', 'policy')", id));
    }

    [Fact]
    public async Task FR_KRN_CFG_02_one_object_that_cannot_be_written_rolls_the_whole_pack_back()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0",
        [
            new("bank", "PK:" + Code(id), new { name = new { en = "Rolled back bank" } }),
            new("option_list_item", "no_such_list_" + Code(id) + ":x", new { label = new { en = "Orphan" }, seq = 1 }),
        ]);
        var (code, admin, _) = await CustomerAsync("b");
        var refused = await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("errors.packs.parent_missing", refused.Body, StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync<long>(code, "select count(*) from ref.banks where code = $1", Code(id)));
        Assert.Equal(0L, await ScalarAsync<long>(code, "select count(*) from cfg.installed_packs where pack_id = $1", id));
        Assert.Equal(0L, await ScalarAsync<long>(code, "select count(*) from cfg.pack_items where pack_id = $1", id));
    }

    [Fact]
    public async Task FR_KRN_CFG_02_an_object_the_customer_already_had_is_kept_at_the_first_install_with_the_packs_value_offered()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0", [new("option_list", Code(id), new { name = new { en = "Pack relations" } })]);
        var (code, admin, _) = await CustomerAsync("k");
        await ExecAsync(code, """
            insert into cfg.option_lists (id, code, name_i18n, is_system, is_active, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en": "Our relations"}', false, true, now(), $3, now(), $3)
            """, Guid.CreateVersion7(), Code(id), BaselineSeed.SystemUserId);
        var installed = await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code);
        Assert.True(installed.Status == HttpStatusCode.OK, installed.Body);
        Assert.Equal((0, 1), (installed.Json.GetProperty("result").GetProperty("added").GetInt32(), installed.Json.GetProperty("result").GetProperty("kept").GetInt32()));
        Assert.Equal("Our relations", await ScalarAsync<string>(code, "select name_i18n->>'en' from cfg.option_lists where code = $1", Code(id)));
        var items = await _h.GetAsync(admin, $"{Packs}/{id}/items?changed=true", Ct, code);
        Assert.Equal("1.0.0", items.Json.GetProperty("items")[0].GetProperty("conflictVersion").GetString());
    }
}
