using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Setup;

namespace Erp.IntegrationTests.Setup;

/// <summary>
/// The readiness checklist, the wizard steps and go-live (slice S1.18a, CR-074; FR-ADM-ONB-01, -02, -03; ruling Q4), through the real API
/// pipeline, the real access engine, the real kernel services and a real PostgreSQL.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class ReadinessTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Readiness = "/api/cfg/readiness";
    private AccessHarness _h = null!;
    private Guid _admin;
    private Guid _viewer;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        // A role of its own: another test removes a permission from the baseline tenant administrator role (AccessGovernanceTests).
        var admin = await _h.RoleAsync(Ct, [
            "iam.self.manage", "cfg.setup_tasks.read", "cfg.setup_tasks.update", "cfg.setup_tasks.go_live", "org.companies.read", "cfg.numbering_schemes.read",
            "cfg.numbering_schemes.create"],
            [("org.companies", "all"), ("cfg.numbering_schemes", "all"), ("cfg.setup_tasks", "all")]);
        var viewer = await _h.RoleAsync(Ct, ["iam.self.manage", "cfg.setup_tasks.read", "org.companies.read"], [("org.companies", "all"), ("cfg.setup_tasks", "all")]);
        _admin = await _h.UserAsync(Ct);
        _viewer = await _h.UserAsync(Ct);
        await _h.AssignAsync(_admin, admin, null, _h.Today.AddDays(-1), null, Ct);
        await _h.AssignAsync(_viewer, viewer, null, _h.Today.AddDays(-1), null, Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void Expect(HttpStatusCode expected, Answer answer) =>
        Assert.True(answer.Status == expected, $"expected {expected}, got {answer.Status}: {answer.Body}");

    private async Task<JsonElement> OverviewAsync(Guid? company, Guid? user = null)
    {
        var answer = await _h.GetAsync(user ?? _admin, Readiness + (company is null ? string.Empty : "?companyId=" + company), Ct);
        Expect(HttpStatusCode.OK, answer);
        return answer.Json;
    }

    private static JsonElement Item(JsonElement overview, string key) =>
        overview.GetProperty("modules").EnumerateArray().SelectMany(m => m.GetProperty("items").EnumerateArray()).First(i => i.GetProperty("taskKey").GetString() == key);

    private static JsonElement Step(JsonElement overview, string key) =>
        overview.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("taskKey").GetString() == key);

    private static JsonElement Module(JsonElement overview, string id) =>
        overview.GetProperty("modules").EnumerateArray().First(m => m.GetProperty("moduleId").GetString() == id);

    /// <summary>Gives the company an active scheme for every numbered document type, through the numbering API (what the screen does).</summary>
    private async Task FixNumberingAsync(Guid company)
    {
        var types = await _h.GetAsync(_admin, "/api/cfg/numbering/document-types?companyId=" + company, Ct);
        Expect(HttpStatusCode.OK, types);
        foreach (var type in types.Json.EnumerateArray().Where(t => t.GetProperty("hasActiveScheme").ValueKind != JsonValueKind.True))
        {
            var created = await _h.PostAsync(_admin, "/api/cfg/numbering/schemes", new
            {
                companyId = type.GetProperty("tenantWide").GetBoolean() ? (Guid?)null : company,
                documentType = type.GetProperty("code").GetString(),
                pattern = type.GetProperty("defaultPattern").GetString(),
                resetRule = type.GetProperty("defaultReset").GetString(),
            }, Ct);
            Expect(HttpStatusCode.Created, created);
        }
    }

    [Fact]
    public async Task FR_ADM_ONB_03_checklist_lists_kernel_items_with_status_links_and_a_message()
    {
        var company = await _h.CompanyAsync(Ct);
        var overview = await OverviewAsync(company);
        var kernel = Module(overview, "kernel");
        Assert.True(kernel.GetProperty("requiredTotal").GetInt32() >= 4);
        var location = Item(overview, "location");
        Assert.Equal("pending", location.GetProperty("status").GetString());
        Assert.Equal("/organization/locations", location.GetProperty("link").GetString());
        Assert.Equal("setup.messages.location_missing", location.GetProperty("messageKey").GetString());
        Assert.True(location.GetProperty("required").GetBoolean());
        Assert.False(Item(overview, "holiday_calendar").GetProperty("required").GetBoolean());
        Assert.True(Item(overview, "notification_rules").GetProperty("manual").GetBoolean());
        Assert.Equal(JsonValueKind.Null, kernel.GetProperty("liveAt").ValueKind);
    }

    [Fact]
    public async Task FR_ADM_ONB_03_numbering_item_is_open_for_a_type_without_a_scheme_and_done_after_the_fix()
    {
        var company = await _h.CompanyAsync(Ct);
        var before = Item(await OverviewAsync(company), "numbering");
        Assert.Equal("pending", before.GetProperty("status").GetString());
        Assert.Equal("/settings/numbering", before.GetProperty("link").GetString());
        Assert.Equal("setup.messages.numbering_missing", before.GetProperty("messageKey").GetString());
        Assert.True(int.Parse(before.GetProperty("messageArguments").GetProperty("count").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 0);

        await FixNumberingAsync(company);
        var after = Item(await OverviewAsync(company), "numbering");
        Assert.Equal("done", after.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("messageKey").ValueKind);
    }

    [Fact]
    public async Task FR_ADM_ONB_03_location_and_unit_items_follow_the_data()
    {
        var company = await _h.CompanyAsync(Ct);
        Assert.Equal("pending", Item(await OverviewAsync(company), "unit_structure").GetProperty("status").GetString());
        var n = Guid.NewGuid().ToString("N")[..8];
        var type = Guid.CreateVersion7();
        await _h.ExecAsync("insert into org.org_unit_types (id, code, name_i18n, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"Department\"}', now(), $3, now(), $3)", Ct, type, "T" + n, BaselineSeed.SystemUserId);
        var unit = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into org.org_units (id, company_id, code, name_i18n, unit_type_id, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, '{"en":"Finance"}', $4, now(), $5, now(), $5)
            """, Ct, unit, company, "U" + n, type, BaselineSeed.SystemUserId);
        var overview = await OverviewAsync(company);
        Assert.Equal("done", Item(overview, "unit_structure").GetProperty("status").GetString());
        Assert.Equal("done", Step(overview, "step_structure").GetProperty("status").GetString());
    }

    [Fact]
    public async Task FR_ADM_ONB_02_steps_can_be_done_skipped_and_reopened_and_progress_survives_a_new_sign_in()
    {
        var company = await _h.CompanyAsync(Ct);
        await OverviewAsync(company);
        Expect(HttpStatusCode.OK, await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/step_languages_formats/complete", new { companyId = company }, Ct));
        var skipped = await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/step_modules/skip", new { note = "later" }, Ct);
        Expect(HttpStatusCode.OK, skipped);
        Assert.Equal("skipped", skipped.Json.GetProperty("status").GetString());

        // "Closes the browser, signs in again": a new overview from another user shows the same progress.
        var again = await OverviewAsync(company, _admin);
        Assert.Equal("done", Step(again, "step_languages_formats").GetProperty("status").GetString());
        Assert.Equal("skipped", Step(again, "step_modules").GetProperty("status").GetString());
        Assert.Equal("later", Step(again, "step_modules").GetProperty("note").GetString());
        Assert.Equal("pending", Step(again, "step_users").GetProperty("status").GetString());

        var reopened = await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/step_modules/reopen", new { }, Ct);
        Expect(HttpStatusCode.OK, reopened);
        Assert.Equal("pending", reopened.Json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reopened.Json.GetProperty("completedAt").ValueKind);
    }

    [Fact]
    public async Task FR_ADM_ONB_02_a_step_for_a_company_needs_the_company_and_a_customer_step_refuses_one()
    {
        var company = await _h.CompanyAsync(Ct);
        var missing = await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/step_languages_formats/complete", new { }, Ct);
        Expect(HttpStatusCode.BadRequest, missing);
        Assert.Equal("errors.setup.company_required", missing.Json.GetProperty("errors").GetProperty("companyId")[0].GetString());
        Expect(HttpStatusCode.BadRequest, await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/step_users/complete", new { companyId = company }, Ct));
        Expect(HttpStatusCode.NotFound, await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/nonsense/complete", new { }, Ct));
        Expect(HttpStatusCode.NotFound, await _h.PostAsync(_admin, $"{Readiness}/nomodule/tasks/x/complete", new { }, Ct));
        Expect(HttpStatusCode.NotFound, await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/step_languages_formats/complete", new { companyId = Guid.NewGuid() }, Ct));
    }

    [Fact]
    public async Task FR_ADM_ONB_03_an_item_the_system_decides_cannot_be_ticked_and_an_item_cannot_be_skipped()
    {
        var company = await _h.CompanyAsync(Ct);
        var tick = await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/location/complete", new { companyId = company }, Ct);
        Expect(HttpStatusCode.Conflict, tick);
        Assert.Equal("errors.setup.decided_by_check", tick.Json.GetProperty("title").GetString());
        var skip = await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/notification_rules/skip", new { }, Ct);
        Expect(HttpStatusCode.BadRequest, skip);
        Assert.Equal("errors.setup.only_steps_skip", skip.Json.GetProperty("title").GetString());

        // A manual item is ticked by a person and shows who and when.
        var done = await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/notification_rules/complete", new { }, Ct);
        Expect(HttpStatusCode.OK, done);
        Assert.Equal("done", done.Json.GetProperty("status").GetString());
        Assert.Equal(_admin, done.Json.GetProperty("completedBy").GetGuid());
    }

    [Fact]
    public async Task FR_ADM_ONB_03_go_live_is_refused_without_a_reason_while_required_items_are_open()
    {
        var company = await _h.CompanyAsync(Ct);
        var refused = await _h.PostAsync(_admin, $"{Readiness}/kernel/go-live", new { companyId = company }, Ct);
        Expect(HttpStatusCode.BadRequest, refused);
        Assert.Equal("errors.setup.reason_required", refused.Json.GetProperty("errors").GetProperty("overrideReason")[0].GetString());
        var blank = await _h.PostAsync(_admin, $"{Readiness}/kernel/go-live", new { companyId = company, overrideReason = "   " }, Ct);
        Expect(HttpStatusCode.BadRequest, blank);
        Assert.False(Module(await OverviewAsync(company), "kernel").GetProperty("isLive").GetBoolean());
    }

    [Fact]
    public async Task FR_ADM_ONB_03_go_live_with_a_reason_is_recorded_and_audited_and_a_second_company_has_its_own()
    {
        var first = await _h.CompanyAsync(Ct);
        var second = await _h.CompanyAsync(Ct);
        var live = await _h.PostAsync(_admin, $"{Readiness}/kernel/go-live", new { companyId = first, overrideReason = "Pilot with the head office only" }, Ct);
        Expect(HttpStatusCode.OK, live);
        Assert.True(live.Json.GetProperty("overridden").GetBoolean());
        Assert.True(live.Json.GetProperty("openRequired").GetInt32() > 0);

        var kernel = Module(await OverviewAsync(first), "kernel");
        Assert.True(kernel.GetProperty("isLive").GetBoolean());
        Assert.Equal("Pilot with the head office only", kernel.GetProperty("liveNote").GetString());
        Assert.False(Module(await OverviewAsync(second), "kernel").GetProperty("isLive").GetBoolean());

        // The override and its reason are in the audit log (every tick, skip and override is audited).
        var changes = await _h.ScalarAsync<string>("select string_agg(changes::text, ' ') from aud.change_log where entity_type = 'cfg.setup_tasks' and company_id = $1", Ct, first);
        Assert.Contains("Pilot with the head office only", changes, StringComparison.Ordinal);
        Assert.Contains("go_live", changes, StringComparison.Ordinal);

        var again = await _h.PostAsync(_admin, $"{Readiness}/kernel/go-live", new { companyId = first, overrideReason = "again" }, Ct);
        Expect(HttpStatusCode.Conflict, again);
        Assert.Equal("errors.setup.already_live", again.Json.GetProperty("title").GetString());
        Expect(HttpStatusCode.OK, await _h.PostAsync(_admin, $"{Readiness}/kernel/go-live", new { companyId = second, overrideReason = "Second company, same reason" }, Ct));
    }

    [Fact]
    public async Task FR_ADM_ONB_03_go_live_needs_no_reason_when_every_required_item_is_done()
    {
        var company = await _h.CompanyAsync(Ct);
        await FixNumberingAsync(company);
        await DoneProfileAndLocationAsync(company);
        var overview = await OverviewAsync(company);
        var kernel = Module(overview, "kernel");
        Assert.Equal(kernel.GetProperty("requiredTotal").GetInt32(), kernel.GetProperty("requiredDone").GetInt32());
        var live = await _h.PostAsync(_admin, $"{Readiness}/kernel/go-live", new { companyId = company }, Ct);
        Expect(HttpStatusCode.OK, live);
        Assert.False(live.Json.GetProperty("overridden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Module(await OverviewAsync(company), "kernel").GetProperty("liveNote").ValueKind);
    }

    private async Task DoneProfileAndLocationAsync(Guid company)
    {
        await _h.ExecAsync("""
            insert into org.company_registrations (id, company_id, kind, value, value_hmac, authority, valid_from, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'ntn', '\x00', '\x01', 'FBR', '2020-01-01', now(), $3, now(), $3)
            """, Ct, Guid.CreateVersion7(), company, BaselineSeed.SystemUserId);
        var branch = await _h.ScalarAsync<Guid>("select id from org.branches where company_id = $1 order by created_at limit 1", Ct, company);
        await _h.ExecAsync("""
            insert into org.locations (id, company_id, code, name_i18n, branch_id, address, region_code, time_zone, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, '{"en":"Head office"}', $4, '{}', 'PB', 'Asia/Karachi', now(), $5, now(), $5)
            """, Ct, Guid.CreateVersion7(), company, "L" + Guid.NewGuid().ToString("N")[..8], branch, BaselineSeed.SystemUserId);
        // Required items all done except the approval routes and the customer-level ones, which the baseline provides.
        var overview = await OverviewAsync(company);
        Assert.Equal("done", Item(overview, "company_profile").GetProperty("status").GetString());
        Assert.Equal("done", Item(overview, "location").GetProperty("status").GetString());
    }

    [Fact]
    public async Task FR_ADM_ONB_03_permissions_are_checked_on_every_readiness_endpoint()
    {
        var company = await _h.CompanyAsync(Ct);
        var nobody = await _h.UserAsync(Ct);
        Expect(HttpStatusCode.Forbidden, await _h.GetAsync(nobody, Readiness, Ct));
        Expect(HttpStatusCode.OK, await _h.GetAsync(_viewer, $"{Readiness}?companyId={company}", Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(_viewer, $"{Readiness}/kernel/tasks/notification_rules/complete", new { }, Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(_viewer, $"{Readiness}/kernel/tasks/step_modules/skip", new { }, Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(_viewer, $"{Readiness}/kernel/tasks/step_modules/reopen", new { }, Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(_viewer, $"{Readiness}/kernel/go-live", new { companyId = company, overrideReason = "x" }, Ct));
    }

    [Fact]
    public async Task FR_ADM_ONB_01_set_up_rows_are_seeded_idempotently_and_keep_a_persons_progress()
    {
        var company = await _h.CompanyAsync(Ct);
        await OverviewAsync(company);
        Expect(HttpStatusCode.OK, await _h.PostAsync(_admin, $"{Readiness}/kernel/tasks/step_modules/skip", new { note = "keep" }, Ct));
        await using var owner = await _h.OwnerAsync(null, Ct);
        var modules = new[] { SetupManifest.Parse(SetupManifest.KernelJson) };
        await SetupTaskSetup.EnsureAsync(owner, null, modules, DateTimeOffset.UtcNow, Ct);
        var second = await SetupTaskSetup.EnsureAsync(owner, null, modules, DateTimeOffset.UtcNow, Ct);
        Assert.Equal(0, second);
        var status = await AccessHarness.ScalarAsync<string>(owner, "select status || ':' || coalesce(note, '') from cfg.setup_tasks where module_id = 'kernel' and task_key = 'step_modules' and company_id is null", Ct);
        Assert.Equal("skipped:keep", status);
        var perCompany = await AccessHarness.ScalarAsync<long>(owner, "select count(*) from cfg.setup_tasks where module_id = 'kernel' and company_id = $1", Ct, company);
        Assert.Equal(modules[0].Tasks.Count(t => t.CompanyLevel), perCompany);
    }
}
