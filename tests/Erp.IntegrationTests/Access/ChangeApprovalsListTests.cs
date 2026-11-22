#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Settings;

namespace Erp.IntegrationTests.Access;

/// <summary>
/// "Approvals of changes" in the inbox (slice S1.12d Build item 22, ruling Q15): <c>GET /api/iam/maker-checker-requests/pending</c>
/// through the real API pipeline, the real access engine and a real PostgreSQL. Pending requests of others are described in
/// plain words with whether approving would pass now (agreeing with the approval endpoint); the person's own requests are
/// listed but never decidable by them; expired requests are not offered (FR-KRN-IAM-14, FR-ADM-SET-04, FR-XM-32).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class ChangeApprovalsListTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Pending = "/api/iam/maker-checker-requests/pending";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync()
    {
        _h.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private async Task<string> HeldSettingAsync()
    {
        var key = "test.s112d_" + Guid.NewGuid().ToString("N")[..10];
        await _h.ExecAsync("""
            insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n, name_i18n, is_visible, area, role_rule)
            values ($1, 'test', 'int', '10', '{"min":1,"max":1000}', '{tenant,company}', true, '{"en":"Approval test","ur":"منظوری"}', '{"en":"Approval test","ur":"منظوری ٹیسٹ"}', true, 'test', 'highest')
            """, Ct, key);
        return key;
    }

    private async Task<JsonElement?> FindAsync(Guid user, string list, Guid request)
    {
        var answer = await _h.GetAsync(user, Pending, Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json.GetProperty(list).EnumerateArray().Cast<JsonElement?>().FirstOrDefault(r => r!.Value.GetProperty("id").GetGuid() == request);
    }

    [Fact]
    public async Task FR_KRN_IAM_14_FR_XM_32_S112d_pending_changes_are_described_offered_only_where_approval_would_pass_and_never_to_the_requester()
    {
        var maker = await _h.UserAsync(Ct, null, "tenant_admin");
        var checker = await _h.UserAsync(Ct, null, "tenant_admin");
        var target = await _h.CompanyAsync(Ct);
        var elsewhere = await _h.CompanyAsync(Ct);
        var narrow = await _h.UserAsync(Ct);
        var approverRole = await _h.RoleAsync(Ct, ["iam.maker_checker.approve", SettingsPermissions.Update, SettingsPermissions.Read]);
        await _h.AssignAsync(narrow, approverRole, elsewhere, _h.Today.AddDays(-1), null, Ct);

        var key = await HeldSettingAsync();
        var held = await _h.PutAsync(maker, $"/api/cfg/settings/{key}", new { layer = "company", scopeId = target, value = 42, reason = "month end" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        var request = held.Json.GetProperty("requestId").GetGuid();

        // The second administrator: what will change in plain words, who asked and when, and that approving would pass.
        var item = await FindAsync(checker, "toDecide", request) ?? throw new InvalidOperationException("not offered to the checker");
        Assert.Equal("cfg.setting.change", item.GetProperty("kind").GetString());
        Assert.Equal(maker, item.GetProperty("requestedBy").GetGuid());
        Assert.Equal(await _h.ScalarAsync<string>("select display_name from iam.users where id = $1", Ct, maker), item.GetProperty("requestedByName").GetString());
        Assert.Equal("month end", item.GetProperty("reason").GetString());
        Assert.Equal("Approval test", item.GetProperty("subject").GetProperty("en").GetString());
        Assert.Equal(item.GetProperty("requestedAt").GetDateTimeOffset().AddDays(7), item.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.True(item.GetProperty("canApprove").GetBoolean());
        var details = item.GetProperty("details").EnumerateArray().ToDictionary(d => d.GetProperty("label").GetString()!);
        Assert.Equal(key, details["changes.detail.settingKey"].GetProperty("text").GetString());
        Assert.Equal("changes.layer.company", details["changes.detail.layer"].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Object, details["changes.detail.company"].GetProperty("names").ValueKind);
        Assert.Equal("42", details["changes.detail.newValue"].GetProperty("text").GetString());

        // The requester sees it among their own, never as theirs to decide (FR-XM-32).
        Assert.Null(await FindAsync(maker, "toDecide", request));
        var mine = await FindAsync(maker, "mine", request) ?? throw new InvalidOperationException("not in the maker's own list");
        Assert.False(mine.GetProperty("canApprove").GetBoolean());
        Assert.Equal("errors.maker_checker.own_request", mine.GetProperty("approveProblem").GetString());

        // An approver whose right covers another company only: offered, but not approvable, and the endpoint agrees.
        var narrowItem = await FindAsync(narrow, "toDecide", request) ?? throw new InvalidOperationException("not listed for the narrow approver");
        Assert.False(narrowItem.GetProperty("canApprove").GetBoolean());
        var refused = await _h.PostAsync(narrow, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "not mine" }, Ct);
        Assert.Equal((HttpStatusCode.Forbidden, narrowItem.GetProperty("approveProblem").GetString()), (refused.Status, refused.Text("title")));

        // Reading the list changed nothing; once approved it is no longer listed and the setting has the new value.
        Assert.Equal("pending", await _h.ScalarAsync<string>("select status from iam.maker_checker_requests where id = $1", Ct, request));
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "ok" }, Ct)).Status);
        Assert.Null(await FindAsync(checker, "toDecide", request));
        Assert.Null(await FindAsync(maker, "mine", request));
        Assert.Equal("42", await _h.ScalarAsync<string>("select value::text from cfg.setting_values where setting_key = $1", Ct, key));
    }

    [Fact]
    public async Task FR_KRN_IAM_14_S112d_expired_requests_are_not_offered_and_people_without_access_are_refused()
    {
        var maker = await _h.UserAsync(Ct, null, "tenant_admin");
        var checker = await _h.UserAsync(Ct, null, "tenant_admin");
        var key = await HeldSettingAsync();
        var held = await _h.PutAsync(maker, $"/api/cfg/settings/{key}", new { layer = "tenant", scopeId = (Guid?)null, value = 5, reason = "test" }, Ct);
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.NotNull(await FindAsync(checker, "toDecide", request));

        _h.Clock.Advance(TimeSpan.FromDays(8)); // iam.maker_checker.expiry_days is 7
        Assert.Null(await FindAsync(checker, "toDecide", request));
        Assert.Null(await FindAsync(maker, "mine", request));

        var nobody = await _h.UserAsync(Ct, null, "business_user");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, Pending, Ct)).Status);
    }
}
