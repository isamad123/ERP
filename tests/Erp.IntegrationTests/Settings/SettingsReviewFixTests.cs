#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Numbering;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;

namespace Erp.IntegrationTests.Settings;

/// <summary>
/// Fixes of the S1.09a fresh-session review (<c>docs/reviews/S1.09a-review.md</c>, product owner "accept all", CR-061):
/// the country default as the pack layer (finding 1), no generated read of setting values (2), no approval for
/// <c>iam.*</c> keys (3), the true old value under concurrent first writes (4), no stale snapshot after a change on the
/// same host (5, T3), company scope of the layered view (6) and of the history (7, T1), and the approval checks (T2).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class SettingsReviewFixTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/cfg/settings";
    private const string FiscalYear = "cfg.numbering.fiscal_year_start_month";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<string> SettingAsync(bool approval = false, string layers = "{tenant,company,role,user}")
    {
        var key = "test.s109r_" + Guid.NewGuid().ToString("N")[..10];
        await _h.ExecAsync("""
            insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n, is_visible, area, role_rule)
            values ($1, 'test', 'int', '10', '{"min":1,"max":1000}', $2::text[], $3, '{"en":"Review test","ur":"جائزہ"}', true, 'test', 'highest')
            """, Ct, key, layers, approval);
        return key;
    }

    private static object Set(string layer, Guid? scope, object? value, string reason = "review test") => new { layer, scopeId = scope, value, reason };

    private async Task<Guid> CompanyInAsync(string country)
    {
        var company = await _h.CompanyAsync(Ct);
        await _h.ExecAsync("update org.companies set country_code = $2 where id = $1", Ct, company, country);
        return company;
    }

    // ------------------------------------------------------------------ finding 1: country default = pack layer

    [Fact]
    public async Task FR_KRN_SET_02_CR_061_a_company_country_default_is_the_pack_layer_and_the_settings_centre_and_numbering_agree()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var pakistan = await CompanyInAsync("PK");
        var elsewhere = await CompanyInAsync("AE");
        await using var c = await _h.OwnerAsync(ct: Ct);
        var tenantValue = await _h.ScalarAsync<string?>("select value::text from cfg.setting_values where setting_key = $1 and layer = 'tenant'", Ct, FiscalYear);

        async Task<(int Value, string Source)> CentreAsync(Guid company)
        {
            var setting = (await _h.GetAsync(admin, $"{Api}/{FiscalYear}?companyId={company}", Ct)).Json.GetProperty("setting");
            return (setting.GetProperty("value").GetInt32(), setting.GetProperty("source").GetString()!);
        }

        if (tenantValue is null)
        {
            // Nothing set for the customer: the Pakistani company gets July from its country, shown as the pack layer.
            Assert.Equal((7, "pack"), await CentreAsync(pakistan));
            Assert.Equal(7, (await NumberingSettings.ReadAsync(c, null, pakistan, Ct)).FiscalYearStartMonth);
            Assert.Equal((1, "product"), await CentreAsync(elsewhere));
            Assert.Equal(1, (await NumberingSettings.ReadAsync(c, null, elsewhere, Ct)).FiscalYearStartMonth);
            var list = (await _h.GetAsync(admin, $"{Api}?search={FiscalYear}&companyId={pakistan}", Ct)).Json.EnumerateArray().Single();
            Assert.Equal(("pack", 7), (list.GetProperty("source").GetString(), list.GetProperty("value").GetInt32()));
            var pack = (await _h.GetAsync(admin, $"{Api}/{FiscalYear}?companyId={pakistan}", Ct)).Json.GetProperty("layers").EnumerateArray()
                .Single(l => l.GetProperty("layer").GetString() == "pack");
            Assert.Equal(JsonValueKind.Null, pack.GetProperty("ignored").ValueKind);
            Assert.Equal(JsonValueKind.Null, pack.GetProperty("changed").ValueKind); // computed: nobody set it
        }

        // A company value wins over the country default, in both readers.
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"{Api}/{FiscalYear}", Set("company", pakistan, 3), Ct)).Status);
        Assert.Equal((3, "company"), await CentreAsync(pakistan));
        Assert.Equal(3, (await NumberingSettings.ReadAsync(c, null, pakistan, Ct)).FiscalYearStartMonth);

        // Administrators still cannot write the pack layer.
        Assert.Equal(SettingsErrors.PackLayer, (await _h.PutAsync(admin, $"{Api}/{FiscalYear}", Set("pack", null, 4), Ct)).Text("title"));
    }

    [Fact]
    public async Task FR_KRN_SET_01_CR_061_every_country_default_is_a_valid_pack_value_of_a_registered_setting()
    {
        await using var c = await _h.OwnerAsync(ct: Ct);
        var definitions = await SettingsStore.DefinitionsAsync(c, null, [.. CountryPackDefaults.Keys], Ct);
        Assert.NotEmpty(CountryPackDefaults.Keys);
        foreach (var key in CountryPackDefaults.Keys)
        {
            Assert.True(definitions.TryGetValue(key, out var definition), key + " is not a registered setting");
            Assert.Contains(SettingLayers.Pack, definition.Layers);
            var value = CountryPackDefaults.ValueFor("PK", key);
            Assert.NotNull(value);
            Assert.Null(definition.Definition.Validation.Problem(definition.DataType, value.Value));
        }
    }

    // ------------------------------------------------------------------ finding 2: no generated read of values

    [Fact]
    public async Task FR_ADM_SET_01_CR_061_setting_values_are_read_only_through_the_settings_centre_which_applies_company_scope()
    {
        var key = await SettingAsync();
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var own = await _h.CompanyAsync(Ct);
        var other = await _h.CompanyAsync(Ct);
        var companyAdmin = await _h.UserAsync(Ct);
        await _h.AssignAsync(companyAdmin, await _h.RoleIdAsync("company_admin", Ct), own, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"{Api}/{key}", Set("company", other, 55), Ct)).Status);
        var row = await _h.ScalarAsync<Guid>("select id from cfg.setting_values where setting_key = $1", Ct, key);

        foreach (var user in new[] { companyAdmin, admin })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(user, $"/api/cfg/setting-values?settingKey={key}", Ct)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(user, $"/api/cfg/setting-values/{row}", Ct)).Status);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(companyAdmin, $"{Api}/{key}?companyId={other}", Ct)).Status);
        Assert.Equal(55, (await _h.GetAsync(admin, $"{Api}/{key}?companyId={other}", Ct)).Json.GetProperty("setting").GetProperty("value").GetInt32());
    }

    // ------------------------------------------------------------------ findings 3 and 8: approval and iam.* / self-service

    [Fact]
    public async Task FR_ADM_SET_04_review_finding_3_an_iam_setting_defined_as_needing_approval_is_refused_never_applied()
    {
        const string Key = "iam.session.idle_minutes";
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var before = await _h.ScalarAsync<string?>("select value::text from cfg.setting_values where setting_key = $1 and layer = 'tenant'", Ct, Key);
        await _h.ExecAsync("update cfg.settings set requires_approval = true where key = $1", Ct, Key);
        try
        {
            var answer = await _h.PutAsync(admin, $"{Api}/{Key}", Set("tenant", null, 44), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
            Assert.Equal(SettingsErrors.ApprovalNotSupported, answer.Text("title"));
            Assert.Equal(before, await _h.ScalarAsync<string?>("select value::text from cfg.setting_values where setting_key = $1 and layer = 'tenant'", Ct, Key));
        }
        finally
        {
            await _h.ExecAsync("update cfg.settings set requires_approval = false where key = $1", Ct, Key);
        }
    }

    [Fact]
    public async Task FR_ADM_USR_10_review_finding_8_a_setting_that_needs_approval_is_never_self_service()
    {
        var user = await _h.UserAsync(Ct, null, "auditor");
        var key = await SettingAsync(approval: true, layers: "{tenant,user}");
        await _h.ExecAsync("update cfg.settings set self_service = true where key = $1", Ct, key);
        var answer = await _h.PutAsync(user, $"/api/cfg/me/settings/{key}", new { value = 5 }, Ct);
        Assert.Equal(SettingsErrors.NotSelfService, answer.Text("title"));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from iam.maker_checker_requests where payload ->> 'settingKey' = $1", Ct, key));
    }

    // ------------------------------------------------------------------ finding 4: concurrent first writes

    [Fact]
    public async Task FR_KRN_SET_04_review_finding_4_two_first_changes_at_the_same_moment_record_the_true_old_value()
    {
        var key = await SettingAsync();
        var now = _h.Clock.UtcNow;
        var by = BaselineSeed.SystemUserId;
        await using var c1 = await _h.OwnerAsync(ct: Ct);
        await using var c2 = await _h.OwnerAsync(ct: Ct);
        await using var t1 = await c1.BeginTransactionAsync(Ct);
        await SettingWrites.ApplyAsync(c1, t1, key, "tenant", null, "11", "first", null, null, now, by, Ct);
        await using var t2 = await c2.BeginTransactionAsync(Ct);
        var second = SettingWrites.ApplyAsync(c2, t2, key, "tenant", null, "22", "second", null, null, now, by, Ct);
        await Task.Delay(300, Ct);
        Assert.False(second.IsCompleted, "the second writer must wait for the first");
        await t1.CommitAsync(Ct);
        await second;
        await t2.CommitAsync(Ct);

        var history = await _h.ScalarAsync<string>("""
            select string_agg(reason || ':' || coalesce(old_value::text, 'null') || '->' || new_value::text, ', ' order by reason)
            from cfg.setting_value_history where setting_key = $1
            """, Ct, key);
        Assert.Equal("first:null->11, second:11->22", history);
    }

    // ------------------------------------------------------------------ finding 5 and T3: the changing host's cache

    private async Task<(DbDataSourceHandle Source, string Tenant)> CacheSourceAsync()
    {
        await using var owner = await _h.OwnerAsync(ct: Ct);
        var builder = new NpgsqlConnectionStringBuilder(fx.AdminConnectionString) { Database = owner.Database };
        return (new DbDataSourceHandle(NpgsqlDataSource.Create(builder.ConnectionString)), "review-" + Guid.NewGuid().ToString("N")[..8]);
    }

    private static int? CachedTenantValue(SettingsSnapshot snapshot, string key) =>
        snapshot.Values[key].FirstOrDefault(v => v.Value.Layer == SettingLayers.Tenant)?.Value.Value.GetInt32();

    [Fact]
    public async Task FR_KRN_SET_06_ruling_Q13_the_host_that_made_a_change_sees_it_at_once_because_it_drops_its_snapshot()
    {
        var key = await SettingAsync();
        var (source, tenant) = await CacheSourceAsync();
        await using var _ = source;
        var cache = new SettingsCache(new SettingsCacheOptions(TimeSpan.FromHours(1)), TimeProvider.System);
        Assert.Null(CachedTenantValue(await cache.GetAsync(tenant, source.DataSource, Ct), key));

        await _h.ExecAsync("""
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'tenant', null, '21', now(), $3, now(), $3)
            """, Ct, Guid.CreateVersion7(), key, BaselineSeed.SystemUserId);
        Assert.Null(CachedTenantValue(await cache.GetAsync(tenant, source.DataSource, Ct), key)); // within the interval: cached
        cache.Invalidate(tenant);
        Assert.Equal(21, CachedTenantValue(await cache.GetAsync(tenant, source.DataSource, Ct), key));
    }

    [Fact]
    public async Task FR_KRN_SET_06_review_finding_5_a_load_that_started_before_a_change_does_not_put_the_old_values_back()
    {
        var key = await SettingAsync();
        var (source, tenant) = await CacheSourceAsync();
        await using var _ = source;
        var cache = new SettingsCache(new SettingsCacheOptions(TimeSpan.FromHours(1)), TimeProvider.System);
        await cache.GetAsync(tenant, source.DataSource, Ct);
        cache.Invalidate(tenant);

        // The writer holds the values table, so a load that starts now takes its snapshot and then waits for the writer.
        await using var writer = await _h.OwnerAsync(ct: Ct);
        await using var tx = await writer.BeginTransactionAsync(Ct);
        await AccessHarness.ExecAsync(writer, "lock table cfg.setting_values in access exclusive mode", Ct);
        var load = cache.GetAsync(tenant, source.DataSource, Ct);
        await Task.Delay(500, Ct);
        Assert.False(load.IsCompleted, "the load must be waiting for the writer");
        await AccessHarness.ExecAsync(writer, """
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'tenant', null, '31', now(), $3, now(), $3)
            """, Ct, Guid.CreateVersion7(), key, BaselineSeed.SystemUserId);
        await tx.CommitAsync(Ct);
        cache.Invalidate(tenant); // as the service does right after its commit

        Assert.Null(CachedTenantValue(await load, key)); // the load predates the change
        Assert.Equal(31, CachedTenantValue(await cache.GetAsync(tenant, source.DataSource, Ct), key)); // and was not kept
    }

    // ------------------------------------------------------------------ findings 6 and 7, T1: company scope of reads

    [Fact]
    public async Task FR_ADM_SET_03_review_finding_6_a_company_admin_cannot_read_a_users_or_roles_values_through_the_layered_view()
    {
        var key = await SettingAsync();
        var own = await _h.CompanyAsync(Ct);
        var companyAdmin = await _h.UserAsync(Ct);
        await _h.AssignAsync(companyAdmin, await _h.RoleIdAsync("company_admin", Ct), own, _h.Today.AddDays(-1), null, Ct);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var role = await _h.RoleIdAsync("auditor", Ct);

        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(companyAdmin, $"{Api}/{key}?companyId={own}", Ct)).Status);
        var byUser = await _h.GetAsync(companyAdmin, $"{Api}/{key}?companyId={own}&userId={admin}", Ct);
        Assert.Equal((HttpStatusCode.Forbidden, SettingsErrors.CompanyNotCovered), (byUser.Status, byUser.Text("title")));
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(companyAdmin, $"{Api}/{key}?companyId={own}&roleId={role}", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(admin, $"{Api}/{key}?companyId={own}&userId={companyAdmin}&roleId={role}", Ct)).Status);
    }

    [Fact]
    public async Task FR_ADM_SET_06_ruling_Q16_review_finding_7_a_company_admin_sees_its_own_history_rows_and_no_other_companys()
    {
        var key = await SettingAsync();
        var own = await _h.CompanyAsync(Ct);
        var other = await _h.CompanyAsync(Ct);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var companyAdmin = await _h.UserAsync(Ct);
        await _h.AssignAsync(companyAdmin, await _h.RoleIdAsync("company_admin", Ct), own, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(companyAdmin, $"{Api}/{key}", Set("company", own, 33, "own"), Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"{Api}/{key}", Set("company", other, 44, "other"), Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"{Api}/{key}", Set("tenant", null, 55, "customer"), Ct)).Status);

        // 510 newer changes of the other company, more than one page.
        await _h.ExecAsync("""
            insert into cfg.setting_value_history (id, setting_value_id, setting_key, layer, scope_id, old_value, new_value, reason, created_at, created_by)
            select gen_random_uuid(), gen_random_uuid(), $1, 'company', $2, '1', '2', 'bulk', now() + make_interval(secs => g), $3 from generate_series(1, 510) g
            """, Ct, key, other, admin);

        var mine = (await _h.GetAsync(companyAdmin, $"{Api}/{key}/history", Ct)).Json.EnumerateArray().ToList();
        var row = Assert.Single(mine);
        Assert.Equal((own, "own"), (row.GetProperty("scopeId").GetGuid(), row.GetProperty("reason").GetString()));
        Assert.Equal(500, (await _h.GetAsync(admin, $"{Api}/{key}/history", Ct)).Json.GetArrayLength());
    }

    // ------------------------------------------------------------------ T2: approval checks

    [Fact]
    public async Task FR_ADM_SET_04_FR_XM_32_a_held_change_needs_an_approver_covering_its_company_and_is_checked_again_when_approved()
    {
        var maker = await _h.UserAsync(Ct, null, "tenant_admin");
        var checker = await _h.UserAsync(Ct, null, "tenant_admin");
        var target = await _h.CompanyAsync(Ct);
        var elsewhere = await _h.CompanyAsync(Ct);
        var narrow = await _h.UserAsync(Ct);
        var approverRole = await _h.RoleAsync(Ct, ["iam.maker_checker.approve", SettingsPermissions.Update, SettingsPermissions.Read]);
        await _h.AssignAsync(narrow, approverRole, elsewhere, _h.Today.AddDays(-1), null, Ct);

        // An approver whose right covers another company only is refused.
        var key = await SettingAsync(approval: true);
        var held = await _h.PutAsync(maker, $"{Api}/{key}", Set("company", target, 42), Ct);
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(narrow, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "not mine to decide" }, Ct)).Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_values where setting_key = $1", Ct, key));

        // A held change is validated again when approved: the setting was hidden meanwhile.
        await _h.ExecAsync("update cfg.settings set is_visible = false where key = $1", Ct, key);
        var stale = await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "late" }, Ct);
        Assert.Equal((HttpStatusCode.BadRequest, SettingsErrors.Unknown), (stale.Status, stale.Text("title")));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_values where setting_key = $1", Ct, key));

        // A held reset, once approved, removes the value with its history row.
        var second = await SettingAsync(approval: true);
        var set = await _h.PutAsync(maker, $"{Api}/{second}", Set("tenant", null, 42), Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{set.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "ok" }, Ct)).Status);
        var reset = await _h.PostAsync(maker, $"{Api}/{second}/reset", new { layer = "tenant", reason = "back" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, reset.Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{reset.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "ok" }, Ct)).Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.setting_values where setting_key = $1", Ct, second));
        Assert.Equal("42->null", await _h.ScalarAsync<string>("select old_value::text || '->' || coalesce(new_value::text, 'null') from cfg.setting_value_history where setting_key = $1 and reason = 'back'", Ct, second));
    }

    /// <summary>A data source the test disposes.</summary>
    private sealed class DbDataSourceHandle(NpgsqlDataSource dataSource) : IAsyncDisposable
    {
        public NpgsqlDataSource DataSource { get; } = dataSource;

        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }
}
