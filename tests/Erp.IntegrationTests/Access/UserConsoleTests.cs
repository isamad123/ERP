#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Access;

/// <summary>
/// The administrator's views over users (slice S1.13a, CR-069): the user list, the access data of one user, the people lookup,
/// the single revocations of the user record and the username and e-mail clash rule (ruling Q6), through the real API
/// pipeline, the real access engine and a real PostgreSQL.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class UserConsoleTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<string> UsernameAsync(Guid user) => await _h.ScalarAsync<string>("select username from iam.users where id = $1", Ct, user);

    [Fact]
    public async Task FR_ADM_USR_01_FR_ADM_USR_08_S113a_user_list_shows_roles_companies_second_factor_and_filters_and_pages()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["org.companies.read"]);
        var person = await _h.UserAsync(Ct);
        var assignment = await _h.AssignAsync(person, role, company, _h.Today.AddDays(-2), null, Ct);
        var ended = await _h.RoleAsync(Ct, ["org.companies.read"]);
        await _h.AssignAsync(person, ended, null, _h.Today.AddDays(-30), _h.Today.AddDays(-10), Ct);
        await _h.ExecAsync("""
            insert into iam.mfa_devices (id, user_id, kind, label, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'totp', 'phone', 'active', now(), $3, now(), $3)
            """, Ct, Guid.CreateVersion7(), person, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        var other = await _h.UserAsync(Ct);
        var name = await UsernameAsync(person);
        Assert.NotEqual(Guid.Empty, assignment);
        Assert.NotEqual(Guid.Empty, other);

        var answer = await _h.GetAsync(admin, $"/api/iam/user-overview?search={name}", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        var item = Assert.Single(answer.Json.GetProperty("items").EnumerateArray());
        Assert.Equal(person, item.GetProperty("id").GetGuid());
        Assert.Equal("active", item.GetProperty("status").GetString());
        Assert.True(item.GetProperty("hasMfa").GetBoolean());
        Assert.False(item.GetProperty("allCompanies").GetBoolean());
        Assert.Equal([company], item.GetProperty("companyIds").EnumerateArray().Select(e => e.GetGuid()).ToArray());
        var chip = Assert.Single(item.GetProperty("roles").EnumerateArray()); // the ended assignment is not in force
        Assert.Equal(role, chip.GetProperty("roleId").GetGuid());

        // Filters: role, company, second factor, status; the other user has none of them.
        Assert.Contains(person, await IdsAsync(admin, $"?search={name}&roleId={role}"));
        Assert.DoesNotContain(person, await IdsAsync(admin, $"?search={name}&roleId={ended}"));
        Assert.Contains(person, await IdsAsync(admin, $"?search={name}&companyId={company}"));
        Assert.Contains(person, await IdsAsync(admin, $"?search={name}&mfa=true"));
        Assert.DoesNotContain(person, await IdsAsync(admin, $"?search={name}&mfa=false"));
        Assert.DoesNotContain(person, await IdsAsync(admin, $"?search={name}&status=locked"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.GetAsync(admin, "/api/iam/user-overview?status=bogus", Ct)).Status);

        // Paging: a cursor continues after the last user of the page; nobody is listed twice.
        var first = await _h.GetAsync(admin, "/api/iam/user-overview?limit=2", Ct);
        var next = first.Json.GetProperty("next").GetString();
        Assert.NotNull(next);
        var second = await _h.GetAsync(admin, $"/api/iam/user-overview?limit=2&cursor={Uri.EscapeDataString(next!)}", Ct);
        var seen = first.Json.GetProperty("items").EnumerateArray().Concat(second.Json.GetProperty("items").EnumerateArray()).Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(seen.Count, seen.Distinct().Count());

        // Only people who may read users see the list.
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(await _h.UserAsync(Ct, null, "business_user"), "/api/iam/user-overview", Ct)).Status);
    }

    private async Task<List<Guid>> IdsAsync(Guid caller, string query)
    {
        var answer = await _h.GetAsync(caller, "/api/iam/user-overview" + query, Ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        return answer.Json.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public async Task FR_ADM_USR_04_S113a_user_record_shows_roles_with_ended_ones_groups_scopes_second_factors_and_devices_never_secrets()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var scope = await _h.ScopeAsync("own", new { }, Ct);
        var role = await _h.RoleAsync(Ct, ["org.companies.read"], [("org.companies", scope)]);
        var oldRole = await _h.RoleAsync(Ct, ["org.companies.read"]);
        var person = await _h.UserAsync(Ct);
        var system = Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId;
        await _h.AssignAsync(person, role, null, _h.Today.AddDays(-2), null, Ct);
        await _h.AssignAsync(person, oldRole, null, _h.Today.AddDays(-30), _h.Today.AddDays(-5), Ct);
        var group = Guid.CreateVersion7();
        await _h.ExecAsync("insert into iam.user_groups (id, code, name_i18n, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"Auditors\"}', now(), $3, now(), $3)", Ct, group, "grp" + Guid.NewGuid().ToString("N")[..8], system);
        await _h.ExecAsync("insert into iam.user_group_members (group_id, user_id) values ($1, $2)", Ct, group, person);
        await _h.ExecAsync("insert into iam.user_group_roles (id, group_id, role_id, reason, valid_from, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, 'g', $4, now(), $5, now(), $5)",
            Ct, Guid.CreateVersion7(), group, oldRole, _h.Today.AddDays(-1), system);
        await _h.ExecAsync("""
            insert into iam.mfa_devices (id, user_id, kind, secret, label, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'totp', '\x0102030405', 'phone', 'active', now(), $3, now(), $3)
            """, Ct, Guid.CreateVersion7(), person, system);
        var device = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into iam.trusted_devices (id, user_id, token_hash, label, expires_at, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, 'Firefox', now() + interval '10 days', now(), $4, now(), $4)
            """, Ct, device, person, Guid.NewGuid().ToString("N"), system);

        var answer = await _h.GetAsync(admin, $"/api/iam/users/{person}/access", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        var roles = answer.Json.GetProperty("roles").EnumerateArray().ToList();
        Assert.Equal(2, roles.Count);
        Assert.True(roles.Single(r => r.GetProperty("roleId").GetGuid() == role).GetProperty("inForce").GetBoolean());
        Assert.False(roles.Single(r => r.GetProperty("roleId").GetGuid() == oldRole).GetProperty("inForce").GetBoolean());
        var groupLine = Assert.Single(answer.Json.GetProperty("groups").EnumerateArray());
        Assert.Equal("Auditors", groupLine.GetProperty("name").GetProperty("en").GetString());
        Assert.Single(groupLine.GetProperty("roles").EnumerateArray());
        var scopeLine = Assert.Single(answer.Json.GetProperty("scopes").EnumerateArray()); // only the role in force counts
        Assert.Equal("org.companies", scopeLine.GetProperty("resource").GetString());
        Assert.Equal("role", scopeLine.GetProperty("source").GetString());
        var factor = Assert.Single(answer.Json.GetProperty("mfaDevices").EnumerateArray());
        Assert.Equal("totp", factor.GetProperty("kind").GetString());
        Assert.False(factor.TryGetProperty("secret", out _));
        Assert.DoesNotContain("AQIDBAU", answer.Body, StringComparison.Ordinal);
        Assert.Equal(device, Assert.Single(answer.Json.GetProperty("trustedDevices").EnumerateArray()).GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(admin, $"/api/iam/users/{Guid.NewGuid()}/access", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(await _h.UserAsync(Ct, null, "business_user"), $"/api/iam/users/{person}/access", Ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_IAM_04_FR_ADM_USR_24_S113a_administrator_ends_one_session_and_removes_one_remembered_browser()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var person = await _h.UserAsync(Ct);
        var system = Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId;
        var (keep, drop, device) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        foreach (var id in new[] { keep, drop })
        {
            await _h.ExecAsync("insert into iam.sessions (id, user_id, issued_at, last_seen_at, expires_at, created_at, created_by) values ($1, $2, now(), now(), now() + interval '8 hours', now(), $3)", Ct, id, person, system);
        }

        await _h.ExecAsync("""
            insert into iam.trusted_devices (id, user_id, token_hash, label, expires_at, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, 'Edge', now() + interval '10 days', now(), $4, now(), $4)
            """, Ct, device, person, Guid.NewGuid().ToString("N"), system);

        var ended = await _h.PostAsync(admin, $"/api/iam/users/{person}/sessions/{drop}/revoke", new { reason = "lost laptop" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, ended.Status);
        Assert.True(await _h.ScalarAsync<bool>("select revoked_at is not null from iam.sessions where id = $1", Ct, drop));
        Assert.False(await _h.ScalarAsync<bool>("select revoked_at is not null from iam.sessions where id = $1", Ct, keep));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from iam.outbox_messages where event_type = 'iam.sessions_revoked' and payload ->> 'session' = $1", Ct, drop.ToString()));

        Assert.Equal(HttpStatusCode.NoContent, (await _h.PostAsync(admin, $"/api/iam/users/{person}/trusted-devices/{device}/remove", new { reason = "x" }, Ct)).Status);
        Assert.True(await _h.ScalarAsync<bool>("select revoked_at is not null from iam.trusted_devices where id = $1", Ct, device));

        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(admin, $"/api/iam/users/{Guid.NewGuid()}/sessions/{drop}/revoke", new { reason = "x" }, Ct)).Status);
        var nobody = await _h.UserAsync(Ct, null, "business_user");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(nobody, $"/api/iam/users/{person}/sessions/{keep}/revoke", new { reason = "x" }, Ct)).Status);
    }

    [Fact]
    public async Task S113a_ruling_Q7_people_lookup_gives_name_username_and_id_of_active_people_of_the_callers_companies_only()
    {
        var company = await _h.CompanyAsync(Ct);
        var elsewhere = await _h.CompanyAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["wf.workflow_tasks.reassign"]);
        var plain = await _h.RoleAsync(Ct, ["org.companies.read"]);
        var approver = await _h.UserAsync(Ct);
        await _h.AssignAsync(approver, role, company, _h.Today.AddDays(-1), null, Ct);
        var neighbour = await _h.UserAsync(Ct);
        await _h.AssignAsync(neighbour, plain, company, _h.Today.AddDays(-1), null, Ct);
        var stranger = await _h.UserAsync(Ct);
        await _h.AssignAsync(stranger, plain, elsewhere, _h.Today.AddDays(-1), null, Ct);
        var disabled = await _h.UserAsync(Ct);
        await _h.AssignAsync(disabled, plain, company, _h.Today.AddDays(-1), null, Ct);
        await _h.ExecAsync("update iam.users set status = 'disabled' where id = $1", Ct, disabled);

        var answer = await _h.GetAsync(approver, "/api/iam/people-lookup", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        var ids = answer.Json.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(neighbour, ids);
        Assert.DoesNotContain(stranger, ids);
        Assert.DoesNotContain(disabled, ids);
        Assert.All(answer.Json.EnumerateArray(), e => Assert.Equal(["displayName", "id", "username"], e.EnumerateObject().Select(p => p.Name).Order().ToArray()));
        Assert.True(ids.Count <= 20);

        var found = await _h.GetAsync(approver, $"/api/iam/people-lookup?search={await UsernameAsync(neighbour)}", Ct);
        Assert.Equal(neighbour, Assert.Single(found.Json.EnumerateArray()).GetProperty("id").GetGuid());

        // An administrator for all companies sees the stranger too; a person with no picker permission is refused.
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var all = await _h.GetAsync(admin, $"/api/iam/people-lookup?search={await UsernameAsync(stranger)}", Ct);
        Assert.Equal(stranger, Assert.Single(all.Json.EnumerateArray()).GetProperty("id").GetGuid());
        var nobody = await _h.UserAsync(Ct);
        await _h.AssignAsync(nobody, plain, null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(nobody, "/api/iam/people-lookup", Ct)).Status);
    }

    [Fact]
    public async Task S113a_ruling_Q6_username_and_email_of_different_users_never_clash_in_any_letter_case()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var existing = await _h.UserAsync(Ct);
        var mail = await _h.ScalarAsync<string>("select email from iam.users where id = $1", Ct, existing);
        var name = await UsernameAsync(existing);
        var n = Guid.NewGuid().ToString("N")[..10];

        // A new username equal to another user's e-mail address is refused, whatever the case.
        var clash = await _h.PostAsync(admin, "/api/iam/users", new { username = mail.ToUpperInvariant(), email = $"clash{n}@x.test", displayName = "Clash", language = "en", timeZone = "Asia/Karachi" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, clash.Status);
        Assert.Contains("ck_users_username_email_clash", clash.Body, StringComparison.Ordinal);
        // An e-mail address equal to another user's username is refused too.
        var other = await _h.PostAsync(admin, "/api/iam/users", new { username = "u" + n, email = name.ToUpperInvariant(), displayName = "Clash", language = "en", timeZone = "Asia/Karachi" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, other.Status);
        Assert.Contains("ck_users_username_email_clash", other.Body, StringComparison.Ordinal);
        // An update of the username to another user's e-mail is refused; one's own e-mail as one's own username is allowed.
        var fine = await _h.PostAsync(admin, "/api/iam/users", new { username = $"fine{n}@x.test", email = $"fine{n}@x.test", displayName = "Fine", language = "en", timeZone = "Asia/Karachi" }, Ct);
        Assert.Equal(HttpStatusCode.Created, fine.Status);
        var id = fine.Json.GetProperty("id").GetGuid();
        var update = await _h.PutAsync(admin, $"/api/iam/users/{id}", new { username = mail, displayName = "Fine", language = "en", timeZone = "Asia/Karachi", rowVersion = fine.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, update.Status);
        Assert.Contains("ck_users_username_email_clash", update.Body, StringComparison.Ordinal);
    }
}
