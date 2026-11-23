using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;

namespace Erp.IntegrationTests.Audit;

/// <summary>
/// The findings of the fresh-session review of slice S1.07 (docs/reviews/S1.07-review.md), each reproduced through the
/// real API, the real access engine and real customer databases. Before the fixes each of these tests failed.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AuditReviewFixTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ finding 1

    [Fact]
    public async Task FR_KRN_AUD_04_GHR_06_review_S107_1_change_search_shows_sensitive_values_only_through_roles_that_see_every_record()
    {
        // A salary change and a personal value (date of birth) in the change log.
        var company = await _h.DefaultCompanyAsync(Ct);
        var id = (Guid)(await _h.RowAsync("hrm.pay_results", company, null, Ct))["id"]!;
        var editor = await _h.UserAsync(Ct);
        await _h.AssignAsync(editor, await _h.RoleAsync(Ct, ["hrm.pay_results.read", "hrm.pay_results.update", "hrm.persons.create", "hrm.persons.read"],
            [("hrm.pay_results", "all"), ("hrm.persons", "all")], [("hrm.pay_results", "class:salary", "editable")]), null, _h.Today.AddDays(-1), null, Ct);
        var current = JsonNode.Parse((await _h.GetAsync(editor, $"/api/hrm/pay-results/{id}", Ct)).Body)!.AsObject();
        var newGross = current["gross"]!.GetValue<decimal>() + 1234.5m;
        current["gross"] = newGross;
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(editor, $"/api/hrm/pay-results/{id}", current.ToJsonString(), Ct)).Status);
        var person = await _h.PostAsync(editor, "/api/hrm/persons", new { personNo = "R" + Guid.NewGuid().ToString("N")[..8], firstName = "Review", gender = "female", dateOfBirth = "1977-03-29" }, Ct);
        Assert.Equal(HttpStatusCode.Created, person.Status);
        var personId = person.Json.GetProperty("id").GetGuid();

        // A Tenant Admin who is also an employee: the Employee role opens salary and personal fields of the employee's OWN
        // records only; it must not open them on every record in the change search or the export.
        var adminEmployee = await _h.UserAsync(Ct, null, "tenant_admin", "employee");
        var pay = await _h.GetAsync(adminEmployee, $"/api/aud/changes?entityId={id}&action=update", Ct);
        Assert.Equal(HttpStatusCode.OK, pay.Status);
        Assert.True(Field(pay.Json.GetProperty("items")[0], "gross").TryGetProperty("hidden", out var hidden) && hidden.GetBoolean(), pay.Body);
        Assert.DoesNotContain(newGross.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), pay.Body, StringComparison.Ordinal);
        var who = await _h.GetAsync(adminEmployee, $"/api/aud/changes?entityId={personId}", Ct);
        Assert.Equal(HttpStatusCode.OK, who.Status);
        Assert.DoesNotContain("1977-03-29", who.Body, StringComparison.Ordinal);
        var export = await _h.GetAsync(adminEmployee, $"/api/aud/export?entityId={id}", Ct);
        Assert.Equal(HttpStatusCode.OK, export.Status);
        Assert.DoesNotContain(newGross.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), export.Body, StringComparison.Ordinal);

        // Control: the Auditor sees every record with salary read-only, so the search shows the value.
        var auditor = await _h.UserAsync(Ct, null, "auditor");
        var audited = await _h.GetAsync(auditor, $"/api/aud/changes?entityId={id}&action=update", Ct);
        Assert.Equal(newGross, Field(audited.Json.GetProperty("items")[0], "gross").GetProperty("after").GetDecimal());
    }

    // ------------------------------------------------------------------ finding 2

    [Fact]
    public async Task FR_KRN_AUD_03_S107_P5_review_S107_2_a_day_is_copied_to_the_control_plane_only_when_no_entry_can_still_land_on_it()
    {
        var code = await _h.ProvisionAsync("rp", Ct);
        var admin = await _h.UserAsync(Ct, code, "tenant_admin");
        var now = _h.Clock.UtcNow;
        await AppendAsync(code, now);

        // The copy runs "after midnight" (its clock is a day ahead) while the chain head is still on today: an entry that
        // was stamped before midnight (a clock a little behind, a transaction waiting for the chain lock) still lands today.
        var lateClock = new TestClock();
        lateClock.Advance(TimeSpan.FromDays(1));
        var publisher = new AnchorPublisher(fx.Options, fx.Secrets, lateClock, fx.Migrations);
        await publisher.PublishAsync(fx.Admin, code, Ct);
        await AppendAsync(code, now.AddSeconds(1));

        var again = await publisher.PublishAsync(fx.Admin, code, Ct);
        Assert.Empty(again.Single().Mismatches);
        var report = await _h.PostAsync(admin, "/api/aud/verify", new { from = _h.Today, to = _h.Today }, Ct, code);
        Assert.True(report.Json.GetProperty("intact").GetBoolean(), report.Body);

        // Once the chain has moved on to a later day, the earlier day is final and is copied.
        await AppendAsync(code, now.AddDays(1).AddMinutes(-4), entityType: "org.branches");
        var later = new AnchorPublisher(fx.Options, fx.Secrets, Later(TimeSpan.FromDays(2)), fx.Migrations);
        var copied = await later.PublishAsync(fx.Admin, code, Ct);
        Assert.True(copied.Single().Copied >= 1, JsonSerializer.Serialize(copied));
        Assert.Empty(copied.Single().Mismatches);
    }

    // ------------------------------------------------------------------ finding 3

    [Fact]
    public async Task FR_KRN_MET_02_FR_KRN_AUD_07_review_S107_3_record_history_applies_the_custom_field_rules()
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        var id = (Guid)(await _h.RowAsync("org.org_units", company, null, Ct))["id"]!;
        await _h.ExecAsync("update org.org_units set custom = '{\"secret\":\"hush-4711\",\"note\":\"n1\"}' where id = $1", Ct, id);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        // Custom values are checked against their definitions since S1.15b: the two fields the record carries are defined first.
        foreach (var key in new[] { "secret", "note" })
        {
            Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(admin, "/api/cfg/custom-fields", new { entityType = "org.org_units", fieldKey = key, dataType = "text", label = new { en = key } }, Ct)).Status);
        }

        var unit = JsonNode.Parse((await _h.GetAsync(admin, $"/api/org/org-units/{id}", Ct)).Body)!.AsObject();
        unit["custom"] = new JsonObject { ["secret"] = "hush-4711", ["note"] = "note-0042" };
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(admin, $"/api/org/org-units/{id}", unit.ToJsonString(), Ct)).Status);

        var viewer = await _h.UserAsync(Ct);
        await _h.AssignAsync(viewer, await _h.RoleAsync(Ct, ["org.org_units.read", "aud.history.view"], [("org.org_units", "all")],
            [("org.org_units", "custom.secret", "hidden"), ("org.org_units", "custom.note", "masked")]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.False(JsonNode.Parse((await _h.GetAsync(viewer, $"/api/org/org-units/{id}", Ct)).Body)!["custom"]!.AsObject().ContainsKey("secret"));

        var history = await _h.GetAsync(viewer, $"/api/aud/history/org.org_units/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, history.Status);
        Assert.DoesNotContain("hush-4711", history.Body, StringComparison.Ordinal);
        var custom = Field(history.Json[0], "custom");
        Assert.Equal("****0042", custom.GetProperty("after").GetProperty("note").GetString());
        Assert.Equal("note-0042", Field((await _h.GetAsync(admin, $"/api/aud/history/org.org_units/{id}", Ct)).Json[0], "custom").GetProperty("after").GetProperty("note").GetString());
        await _h.ExecAsync("delete from cfg.custom_field_defs where entity_type = 'org.org_units' and field_key in ('secret', 'note')", Ct);
    }

    // ------------------------------------------------------------------ finding 4

    [Fact]
    public async Task FR_KRN_AUD_04_GHR_06_review_S107_4_a_classed_value_of_a_table_without_its_own_api_is_hidden_in_search_and_history()
    {
        var entity = Guid.CreateVersion7();
        await using (var rw = await fx.OpenTenantAsync(_h.Code, TenantLogins.ReadWrite, Ct))
        {
            var change = new JsonObject { ["field"] = "amount", ["class"] = "salary", ["key"] = "customer", ["after"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(40)) };
            await AuditDb.AppendChangesAsync(rw, null, [new ChangeEntry(Guid.CreateVersion7(), _h.Clock.UtcNow, null, "system", "hrm.pay_traces", entity, null, "create", [change], null, null, null, "system", BaselineSeed.SystemUserId)], Ct);
        }

        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var search = await _h.GetAsync(admin, $"/api/aud/changes?entityId={entity}", Ct);
        Assert.Equal(HttpStatusCode.OK, search.Status);
        Assert.True(Field(search.Json.GetProperty("items")[0], "amount").GetProperty("hidden").GetBoolean(), search.Body);
        var history = await _h.GetAsync(admin, $"/api/aud/history/hrm.pay_traces/{entity}", Ct);
        Assert.Equal(HttpStatusCode.OK, history.Status);
        Assert.True(Field(history.Json[0], "amount").GetProperty("hidden").GetBoolean(), history.Body);
    }

    // ------------------------------------------------------------------ finding 5

    [Fact]
    public async Task Spec03_R21_review_S107_5_a_personal_value_whose_person_is_unknown_is_never_kept_under_the_customer_key()
    {
        await using var rw = await fx.OpenTenantAsync(_h.Code, TenantLogins.ReadWrite, Ct);
        var protector = new AuditValueProtector(rw, null, new AesGcmFieldEncryptor(RandomNumberGenerator.GetBytes(32), 1));
        var change = await protector.FieldAsync("hrm.employee_bank_accounts", "account_number", "personal", redacted: false,
            JsonValue.Create("PK00ABCD0000001234567890"), JsonValue.Create("PK00ABCD0000009876543210"), create: false, delete: false, subject: null, Ct);

        // Erasure works only through the person's key (R-21): without a known person the value is not stored at all.
        Assert.True(change["redacted"]?.GetValue<bool>() ?? false, change.ToJsonString());
        Assert.False(change.ContainsKey("before"));
        Assert.False(change.ContainsKey("after"));
    }

    // ------------------------------------------------------------------ finding 6 (CR-034)

    [Fact]
    public void PAY_32_review_S107_6_CR034_the_payroll_calculation_trace_is_not_written_to_the_change_log()
    {
        // The trace holds salary inputs in plain text; the pay result's own entries record the change (product owner, 2026-11-17).
        var trace = Erp.Hrm.Infrastructure.Generated.HrmAuditCatalog.All.Single(t => t.Table == "hrm.pay_traces");
        Assert.False(trace.Audited);
        Assert.Contains("CR-034", trace.Reason, StringComparison.Ordinal);
        Assert.True(Erp.Hrm.Infrastructure.Generated.HrmAuditCatalog.All.Single(t => t.Table == "hrm.pay_results").Audited);
    }

    // ------------------------------------------------------------------ helpers

    private async Task AppendAsync(string code, DateTimeOffset at, string entityType = "org.companies")
    {
        await using var rw = await fx.OpenTenantAsync(code, TenantLogins.ReadWrite, Ct);
        await AuditDb.AppendChangesAsync(rw, null, [new ChangeEntry(Guid.CreateVersion7(at), at, null, "system", entityType, Guid.CreateVersion7(at), null, "update",
            [new JsonObject { ["field"] = "code", ["after"] = "v" + at.Ticks }], null, null, null, "system", BaselineSeed.SystemUserId)], Ct);
    }

    private static TestClock Later(TimeSpan by)
    {
        var clock = new TestClock();
        clock.Advance(by);
        return clock;
    }

    private static JsonElement Field(JsonElement item, string field) =>
        item.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("field").GetString() == field);
}
