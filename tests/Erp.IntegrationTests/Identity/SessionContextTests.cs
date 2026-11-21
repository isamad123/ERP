using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.Identity;

/// <summary>
/// Slice S1.12b, S1.12 ruling Q9: <c>GET /api/kernel/me</c> through the real API pipeline and the real access engine. The
/// web shell shows the company switcher (FR-ADM-ORG-06), menus (FR-KRN-ENT-03 with the permissions) and the person's
/// language, time zone and number style (FR-KRN-LOC-01, FR-KRN-LOC-02 "apply", ruling Q6) from it. Locked and suspended
/// customers: <c>EntitlementTests</c>; other customers: the tenant-isolation suite.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class SessionContextTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task S112b_FR_ADM_ORG_06_me_lists_the_companies_and_the_permissions_with_the_companies_each_covers()
    {
        var a = await CompanyAsync();
        var b = await CompanyAsync();
        var inactive = await CompanyAsync();
        await _h.ExecAsync("update org.companies set status = 'inactive' where id = $1", Ct, inactive);
        var user = await _h.UserAsync(Ct);
        var first = await _h.RoleAsync(Ct, ["iam.self.manage", "org.branches.read"]);
        var second = await _h.RoleAsync(Ct, ["org.branches.read", "ntf.in_app_items.read"]);
        await _h.AssignAsync(user, first, a, _h.Today.AddDays(-1), null, Ct);
        await _h.AssignAsync(user, second, b, _h.Today.AddDays(-1), null, Ct);
        await _h.AssignAsync(user, second, inactive, _h.Today.AddDays(-1), null, Ct);
        await _h.ExecAsync("update iam.users set default_company_id = $2, language = 'ur', time_zone = 'Asia/Dubai' where id = $1", Ct, user, b);

        var me = await _h.GetAsync(user, "/api/kernel/me", Ct);
        Assert.True(me.Status == HttpStatusCode.OK, me.Body);
        var json = me.Json;
        Assert.Equal(user, json.GetProperty("userId").GetGuid());
        Assert.Equal("ur", json.GetProperty("language").GetString());
        Assert.Equal("Asia/Dubai", json.GetProperty("timeZone").GetString());
        Assert.Equal(b, json.GetProperty("defaultCompanyId").GetGuid());
        var companies = json.GetProperty("companies").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([a, b], companies.Order().ToList()); // the inactive company is left out
        Assert.All(json.GetProperty("companies").EnumerateArray(), c => Assert.False(string.IsNullOrEmpty(c.GetProperty("name").GetProperty("en").GetString())));

        var grants = json.GetProperty("permissions").EnumerateArray().ToDictionary(p => p.GetProperty("code").GetString()!, p => p.GetProperty("companies"));
        Assert.Equal(["iam.self.manage", "ntf.in_app_items.read", "org.branches.read"], grants.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal([a], Ids(grants["iam.self.manage"]));
        Assert.Equal(new[] { a, b, inactive }.Order().ToList(), Ids(grants["org.branches.read"]).Order().ToList());

        // A role for all companies opens every active company, and its permissions carry null (all companies).
        var everywhere = await _h.UserAsync(Ct, null, "business_user");
        var wide = (await _h.GetAsync(everywhere, "/api/kernel/me", Ct)).Json;
        var all = wide.GetProperty("companies").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(a, all);
        Assert.Contains(b, all);
        Assert.DoesNotContain(inactive, all);
        Assert.Contains(await _h.DefaultCompanyAsync(Ct), all);
        Assert.All(wide.GetProperty("permissions").EnumerateArray(), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("companies").ValueKind));
        Assert.Equal(all[0], wide.GetProperty("defaultCompanyId").GetGuid()); // no default company stored: the first by code
        Assert.False(wide.GetProperty("mfa").GetProperty("required").GetBoolean());
        Assert.Empty(wide.GetProperty("mfa").GetProperty("methods").EnumerateArray());
    }

    [Fact]
    public async Task S112b_FR_KRN_IAM_10_me_needs_a_signed_in_person_holding_the_self_service_permission()
    {
        var nobody = await _h.UserAsync(Ct); // no role: holds nothing
        var refused = await _h.GetAsync(nobody, "/api/kernel/me", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("errors.access.forbidden", refused.Body, StringComparison.Ordinal);

        using var anonymous = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(_h.Code), null);
        using var response = await anonymous.GetAsync(new Uri("/api/kernel/me", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task S112b_FR_KRN_LOC_02_number_style_follows_the_user_then_the_company_in_hand_then_the_customer()
    {
        var a = await CompanyAsync();
        var b = await CompanyAsync();
        var outsider = await CompanyAsync();
        var user = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["iam.self.manage"]);
        await _h.AssignAsync(user, role, a, _h.Today.AddDays(-1), null, Ct);
        await _h.AssignAsync(user, role, b, _h.Today.AddDays(-1), null, Ct);
        await _h.ExecAsync("update iam.users set default_company_id = $2 where id = $1", Ct, user, b);

        (string Digits, string Grouping) Numbers(Answer answer)
        {
            Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
            var n = answer.Json.GetProperty("numbers");
            return (n.GetProperty("digits").GetString()!, n.GetProperty("grouping").GetString()!);
        }

        Assert.Equal(("western", "international"), Numbers(await _h.GetAsync(user, "/api/kernel/me", Ct))); // the shipped defaults (Q6)

        var undo = new List<Func<Task>>
        {
            await SetAsync("loc.number.grouping", "tenant", null, "\"lakh_crore\""),
            await SetAsync("loc.number.digits", "company", a, "\"eastern_arabic_indic\""),
            await SetAsync("loc.number.digits", "company", outsider, "\"eastern_arabic_indic\""),
        };
        try
        {
            Assert.Equal(("western", "lakh_crore"), Numbers(await _h.GetAsync(user, "/api/kernel/me", Ct))); // default company b
            Assert.Equal(("eastern_arabic_indic", "lakh_crore"), Numbers(await _h.GetAsync(user, $"/api/kernel/me?companyId={a}", Ct)));
            Assert.Equal(("western", "lakh_crore"), Numbers(await _h.GetAsync(user, $"/api/kernel/me?companyId={outsider}", Ct))); // not theirs: ignored

            undo.Add(await SetAsync("loc.number.digits", "user", user, "\"western\""));
            Assert.Equal(("western", "lakh_crore"), Numbers(await _h.GetAsync(user, $"/api/kernel/me?companyId={a}", Ct))); // the person's own choice wins
        }
        finally
        {
            foreach (var u in undo)
            {
                await u();
            }
        }
    }

    private async Task<Func<Task>> SetAsync(string key, string layer, Guid? scope, string json)
    {
        await _h.ExecAsync("""
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5::jsonb, now(), $6, now(), $6)
            """, Ct, Guid.CreateVersion7(), key, layer, scope, json, BaselineSeed.SystemUserId);
        return () => _h.ExecAsync("delete from cfg.setting_values where setting_key = $1 and layer = $2 and scope_id is not distinct from $3", CancellationToken.None, key, layer, scope);
    }

    /// <summary>A working company (the row factory fills every column, flags included, with generated values).</summary>
    private async Task<Guid> CompanyAsync()
    {
        var id = await _h.CompanyAsync(Ct);
        await _h.ExecAsync("update org.companies set is_active = true, is_deleted = false, status = 'active' where id = $1", Ct, id);
        return id;
    }

    private static List<Guid> Ids(JsonElement companies) => companies.EnumerateArray().Select(c => c.GetGuid()).ToList();
}
