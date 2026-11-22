using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.Views;

/// <summary>
/// Slice S1.12c through the real API pipeline and the real access engine: saved list views (Build item 17, ruling Q12,
/// FR-ADM-NAV-02), label overrides (Build item 15, FR-KRN-ORG-08, FR-KRN-MET-22), the changer's name in a record's
/// history (Build item 19, FR-KRN-AUD-07) and the branch rule the branch screens show (FR-KRN-ORG-02). Other customers:
/// the tenant-isolation suite.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class SavedViewTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Views = "/api/cfg/saved-views";
    private AccessHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Definition(string search = "khi") => new { search, sort = "code", filters = new { isActive = true }, columns = new[] { new { field = "code", width = 120 } } };

    private async Task<Guid> PersonAsync(params string[] permissions)
    {
        var user = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["iam.self.manage", .. permissions]);
        await _h.AssignAsync(user, role, null, _h.Today.AddDays(-1), null, Ct);
        return user;
    }

    [Fact]
    [Trait("Requirement", "FR-ADM-NAV-02")]
    public async Task S112c_FR_ADM_NAV_02_a_person_saves_renames_and_deletes_their_own_views_and_nobody_else_sees_them()
    {
        var list = "org.branches_" + Guid.NewGuid().ToString("N")[..6];
        var me = await PersonAsync();
        var other = await PersonAsync();

        var created = await _h.PostAsync(me, Views, new { listKey = list, name = "Karachi branches", definition = Definition() }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.True(created.Json.GetProperty("mine").GetBoolean());
        Assert.Equal(JsonValueKind.Null, created.Json.GetProperty("role").ValueKind);
        Assert.Equal("khi", created.Json.GetProperty("definition").GetProperty("search").GetString());

        var mine = (await _h.GetAsync(me, $"{Views}?list={list}", Ct)).Json;
        Assert.Equal(id, Assert.Single(mine.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Empty((await _h.GetAsync(other, $"{Views}?list={list}", Ct)).Json.EnumerateArray()); // personal: only mine

        var duplicate = await _h.PostAsync(me, Views, new { listKey = list, name = "karachi BRANCHES", definition = Definition() }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        Assert.Contains("errors.views.duplicate_name", duplicate.Body, StringComparison.Ordinal);
        var unnamed = await _h.PostAsync(me, Views, new { listKey = list, name = "  ", definition = Definition() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.Status);
        Assert.Equal("errors.views.invalid_name", unnamed.Json.GetProperty("errors").GetProperty("name")[0].GetString());
        var badList = await _h.PostAsync(me, Views, new { listKey = "Branches!", name = "x", definition = Definition() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badList.Status);
        var tooBig = await _h.PostAsync(me, Views, new { listKey = list, name = "big", definition = new { search = new string('x', 9000) } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.Status);

        // Someone else cannot change or delete it: it does not exist for them.
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PutAsync(other, $"{Views}/{id}", new { name = "Mine now", definition = Definition(), rowVersion = 1 }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.DeleteAsync(other, $"{Views}/{id}?rowVersion=1", Ct)).Status);

        var renamed = await _h.PutAsync(me, $"{Views}/{id}", new { name = "Karachi only", definition = Definition("karachi"), rowVersion = 1 }, Ct);
        Assert.True(renamed.Status == HttpStatusCode.OK, renamed.Body);
        Assert.Equal(2, renamed.Json.GetProperty("rowVersion").GetInt64());
        var stale = await _h.PutAsync(me, $"{Views}/{id}", new { name = "Lost", definition = Definition(), rowVersion = 1 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.DeleteAsync(me, $"{Views}/{id}?rowVersion=1", Ct)).Status);

        // The change log records it like any other change (S1.07).
        Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'cfg.saved_views' and entity_id = $1", Ct, id));
        Assert.Equal(HttpStatusCode.NoContent, (await _h.DeleteAsync(me, $"{Views}/{id}?rowVersion=2", Ct)).Status);
        Assert.Empty((await _h.GetAsync(me, $"{Views}?list={list}", Ct)).Json.EnumerateArray());
    }

    [Fact]
    [Trait("Requirement", "FR-ADM-NAV-02")]
    public async Task S112c_FR_ADM_NAV_02_a_view_shared_with_a_role_reaches_its_holders_only_and_stays_the_owners()
    {
        var list = "org.branches_" + Guid.NewGuid().ToString("N")[..6];
        var sharer = await PersonAsync("cfg.saved_views.share");
        var noShare = await PersonAsync();
        var team = await _h.RoleAsync(Ct, ["iam.self.manage"]);
        var holder = await PersonAsync(); // self-service through another role, so losing the team role keeps the list readable
        await _h.AssignAsync(holder, team, null, _h.Today.AddDays(-1), null, Ct);
        var outsider = await PersonAsync();

        var refused = await _h.PostAsync(noShare, Views, new { listKey = list, name = "Shared", definition = Definition(), roleId = team }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status); // sharing needs cfg.saved_views.share
        Assert.Empty((await _h.GetAsync(noShare, $"{Views}/roles", Ct)).Json.EnumerateArray());
        Assert.Contains((await _h.GetAsync(sharer, $"{Views}/roles", Ct)).Json.EnumerateArray(), r => r.GetProperty("id").GetGuid() == team);
        var unknown = await _h.PostAsync(sharer, Views, new { listKey = list, name = "Shared", definition = Definition(), roleId = Guid.CreateVersion7() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);

        var shared = await _h.PostAsync(sharer, Views, new { listKey = list, name = "Shared", definition = Definition(), roleId = team }, Ct);
        Assert.True(shared.Status == HttpStatusCode.Created, shared.Body);
        var id = shared.Json.GetProperty("id").GetGuid();
        Assert.Equal(team, shared.Json.GetProperty("role").GetProperty("id").GetGuid());

        var seen = Assert.Single((await _h.GetAsync(holder, $"{Views}?list={list}", Ct)).Json.EnumerateArray());
        Assert.False(seen.GetProperty("mine").GetBoolean());
        Assert.False(string.IsNullOrEmpty(seen.GetProperty("ownerName").GetString()));
        Assert.Empty((await _h.GetAsync(outsider, $"{Views}?list={list}", Ct)).Json.EnumerateArray());

        // A holder may use it but not change it; the owner keeps the share without the permission check when only renaming.
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PutAsync(holder, $"{Views}/{id}", new { name = "Taken", definition = Definition(), roleId = team, rowVersion = 1 }, Ct)).Status);
        var renamed = await _h.PutAsync(sharer, $"{Views}/{id}", new { name = "Shared view", definition = Definition(), roleId = team, rowVersion = 1 }, Ct);
        Assert.True(renamed.Status == HttpStatusCode.OK, renamed.Body);

        // When the holder loses the role, the view is gone from their list.
        await _h.ExecAsync("update iam.user_roles set valid_to = $3 where user_id = $1 and role_id = $2", Ct, holder, team, _h.Today);
        Assert.Empty((await _h.GetAsync(holder, $"{Views}?list={list}", Ct)).Json.EnumerateArray());
    }

    [Fact]
    [Trait("Requirement", "FR-KRN-IAM-10")]
    public async Task S112c_FR_KRN_IAM_10_saved_views_and_labels_need_the_self_service_permission()
    {
        var nobody = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, $"{Views}?list=org.branches", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, Views, new { listKey = "org.branches", name = "x", definition = Definition() }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, "/api/kernel/labels", Ct)).Status);
    }

    [Fact]
    [Trait("Requirement", "FR-KRN-ORG-08")]
    [Trait("Requirement", "FR-KRN-MET-22")]
    public async Task S112c_FR_KRN_ORG_08_label_overrides_merge_pack_then_customer_then_the_company_in_hand()
    {
        var key = "fields.org.branches.code_" + Guid.NewGuid().ToString("N")[..6];
        var mine = await _h.CompanyAsync(Ct);
        var theirs = await _h.CompanyAsync(Ct);
        var user = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["iam.self.manage"]);
        await _h.AssignAsync(user, role, mine, _h.Today.AddDays(-1), null, Ct);
        async Task LabelAsync(string language, string layer, Guid? scope, string text) => await _h.ExecAsync("""
            insert into cfg.labels (id, key, language, layer, scope_id, text, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, now(), $7, now(), $7)
            """, Ct, Guid.CreateVersion7(), key, language, layer, scope, text, BaselineSeed.SystemUserId);
        await LabelAsync("en", "pack", null, "Pack code");
        await LabelAsync("en", "tenant", null, "Office code");
        await LabelAsync("ur", "tenant", null, "دفتر کوڈ");
        await LabelAsync("en", "company", mine, "Site code");
        await LabelAsync("en", "company", theirs, "Their code");

        string? Text(JsonElement json, string language) =>
            json.TryGetProperty(language, out var texts) && texts.TryGetProperty(key, out var text) ? text.GetString() : null;

        var customer = await _h.GetAsync(user, "/api/kernel/labels", Ct);
        Assert.True(customer.Status == HttpStatusCode.OK, customer.Body);
        Assert.Equal("Office code", Text(customer.Json, "en")); // the customer layer wins over the pack
        Assert.Equal("دفتر کوڈ", Text(customer.Json, "ur"));
        Assert.Equal("Site code", Text((await _h.GetAsync(user, $"/api/kernel/labels?companyId={mine}", Ct)).Json, "en"));
        // A company that is not the person's adds nothing.
        Assert.Equal("Office code", Text((await _h.GetAsync(user, $"/api/kernel/labels?companyId={theirs}", Ct)).Json, "en"));
    }

    [Fact]
    [Trait("Requirement", "FR-KRN-AUD-07")]
    public async Task S112c_FR_KRN_AUD_07_a_records_history_names_the_person_who_changed_it()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var name = await _h.ScalarAsync<string>("select display_name from iam.users where id = $1", Ct, admin);
        var company = await _h.DefaultCompanyAsync(Ct);
        var created = await _h.PostAsync(admin, "/api/org/branches", new { companyId = company, code = "H" + Guid.NewGuid().ToString("N")[..6], nameI18n = new { en = "History" } }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var id = created.Json.GetProperty("id").GetGuid();
        var updated = await _h.PutAsync(admin, $"/api/org/branches/{id}", new { code = created.Json.GetProperty("code").GetString(), nameI18n = new { en = "History office" }, rowVersion = 1 }, Ct);
        Assert.True(updated.Status == HttpStatusCode.OK, updated.Body);

        var history = await _h.GetAsync(admin, $"/api/aud/history/org.branches/{id}", Ct);
        Assert.True(history.Status == HttpStatusCode.OK, history.Body);
        var items = history.Json.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(name, i.GetProperty("actorName").GetString()));
        Assert.Equal("update", items[0].GetProperty("action").GetString()); // newest first
    }

    [Fact]
    [Trait("Requirement", "FR-KRN-ORG-02")]
    public async Task S112c_FR_KRN_ORG_02_deleting_a_companys_last_branch_is_refused_with_the_rule_named()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        await _h.ExecAsync("update org.companies set is_active = true, is_deleted = false, status = 'active' where id = $1", Ct, company);
        // Rule SC-2: the company got its default branch when it was created; it is the only one.
        var only = await _h.ScalarAsync<Guid>("select id from org.branches where company_id = $1 and not is_deleted", Ct, company);
        var version = await _h.ScalarAsync<long>("select row_version from org.branches where id = $1", Ct, only);

        var refused = await _h.DeleteAsync(admin, $"/api/org/branches/{only}?rowVersion={version}", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.data.rule_violated", refused.Json.GetProperty("title").GetString());
        Assert.Equal("ck_branches_one_per_company", refused.Json.GetProperty("detail").GetString());
    }
}
