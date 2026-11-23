#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.Settings;

/// <summary>
/// Slice S1.09a through the real API pipeline and the real access engine: manifests and registration, the resolver and its
/// layered view, set, reset, revert, approval, history and audit, permissions of the baseline roles, self-service and
/// feature flags (FR-KRN-SET-01 to 05, FR-ADM-SET-01 to 04 and 06, FR-ADM-USR-10). Settings that only these tests use are
/// registered under the module id <c>test</c>, which no manifest speaks for, so <c>migrate</c> never hides them.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class SettingsTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/cfg/settings";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Registers a test setting as a manifest would (definition only).</summary>
    private async Task<string> SettingAsync(string type = "int", string @default = "10", string validation = """{"min":1,"max":100}""",
        string[]? layers = null, string? roleRule = null, bool approval = false, bool selfService = false, bool visible = true)
    {
        var key = "test.s109_" + Guid.NewGuid().ToString("N")[..10];
        await _h.ExecAsync("""
            insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n,
                                      is_visible, area, name_i18n, help_i18n, role_rule, self_service)
            values ($1, 'test', $2, $3::jsonb, $4::jsonb, $5, $6, '{"en":"Test setting","ur":"آزمائشی ترتیب"}', $7, 'test', '{"en":"Test","ur":"آزمائش"}',
                    '{"en":"Help","ur":"مدد"}', $8, $9)
            """, Ct, key, type, @default, validation, layers ?? ["tenant", "company", "role", "user"], approval, visible, (object?)roleRule ?? DBNull.Value, selfService);
        return key;
    }

    private static object Set(string? layer, Guid? scope, object? value, string? reason = "test change") => new { layer, scopeId = scope, value, reason };

    // ------------------------------------------------------------------ FR-KRN-SET-01: manifests and registration

    [Fact]
    public async Task FR_KRN_SET_01_moving_existing_settings_into_manifests_changes_no_key_default_validation_or_text()
    {
        var golden = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Settings", "settings-before-s1-09a.json"), Ct)).RootElement.GetProperty("settings");
        Assert.Equal(22, golden.GetArrayLength());
        foreach (var expected in golden.EnumerateArray())
        {
            var key = expected.GetProperty("key").GetString()!;
            var row = await _h.ScalarAsync<string>("""
                select json_build_object('key', key, 'module_id', module_id, 'data_type', data_type, 'default_value', default_value, 'validation', validation,
                    'allowed_layers', allowed_layers, 'requires_approval', requires_approval, 'description_i18n', description_i18n)::text
                from cfg.settings where key = $1
                """, Ct, key);
            using var actual = JsonDocument.Parse(row);
            foreach (var property in expected.EnumerateObject())
            {
                Assert.True(JsonElement.DeepEquals(property.Value, actual.RootElement.GetProperty(property.Name)), $"{key}.{property.Name}: {actual.RootElement.GetProperty(property.Name)} instead of {property.Value}");
            }

            Assert.True(await _h.ScalarAsync<bool>("select is_visible and area is not null and name_i18n ? 'ur' and help_i18n ? 'ur' from cfg.settings where key = $1", Ct, key), key + " lacks manifest texts");
        }

        Assert.True(await _h.ScalarAsync<bool>("select tighten_only and role_rule = 'any' from cfg.settings where key = 'iam.mfa.required'", Ct));
    }

    [Fact]
    public async Task FR_KRN_SET_01_ruling_Q10_a_setting_no_longer_declared_is_kept_hidden_with_its_values_and_registration_is_idempotent()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        const string Orphan = "cfg.s109_retired_setting";
        await _h.ExecAsync("""
            insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n, is_visible, area)
            values ($1, 'cfg', 'int', '5', '{}', '{tenant}', false, '{"en":"Old","ur":"پرانا"}', true, 'test')
            on conflict (key) do update set is_visible = true
            """, Ct, Orphan);
        await _h.ExecAsync("""
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'tenant', null, '7', now(), $3, now(), $3) on conflict (setting_key, layer, scope_id) do nothing
            """, Ct, Guid.CreateVersion7(), Orphan, BaselineSeed.SystemUserId);
        await using (var owner = await _h.OwnerAsync(null, Ct))
        {
            await Erp.Kernel.Infrastructure.Settings.SettingsSetup.EnsureAsync(owner, null, _h.Fixture.Migrations.AccessFolders(["hrm"]), Ct);
            await Erp.Kernel.Infrastructure.Settings.SettingsSetup.EnsureAsync(owner, null, _h.Fixture.Migrations.AccessFolders(["hrm"]), Ct);
        }

        Assert.False(await _h.ScalarAsync<bool>("select is_visible from cfg.settings where key = $1", Ct, Orphan));
        Assert.Equal("7", await _h.ScalarAsync<string>("select value::text from cfg.setting_values where setting_key = $1", Ct, Orphan));
        Assert.Equal(35L, await _h.ScalarAsync<long>("select count(*) from cfg.settings where module_id in ('iam', 'cfg', 'org') and is_visible", Ct)); // + cfg.limits.warning_percent (S1.09b), org.weekly_off_days (S1.10b), four iam.password_reset/email_code settings (S1.11b2), iam.report.inactive_days (S1.13b), six cfg.branding.* (S1.15a)
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(admin, $"{Api}/{Orphan}", Ct)).Status);
        Assert.Equal(SettingsErrors.Unknown, (await _h.PutAsync(admin, $"{Api}/{Orphan}", Set("tenant", null, 8), Ct)).Text("title"));
    }

    // ------------------------------------------------------------------ FR-KRN-SET-02 and spec 02 SET AC

    [Fact]
    public async Task FR_KRN_SET_02_spec02_SET_AC_a_company_value_applies_only_to_that_company_shows_its_source_and_reset_restores_the_inherited_value()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var key = await SettingAsync();
        var a = await _h.CompanyAsync(Ct);
        var b = await _h.CompanyAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"{Api}/{key}", Set("tenant", null, 30), Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"{Api}/{key}", Set("company", a, 40), Ct)).Status);

        async Task<(int Value, string Source)> EffectiveAsync(Guid company)
        {
            var detail = (await _h.GetAsync(admin, $"{Api}/{key}?companyId={company}", Ct)).Json.GetProperty("setting");
            return (detail.GetProperty("value").GetInt32(), detail.GetProperty("source").GetString()!);
        }

        Assert.Equal((40, "company"), await EffectiveAsync(a));
        Assert.Equal((30, "tenant"), await EffectiveAsync(b));

        var list = (await _h.GetAsync(admin, $"{Api}?search={key}&companyId={a}", Ct)).Json;
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal("company", item.GetProperty("source").GetString());
        Assert.Equal(admin, item.GetProperty("lastChange").GetProperty("by").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"{Api}/{key}/reset", new { layer = "company", scopeId = a, reason = "back to the group value" }, Ct)).Status);
        Assert.Equal((30, "tenant"), await EffectiveAsync(a));
        Assert.Equal(SettingsErrors.NothingToReset, (await _h.PostAsync(admin, $"{Api}/{key}/reset", new { layer = "company", scopeId = a, reason = "again" }, Ct)).Text("title"));
    }

    [Fact]
    public async Task FR_KRN_SET_02_FR_ADM_SET_03_the_layered_view_shows_every_layer_and_skips_a_stored_value_that_is_no_longer_valid()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var key = await SettingAsync(roleRule: "highest");
        var company = await _h.CompanyAsync(Ct);
        var user = await _h.UserAsync(Ct, null, "company_admin");
        var role = await _h.RoleIdAsync("company_admin", Ct);
        await _h.PutAsync(admin, $"{Api}/{key}", Set("tenant", null, 30), Ct);
        await _h.PutAsync(admin, $"{Api}/{key}", Set("role", role, 50), Ct);

        // A company value stored by an older rule that the setting no longer accepts (above max 100).
        await _h.ExecAsync("""
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'company', $3, '500', now(), $4, now(), $4)
            """, Ct, Guid.CreateVersion7(), key, company, BaselineSeed.SystemUserId);

        var view = (await _h.GetAsync(admin, $"{Api}/{key}?companyId={company}&userId={user}", Ct)).Json;
        Assert.Equal(50, view.GetProperty("setting").GetProperty("value").GetInt32());
        Assert.Equal("role", view.GetProperty("setting").GetProperty("source").GetString());
        var layers = view.GetProperty("layers").EnumerateArray().ToDictionary(l => l.GetProperty("layer").GetString()!, l => l.GetProperty("ignored").ValueKind == JsonValueKind.Null ? null : l.GetProperty("ignored").GetString());
        Assert.Equal("invalid", layers["company"]);
        Assert.Equal("overridden", layers["tenant"]);
        Assert.Equal("overridden", layers["product"]);
        Assert.Null(layers["role"]);

        // Without the user, the role layer does not apply: the invalid company value is still skipped.
        var customer = (await _h.GetAsync(admin, $"{Api}/{key}?companyId={company}", Ct)).Json.GetProperty("setting");
        Assert.Equal((30, "tenant"), (customer.GetProperty("value").GetInt32(), customer.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task FR_KRN_SET_01_R019_an_unknown_time_zone_is_refused_when_written_and_a_known_one_is_accepted()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var url = $"{Api}/org.time_zone";

        Assert.Equal(SettingsErrors.TimeZoneUnknown, (await _h.PutAsync(admin, url, Set("tenant", null, "Mars/Base"), Ct)).Text("title"));
        Assert.Equal(SettingsErrors.TimeZoneUnknown, (await _h.PutAsync(admin, url, Set("tenant", null, ""), Ct)).Text("title"));
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, url, Set("tenant", null, "Europe/London"), Ct)).Status);
        Assert.Equal("Europe/London", (await _h.GetAsync(admin, url, Ct)).Json.GetProperty("setting").GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, url, Set("tenant", null, "Asia/Karachi"), Ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_SET_01_a_layer_the_setting_does_not_allow_a_bad_value_or_a_missing_reason_is_refused_when_written()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var key = await SettingAsync(layers: ["tenant", "company"]);
        var company = await _h.CompanyAsync(Ct);
        async Task<string?> TitleAsync(object body) => (await _h.PutAsync(admin, $"{Api}/{key}", body, Ct)).Text("title");

        Assert.Equal(SettingsErrors.LayerInvalid, await TitleAsync(Set("user", admin, 20)));
        Assert.Equal(SettingsErrors.LayerInvalid, await TitleAsync(Set("tenant", company, 20)));
        Assert.Equal(SettingsErrors.LayerInvalid, await TitleAsync(Set("company", null, 20)));
        Assert.Equal(SettingsErrors.PackLayer, await TitleAsync(Set("pack", null, 20)));
        Assert.Equal(SettingsErrors.ScopeNotFound, await TitleAsync(Set("company", Guid.CreateVersion7(), 20)));
        Assert.Equal(SettingsErrors.ValueInvalid, await TitleAsync(Set("tenant", null, 101)));
        Assert.Equal(SettingsErrors.ValueInvalid, await TitleAsync(Set("tenant", null, "twenty")));
        Assert.Equal(SettingsErrors.ReasonRequired, await TitleAsync(Set("tenant", null, 20, reason: " ")));
        Assert.Equal(SettingsErrors.Unknown, (await _h.PutAsync(admin, $"{Api}/test.no_such_setting", Set("tenant", null, 20), Ct)).Text("title"));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_values where setting_key = $1", Ct, key));
    }

    // ------------------------------------------------------------------ FR-KRN-SET-04, FR-ADM-SET-06: history, audit, revert

    [Fact]
    public async Task FR_KRN_SET_04_FR_ADM_SET_06_every_change_reset_and_revert_writes_history_and_audit_and_a_revert_restores_as_a_new_change()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var key = await SettingAsync();
        // The harness clock stands still; each change gets its own moment, as real changes do, so "newest first" is defined.
        await _h.PutAsync(admin, $"{Api}/{key}", Set("tenant", null, 20, "first"), Ct);
        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await _h.PutAsync(admin, $"{Api}/{key}", Set("tenant", null, 25, "second"), Ct);
        var row = second.Json.GetProperty("id").GetGuid();
        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        await _h.PostAsync(admin, $"{Api}/{key}/reset", new { layer = "tenant", reason = "third" }, Ct);

        var history = (await _h.GetAsync(admin, $"{Api}/{key}/history", Ct)).Json.EnumerateArray().ToList();
        Assert.Equal(["third", "second", "first"], history.Select(h => h.GetProperty("reason").GetString()));
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("newValue").ValueKind);
        Assert.Equal(25, history[0].GetProperty("oldValue").GetInt32());
        Assert.Equal(20, history[1].GetProperty("oldValue").GetInt32());
        Assert.All(history, h => Assert.Equal(admin, h.GetProperty("by").GetGuid()));

        // Revert "second": its old value 20 is written as a new change.
        var revert = await _h.PostAsync(admin, $"{Api}/history/{history[1].GetProperty("id").GetGuid()}/revert", new { reason = "undo" }, Ct);
        Assert.Equal(HttpStatusCode.OK, revert.Status);
        Assert.Equal("20", await _h.ScalarAsync<string>("select value::text from cfg.setting_values where setting_key = $1 and layer = 'tenant'", Ct, key));
        Assert.Equal(4L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_value_history where setting_key = $1", Ct, key));

        // Revert "first" (no old value): the layer's value is removed.
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"{Api}/history/{history[2].GetProperty("id").GetGuid()}/revert", new { reason = "undo all" }, Ct)).Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_values where setting_key = $1", Ct, key));

        Assert.Equal(3L, await _h.ScalarAsync<long>("select count(*) from cfg.outbox_messages where event_type = 'cfg.setting_changed' and payload ->> 'key' = $1", Ct, key));
        Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from cfg.outbox_messages where event_type = 'cfg.setting_reset' and payload ->> 'key' = $1", Ct, key));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from cfg.outbox_messages where payload ->> 'key' = $1 and payload ? 'reverted_from' and event_type = 'cfg.setting_changed'", Ct, key));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'cfg.setting_values' and entity_id = $1 and actor_user_id = $2", Ct, row, admin) >= 3);
        Assert.Equal(5L, await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type like 'cfg.setting_%' and details -> 'payload' ->> 'key' = $1", Ct, key));
        Assert.Equal(SettingsErrors.HistoryNotFound, (await _h.PostAsync(admin, $"{Api}/history/{Guid.CreateVersion7()}/revert", new { reason = "x" }, Ct)).Text("title"));
    }

    // ------------------------------------------------------------------ FR-ADM-SET-04: maker-checker

    [Fact]
    public async Task FR_ADM_SET_04_a_setting_that_needs_approval_changes_only_when_a_different_administrator_approves()
    {
        var maker = await _h.UserAsync(Ct, null, "tenant_admin");
        var checker = await _h.UserAsync(Ct, null, "tenant_admin");
        var key = await SettingAsync(approval: true);

        var held = await _h.PutAsync(maker, $"{Api}/{key}", Set("tenant", null, 42, "needs a second look"), Ct);
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        Assert.Equal("pending", held.Text("outcome"));
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_values where setting_key = $1", Ct, key));
        Assert.Equal(10, (await _h.GetAsync(maker, $"{Api}/{key}", Ct)).Json.GetProperty("setting").GetProperty("value").GetInt32());

        var own = await _h.PostAsync(maker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, own.Status);
        Assert.Equal("errors.maker_checker.own_request", own.Text("title"));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_values where setting_key = $1", Ct, key));

        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "fine" }, Ct)).Status);
        Assert.Equal(42, (await _h.GetAsync(maker, $"{Api}/{key}", Ct)).Json.GetProperty("setting").GetProperty("value").GetInt32());
        Assert.Equal("needs a second look", await _h.ScalarAsync<string>("select reason from cfg.setting_value_history where setting_key = $1", Ct, key));
        Assert.Equal(checker, await _h.ScalarAsync<Guid>("select created_by from cfg.setting_value_history where setting_key = $1", Ct, key));
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from iam.maker_checker_requests where id = $1", Ct, request));

        // A reset needs approval too.
        var reset = await _h.PostAsync(maker, $"{Api}/{key}/reset", new { layer = "tenant", reason = "back" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, reset.Status);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_values where setting_key = $1", Ct, key));
    }

    // ------------------------------------------------------------------ ruling Q16: permissions and roles

    [Fact]
    public async Task FR_ADM_SET_01_ruling_Q16_company_admin_changes_the_company_layer_of_its_own_companies_only_and_auditor_only_reads()
    {
        var key = await SettingAsync();
        var own = await _h.CompanyAsync(Ct);
        var other = await _h.CompanyAsync(Ct);
        var companyAdmin = await _h.UserAsync(Ct);
        await _h.AssignAsync(companyAdmin, await _h.RoleIdAsync("company_admin", Ct), own, _h.Today.AddDays(-1), null, Ct);
        var auditor = await _h.UserAsync(Ct, null, "auditor");
        var nobody = await _h.UserAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(companyAdmin, $"{Api}/{key}", Set("company", own, 33), Ct)).Status);
        var elsewhere = await _h.PutAsync(companyAdmin, $"{Api}/{key}", Set("company", other, 33), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.Status);
        Assert.Equal(SettingsErrors.CompanyNotCovered, elsewhere.Text("title"));
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(companyAdmin, $"{Api}/{key}", Set("tenant", null, 33), Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(companyAdmin, "/api/iam/settings/iam.session.idle_minutes", new { value = 40, reason = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(companyAdmin, $"{Api}/iam.session.idle_minutes", Set("tenant", null, 40), Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(companyAdmin, $"{Api}/{key}?companyId={own}", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(companyAdmin, $"{Api}/{key}?companyId={other}", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(companyAdmin, "/api/cfg/feature-flags", Ct)).Status);
        var history = (await _h.GetAsync(companyAdmin, $"{Api}/{key}/history", Ct)).Json.EnumerateArray().ToList();
        Assert.All(history, h => Assert.Equal(own, h.GetProperty("scopeId").GetGuid()));

        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(auditor, Api, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(auditor, $"{Api}/{key}/history", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(auditor, "/api/cfg/feature-flags", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(auditor, $"{Api}/{key}", Set("tenant", null, 33), Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, Api, Ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_SET_04_identity_settings_keep_the_S106_rules_through_the_settings_api()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        const string Key = "iam.session.idle_minutes";
        var set = await _h.PutAsync(admin, $"{Api}/{Key}", Set("tenant", null, 45, "longer"), Ct);
        Assert.Equal(HttpStatusCode.OK, set.Status);
        var row = set.Json.GetProperty("id").GetGuid();
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from iam.outbox_messages where event_type = 'iam.setting_changed' and aggregate_id = $1 and payload ->> 'reason' = 'longer'", Ct, row));
        Assert.Equal(45, (await _h.GetAsync(admin, $"{Api}/{Key}", Ct)).Json.GetProperty("setting").GetProperty("value").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"{Api}/{Key}/reset", new { layer = "tenant", reason = "default again" }, Ct)).Status);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from iam.outbox_messages where event_type = 'iam.setting_reset' and aggregate_id = $1", Ct, row));
        Assert.Equal("product", (await _h.GetAsync(admin, $"{Api}/{Key}", Ct)).Json.GetProperty("setting").GetProperty("source").GetString());
        Assert.Equal("errors.settings.layer_invalid", (await _h.PutAsync(admin, $"{Api}/{Key}", Set("user", admin, 30), Ct)).Text("title"));
    }

    // ------------------------------------------------------------------ FR-ADM-USR-10: self-service

    [Fact]
    public async Task FR_ADM_USR_10_a_user_sets_their_own_value_of_a_self_service_setting_and_nothing_else()
    {
        var user = await _h.UserAsync(Ct, null, "auditor");
        var other = await _h.UserAsync(Ct, null, "auditor");
        var mine = await SettingAsync(type: "text", @default: "\"en\"", validation: """{"choices":["en","ur"]}""", layers: ["tenant", "user"], selfService: true);
        var notMine = await SettingAsync(layers: ["tenant", "user"]);

        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(user, $"/api/cfg/me/settings/{mine}", new { value = "ur" }, Ct)).Status);
        var view = (await _h.GetAsync(user, $"/api/cfg/me/settings/{mine}", Ct)).Json.GetProperty("setting");
        Assert.Equal(("ur", "user"), (view.GetProperty("value").GetString(), view.GetProperty("source").GetString()));
        Assert.Equal(user, view.GetProperty("sourceScopeId").GetGuid());
        Assert.Equal("en", (await _h.GetAsync(other, $"/api/cfg/me/settings/{mine}", Ct)).Json.GetProperty("setting").GetProperty("value").GetString());
        Assert.Equal(SettingsErrors.ValueInvalid, (await _h.PutAsync(user, $"/api/cfg/me/settings/{mine}", new { value = "fr" }, Ct)).Text("title"));
        Assert.Equal(SettingsErrors.NotSelfService, (await _h.PutAsync(user, $"/api/cfg/me/settings/{notMine}", new { value = 5 }, Ct)).Text("title"));
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(user, $"/api/cfg/me/settings/{notMine}", Ct)).Status);
        Assert.Equal(SettingsErrors.NotSelfService, (await _h.PutAsync(user, "/api/cfg/me/settings/iam.mfa.required", new { value = false }, Ct)).Text("title"));
    }

    // ------------------------------------------------------------------ FR-KRN-SET-05: feature flags

    [Fact]
    public async Task FR_KRN_SET_05_a_flag_resolves_by_customer_company_and_user_group_and_an_unlicensed_entitlement_key_keeps_it_off()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var flag = "test.flag_" + Guid.NewGuid().ToString("N")[..8];
        var licensedFlag = flag + "_lic";
        var feature = "hrm.relations"; // a catalogue key the test customer is not licensed for (S1.09b: claims follow the key catalogue)
        await _h.ExecAsync("""
            insert into cfg.feature_flags (key, default_enabled, description_i18n, module_id, entitlement_key, name_i18n, help_i18n)
            values ($1, false, '{"en":"Test flag","ur":"آزمائشی فلیگ"}', 'test', null, '{"en":"T","ur":"ٹ"}', '{"en":"H","ur":"م"}'),
                   ($2, true, '{"en":"Test flag","ur":"آزمائشی فلیگ"}', 'test', $3, '{"en":"T","ur":"ٹ"}', '{"en":"H","ur":"م"}')
            """, Ct, flag, licensedFlag, feature);
        var group = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into iam.user_groups (id, code, name_i18n, is_active, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Pilot"}', true, now(), $3, now(), $3)
            """, Ct, group, "pilot" + Guid.NewGuid().ToString("N")[..6], BaselineSeed.SystemUserId);

        async Task<(bool On, string Source)> StateAsync(string key, Guid? companyId = null)
        {
            var list = (await _h.GetAsync(admin, "/api/cfg/feature-flags" + (companyId is null ? string.Empty : $"?companyId={companyId}"), Ct)).Json;
            var item = list.EnumerateArray().Single(f => f.GetProperty("key").GetString() == key);
            return (item.GetProperty("enabled").GetBoolean(), item.GetProperty("source").GetString()!);
        }

        object Flag(string layer, Guid? scope, bool on) => new { layer, scopeId = scope, enabled = on, reason = "pilot" };
        Assert.Equal((false, "default"), await StateAsync(flag));
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"/api/cfg/feature-flags/{flag}", Flag("tenant", null, true), Ct)).Status);
        Assert.Equal((true, "tenant"), await StateAsync(flag));
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"/api/cfg/feature-flags/{flag}", Flag("company", company, false), Ct)).Status);
        Assert.Equal((false, "company"), await StateAsync(flag, company));
        Assert.Equal((true, "tenant"), await StateAsync(flag));
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"/api/cfg/feature-flags/{flag}", Flag("user_group", group, true), Ct)).Status);
        Assert.Equal((false, "company"), await StateAsync(flag, company)); // the caller is not in the group yet
        await _h.ExecAsync("insert into iam.user_group_members (group_id, user_id) values ($1, $2)", Ct, group, admin);
        Assert.Equal((true, "user_group"), await StateAsync(flag, company));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, $"/api/cfg/feature-flags/{flag}/reset", new { layer = "user_group", scopeId = group, reason = "pilot over" }, Ct)).Status);
        Assert.Equal((false, "company"), await StateAsync(flag, company));
        Assert.Equal(SettingsErrors.FlagLayerInvalid, (await _h.PutAsync(admin, $"/api/cfg/feature-flags/{flag}", Flag("user", admin, true), Ct)).Text("title"));
        Assert.Equal(SettingsErrors.FlagUnknown, (await _h.PutAsync(admin, "/api/cfg/feature-flags/test.no_such_flag", Flag("tenant", null, true), Ct)).Text("title"));
        Assert.Equal(4L, await _h.ScalarAsync<long>("select count(*) from cfg.outbox_messages where event_type like 'cfg.feature_flag_%' and payload ->> 'key' = $1", Ct, flag));

        // Not licensed: off whatever the overrides say; licensed through the entitlement copy: on.
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"/api/cfg/feature-flags/{licensedFlag}", Flag("tenant", null, true), Ct)).Status);
        Assert.Equal((false, "entitlement"), await StateAsync(licensedFlag));
        // Slice S1.09b: the licensed features are the claims of the entitlement in force in the control plane.
        var original = await _h.Fixture.SetClaimAsync(_h.Code, "features", new JsonArray("platform.base", "hrm.core", "hrm.payroll", "hrm.performance", "hrm.learning", feature), Ct);
        try
        {
            Assert.Equal((true, "tenant"), await StateAsync(licensedFlag));
        }
        finally
        {
            await _h.Fixture.RestoreClaimsAsync(_h.Code, original, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ the generated writes are closed (CR-036 part 4, CR-037)

    [Fact]
    public async Task CR_036_CR_037_settings_values_history_and_flag_overrides_cannot_be_written_through_the_generated_api()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var key = await SettingAsync();
        await _h.PutAsync(admin, $"{Api}/{key}", Set("tenant", null, 20), Ct);
        var row = await _h.ScalarAsync<Guid>("select id from cfg.setting_values where setting_key = $1", Ct, key);
        var before = await _h.ScalarAsync<long>("select count(*) from cfg.setting_value_history where setting_key = $1", Ct, key);
        foreach (var (method, path) in new[]
        {
            ("POST", "/api/cfg/setting-values"), ("PUT", $"/api/cfg/setting-values/{row}"), ("POST", "/api/cfg/setting-value-history"),
            ("POST", "/api/cfg/feature-flag-overrides"),
        })
        {
            var body = new { settingKey = key, layer = "tenant", value = 99, oldValue = 1, newValue = 99, settingValueId = row, reason = "forged", rowVersion = 1 };
            var reply = method == "POST" ? await _h.PostAsync(admin, path, body, Ct) : await _h.PutAsync(admin, path, body, Ct);
            Assert.True(reply.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {path}: {(int)reply.Status} {reply.Body}");
        }

        Assert.Equal("20", await _h.ScalarAsync<string>("select value::text from cfg.setting_values where id = $1", Ct, row));
        Assert.Equal(before, await _h.ScalarAsync<long>("select count(*) from cfg.setting_value_history where setting_key = $1", Ct, key));
        // CR-061 (S1.09a review finding 2): the generated read of values is gone too; values are read through /api/cfg/settings.
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(admin, $"/api/cfg/setting-values/{row}", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(admin, $"{Api}/{key}", Ct)).Status);
    }
}
