using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Npgsql;

namespace Erp.IntegrationTests.Org;

/// <summary>
/// Organization units over time (slice S1.14b, CR-070 S1.14b detail): the structure of a hierarchy on a date, the move of a
/// unit from a date, the comparison of two dates, and the rules that keep the structure sound (no cycle, no parent of another
/// company), through the real API pipeline, the real access engine and a real PostgreSQL.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class OrgUnitTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void Expect(HttpStatusCode expected, Erp.IntegrationTests.Identity.Answer answer) =>
        Assert.True(answer.Status == expected, $"expected {expected}, got {answer.Status}: {answer.Body}");

    /// <summary>A hierarchy with a unit type and a company, ready for units.</summary>
    private sealed record Org(Guid Company, Guid Hierarchy, Guid UnitType);

    private async Task<Org> NewOrgAsync(Guid? company = null)
    {
        var co = company ?? await _h.CompanyAsync(Ct);
        var n = Guid.NewGuid().ToString("N")[..8];
        var hierarchy = Guid.CreateVersion7();
        var type = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into org.org_hierarchies (id, code, name_i18n, kind, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Reporting"}', 'reporting', now(), $3, now(), $3)
            """, Ct, hierarchy, "H" + n, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        await _h.ExecAsync("""
            insert into org.org_unit_types (id, code, name_i18n, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Department"}', now(), $3, now(), $3)
            """, Ct, type, "T" + n, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        return new Org(co, hierarchy, type);
    }

    private async Task<Guid> UnitAsync(Org org, string code, Guid? parent, string from = "2026-01-01", Guid? company = null, bool link = true)
    {
        var id = Guid.CreateVersion7();
        var system = Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId;
        await _h.ExecAsync("""
            insert into org.org_units (id, company_id, code, name_i18n, unit_type_id, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4::jsonb, $5, now(), $6, now(), $6)
            """, Ct, id, company ?? org.Company, code + "-" + id.ToString("N")[..6], "{\"en\":\"" + code + "\"}", org.UnitType, system);
        if (link)
        {
            await LinkAsync(org, id, parent, from);
        }

        return id;
    }

    private Task LinkAsync(Org org, Guid unit, Guid? parent, string from) => _h.ExecAsync("""
        insert into org.org_unit_links (id, hierarchy_id, unit_id, parent_unit_id, valid_from, created_at, created_by, updated_at, updated_by)
        values ($1, $2, $3, $4, $5::date, now(), $6, now(), $6)
        """, Ct, Guid.CreateVersion7(), org.Hierarchy, unit, parent, from, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);

    private static object Move(Org org, Guid? parent, string from, string? reason = null) => new { hierarchyId = org.Hierarchy, newParentId = parent, effectiveFrom = from, reason };

    private async Task<Guid?> ParentOnAsync(Org org, Guid unit, string date)
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var tree = await _h.GetAsync(admin, $"/api/org/hierarchies/{org.Hierarchy}/tree?asOf={date}", Ct);
        Expect(HttpStatusCode.OK, tree);
        var node = tree.Json.GetProperty("nodes").EnumerateArray().SingleOrDefault(n => n.GetProperty("unitId").GetGuid() == unit);
        Assert.NotEqual(JsonValueKind.Undefined, node.ValueKind);
        return node.GetProperty("parentUnitId").ValueKind == JsonValueKind.Null ? null : node.GetProperty("parentUnitId").GetGuid();
    }

    [Fact]
    public async Task FR_ADM_ORG_03_S114b_moving_a_department_from_a_date_shows_the_old_parent_before_it_and_the_new_one_from_it_and_compare_lists_the_move()
    {
        var org = await NewOrgAsync();
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var root = await UnitAsync(org, "ROOT", null);
        var divA = await UnitAsync(org, "DIVA", root);
        var divB = await UnitAsync(org, "DIVB", root);
        var dept = await UnitAsync(org, "DEPT", divA);
        var team = await UnitAsync(org, "TEAM", dept);

        var moved = await _h.PostAsync(admin, $"/api/org/org-units/{dept}/move", Move(org, divB, "2027-01-01", "Reorganisation"), Ct);

        Expect(HttpStatusCode.Created, moved);
        Assert.Equal(divA, await ParentOnAsync(org, dept, "2026-12-31"));
        Assert.Equal(divB, await ParentOnAsync(org, dept, "2027-01-01"));
        Assert.Equal(dept, await ParentOnAsync(org, team, "2027-06-01"));
        var compare = await _h.GetAsync(admin, $"/api/org/hierarchies/{org.Hierarchy}/compare?from=2026-12-31&to=2027-01-01", Ct);
        Expect(HttpStatusCode.OK, compare);
        var change = Assert.Single(compare.Json.GetProperty("moved").EnumerateArray());
        Assert.Equal(dept, change.GetProperty("unitId").GetGuid());
        Assert.Equal(divA, change.GetProperty("fromParentId").GetGuid());
        Assert.Equal(divB, change.GetProperty("toParentId").GetGuid());
        Assert.Empty(compare.Json.GetProperty("added").EnumerateArray());
        Assert.Empty(compare.Json.GetProperty("removed").EnumerateArray());
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from org.org_unit_links where hierarchy_id = $1 and unit_id = $2 and valid_to = '2027-01-01'", Ct, org.Hierarchy, dept));
        var changes = await _h.ScalarAsync<string>("select string_agg(changes::text, ' ') from aud.change_log where entity_type = 'org.org_unit_links' and company_id = $1", Ct, org.Company);
        Assert.Contains("Reorganisation", changes, StringComparison.Ordinal);
        Assert.Contains("valid_to", changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_ADM_ORG_03_S114b_a_move_that_would_put_a_unit_under_itself_or_its_own_descendant_is_refused_with_a_clear_message()
    {
        var org = await NewOrgAsync();
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var root = await UnitAsync(org, "ROOT", null);
        var dept = await UnitAsync(org, "DEPT", root);
        var team = await UnitAsync(org, "TEAM", dept);
        var squad = await UnitAsync(org, "SQUAD", team);

        var underSelf = await _h.PostAsync(admin, $"/api/org/org-units/{dept}/move", Move(org, dept, "2027-01-01"), Ct);
        var underDescendant = await _h.PostAsync(admin, $"/api/org/org-units/{dept}/move", Move(org, squad, "2027-01-01"), Ct);

        Expect(HttpStatusCode.BadRequest, underSelf);
        Expect(HttpStatusCode.BadRequest, underDescendant);
        Assert.Contains("errors.org.move_cycle", underDescendant.Body, StringComparison.Ordinal);
        Assert.Equal(root, await ParentOnAsync(org, dept, "2027-06-01"));
        // The database refuses the same shape from any other path (the generated endpoints, a script).
        await using var owner = await _h.OwnerAsync(null, Ct);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, """
            update org.org_unit_links set parent_unit_id = $2 where hierarchy_id = $1 and unit_id = $3
            """, Ct, org.Hierarchy, squad, dept));
        Assert.Equal("ck_org_unit_links_no_cycle", ex.ConstraintName);
    }

    [Fact]
    public async Task FR_ADM_ORG_03_S114b_a_cycle_that_only_appears_on_a_later_date_is_refused_too()
    {
        var org = await NewOrgAsync();
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var root = await UnitAsync(org, "ROOT", null);
        var a = await UnitAsync(org, "A", root);
        var b = await UnitAsync(org, "B", root);
        // From 1 July 2027 B sits under A; today A may not be moved under B from 1 January 2027 on, because on 1 July the loop would close.
        Expect(HttpStatusCode.Created, await _h.PostAsync(admin, $"/api/org/org-units/{b}/move", Move(org, a, "2027-07-01"), Ct));

        var refused = await _h.PostAsync(admin, $"/api/org/org-units/{a}/move", Move(org, b, "2027-01-01"), Ct);

        Expect(HttpStatusCode.BadRequest, refused);
        Assert.Contains("errors.org.move_cycle", refused.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_ADM_ORG_03_S114b_a_move_before_the_start_of_the_current_link_to_the_same_parent_or_to_another_company_is_refused()
    {
        var org = await NewOrgAsync();
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var root = await UnitAsync(org, "ROOT", null);
        var other = await UnitAsync(org, "OTHER", root);
        var dept = await UnitAsync(org, "DEPT", root, from: "2026-03-01");
        var foreignCompany = await _h.CompanyAsync(Ct);
        var foreign = await UnitAsync(org, "FOREIGN", null, company: foreignCompany);

        var before = await _h.PostAsync(admin, $"/api/org/org-units/{dept}/move", Move(org, other, "2026-03-01"), Ct);
        var same = await _h.PostAsync(admin, $"/api/org/org-units/{dept}/move", Move(org, root, "2027-01-01"), Ct);
        var across = await _h.PostAsync(admin, $"/api/org/org-units/{dept}/move", Move(org, foreign, "2027-01-01"), Ct);
        var missing = await _h.PostAsync(admin, $"/api/org/org-units/{dept}/move", new { hierarchyId = org.Hierarchy }, Ct);
        var unknown = await _h.PostAsync(admin, $"/api/org/org-units/{Guid.NewGuid()}/move", Move(org, root, "2027-01-01"), Ct);

        Expect(HttpStatusCode.BadRequest, before);
        Assert.Contains("errors.org.move_before_link_start", before.Body, StringComparison.Ordinal);
        Expect(HttpStatusCode.BadRequest, same);
        Assert.Contains("errors.org.move_same_parent", same.Body, StringComparison.Ordinal);
        Expect(HttpStatusCode.BadRequest, across);
        Assert.Contains("errors.org.move_other_company", across.Body, StringComparison.Ordinal);
        Expect(HttpStatusCode.BadRequest, missing);
        Expect(HttpStatusCode.NotFound, unknown);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from org.org_unit_links where unit_id = $1", Ct, dept));
        await using var owner = await _h.OwnerAsync(null, Ct);
        var direct = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(owner, """
            insert into org.org_unit_links (id, hierarchy_id, unit_id, parent_unit_id, valid_from, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, '2026-01-01', now(), $5, now(), $5)
            """, Ct, Guid.CreateVersion7(), org.Hierarchy, foreign, root, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId));
        Assert.Equal("ck_org_unit_links_same_company", direct.ConstraintName);
    }

    [Fact]
    public async Task FR_KRN_ORG_03_S114b_a_unit_not_yet_in_the_hierarchy_is_placed_by_a_move_and_a_move_keeps_a_later_dated_link()
    {
        var org = await NewOrgAsync();
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var root = await UnitAsync(org, "ROOT", null);
        var a = await UnitAsync(org, "A", root);
        var b = await UnitAsync(org, "B", root);
        var loose = await UnitAsync(org, "LOOSE", null, link: false);
        var tree = await _h.GetAsync(admin, $"/api/org/hierarchies/{org.Hierarchy}/tree?asOf=2026-06-01", Ct);
        Assert.Contains(loose, tree.Json.GetProperty("unplaced").EnumerateArray().Select(n => n.GetProperty("unitId").GetGuid()));

        Expect(HttpStatusCode.Created, await _h.PostAsync(admin, $"/api/org/org-units/{loose}/move", Move(org, a, "2026-06-01"), Ct));
        // A move dated before a later link keeps that link: A goes under B on 1 October, a move on 1 August ends on 1 October.
        Expect(HttpStatusCode.Created, await _h.PostAsync(admin, $"/api/org/org-units/{loose}/move", Move(org, b, "2026-10-01"), Ct));
        Expect(HttpStatusCode.Created, await _h.PostAsync(admin, $"/api/org/org-units/{loose}/move", Move(org, root, "2026-08-01"), Ct));

        Assert.Equal(a, await ParentOnAsync(org, loose, "2026-07-31"));
        Assert.Equal(root, await ParentOnAsync(org, loose, "2026-09-30"));
        Assert.Equal(b, await ParentOnAsync(org, loose, "2026-10-01"));
        var compare = await _h.GetAsync(admin, $"/api/org/hierarchies/{org.Hierarchy}/compare?from=2026-05-01&to=2026-06-01", Ct);
        Assert.Equal(loose, Assert.Single(compare.Json.GetProperty("added").EnumerateArray()).GetProperty("unitId").GetGuid());
        var reverse = await _h.GetAsync(admin, $"/api/org/hierarchies/{org.Hierarchy}/compare?from=2026-06-01&to=2026-05-01", Ct);
        Assert.Equal(loose, Assert.Single(reverse.Json.GetProperty("removed").EnumerateArray()).GetProperty("unitId").GetGuid());
        Expect(HttpStatusCode.BadRequest, await _h.GetAsync(admin, $"/api/org/hierarchies/{org.Hierarchy}/compare?from=2026-06-01", Ct));
    }

    [Fact]
    public async Task FR_KRN_IAM_06_S114b_reading_and_moving_need_their_permissions_and_cover_only_the_companies_of_the_role()
    {
        var org = await NewOrgAsync();
        var root = await UnitAsync(org, "ROOT", null);
        var dept = await UnitAsync(org, "DEPT", root);
        var newParent = await UnitAsync(org, "NEWP", root);
        var reader = await _h.UserAsync(Ct);
        var readRole = await _h.RoleAsync(Ct, ["org.org_unit_links.read", "org.org_units.read"], null, []);
        await _h.AssignAsync(reader, readRole, org.Company, _h.Today, null, Ct);
        var nobody = await _h.UserAsync(Ct);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");

        Expect(HttpStatusCode.OK, await _h.GetAsync(reader, $"/api/org/hierarchies/{org.Hierarchy}/tree", Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(reader, $"/api/org/org-units/{dept}/move", Move(org, newParent, "2027-01-01"), Ct));
        Expect(HttpStatusCode.Forbidden, await _h.GetAsync(nobody, $"/api/org/hierarchies/{org.Hierarchy}/tree", Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(nobody, $"/api/org/org-units/{dept}/move", Move(org, newParent, "2027-01-01"), Ct));
        Expect(HttpStatusCode.NotFound, await _h.GetAsync(admin, $"/api/org/hierarchies/{Guid.NewGuid()}/tree", Ct));

        var otherCompany = await _h.CompanyAsync(Ct);
        var hidden = await UnitAsync(org, "HIDDEN", null, company: otherCompany);
        var seen = await _h.GetAsync(reader, $"/api/org/hierarchies/{org.Hierarchy}/tree?asOf=2026-06-01", Ct);
        Assert.DoesNotContain(hidden.ToString(), seen.Body, StringComparison.Ordinal);
        Assert.Contains(root.ToString(), seen.Body, StringComparison.Ordinal);
    }
}
