using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.Metadata;

/// <summary>
/// Labels, option lists and custom fields (slice S1.15b, CR-071 S1.15b detail; FR-ADM-STU-25, -05, FR-KRN-MET-22, -01, -02, -05),
/// through the real API pipeline, the real access engine, the real kernel services and a real PostgreSQL.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class MetadataTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Units = "/api/org/org-units";
    private AccessHarness _h = null!;
    private Guid _admin;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        _admin = await _h.UserAsync(Ct, null, "tenant_admin");
        await ClearDefinitionsAsync();
    }

    /// <summary>The customer is shared by the tests of the collection: definitions of one test (a required field) must not reach another.</summary>
    public async ValueTask DisposeAsync() => await ClearDefinitionsAsync();

    private Task ClearDefinitionsAsync() => _h.ExecAsync("delete from cfg.custom_field_defs", CancellationToken.None);

    private static void Expect(HttpStatusCode expected, Answer answer) =>
        Assert.True(answer.Status == expected, $"expected {expected}, got {answer.Status}: {answer.Body}");

    private static string Key() => "f_" + Guid.NewGuid().ToString("N")[..10];

    private static string Fail(Answer answer, string field) =>
        answer.Json.GetProperty("errors").GetProperty(field)[0].GetString()!;

    // ---------------------------------------------------------------- set-up helpers

    private async Task<Guid> UnitTypeAsync()
    {
        var id = Guid.CreateVersion7();
        await _h.ExecAsync("""
            insert into org.org_unit_types (id, code, name_i18n, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Department"}', now(), $3, now(), $3)
            """, Ct, id, "T" + Guid.NewGuid().ToString("N")[..8], BaselineSeed.SystemUserId);
        return id;
    }

    private async Task<(Guid List, string[] Codes)> ListAsync(params string[] codes)
    {
        var code = "bg" + Guid.NewGuid().ToString("N")[..8];
        var created = await _h.PostAsync(_admin, "/api/cfg/option-lists", new { code, name = new { en = "Blood group", ur = "خون کا گروپ" } }, Ct);
        Expect(HttpStatusCode.Created, created);
        var id = created.Json.GetProperty("id").GetGuid();
        foreach (var item in codes)
        {
            Expect(HttpStatusCode.Created, await _h.PostAsync(_admin, $"/api/cfg/option-lists/{id}/items", new { code = item, label = new { en = item.ToUpperInvariant() } }, Ct));
        }

        return (id, codes);
    }

    /// <summary>Defines a custom field on org units and returns its id and key.</summary>
    private async Task<(Guid Id, string Key, long RowVersion)> DefineAsync(string type, object? validation = null, bool required = false, bool unique = false, Guid? list = null, object? defaultValue = null, string? key = null)
    {
        key ??= Key();
        var answer = await _h.PostAsync(_admin, "/api/cfg/custom-fields", new
        {
            entityType = "org.org_units", fieldKey = key, dataType = type, label = new { en = "Field " + key, ur = "خانہ " + key },
            validation, isRequired = required, isUnique = unique, optionListId = list, defaultValue,
        }, Ct);
        Expect(HttpStatusCode.Created, answer);
        return (answer.Json.GetProperty("id").GetGuid(), key, answer.Json.GetProperty("rowVersion").GetInt64());
    }

    private async Task<Answer> CreateUnitAsync(Guid user, Guid company, Guid type, object? custom, string? code = null) =>
        await _h.PostAsync(user, Units, new { companyId = company, code = code ?? "U" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "Unit" }, unitTypeId = type, custom }, Ct);

    private async Task<Answer> UpdateUnitAsync(Guid user, JsonElement unit, object? custom) =>
        await _h.PutAsync(user, $"{Units}/{unit.GetProperty("id").GetGuid()}", new
        {
            code = unit.GetProperty("code").GetString(), nameI18n = new { en = "Unit" }, unitTypeId = unit.GetProperty("unitTypeId").GetGuid(),
            companyId = unit.GetProperty("companyId").GetGuid(), custom, rowVersion = unit.GetProperty("rowVersion").GetInt64(),
        }, Ct);

    private async Task<Guid> UserWithAsync(string[] permissions, (string Resource, string Field, string Access)[]? fields = null)
    {
        var role = await _h.RoleAsync(Ct, permissions, [("org.org_units", "all")], fields);
        var user = await _h.UserAsync(Ct);
        await _h.AssignAsync(user, role, null, _h.Today.AddDays(-1), null, Ct);
        return user;
    }

    // ---------------------------------------------------------------- labels

    [Fact]
    public async Task FR_ADM_STU_25_a_a_label_is_set_in_both_languages_read_back_and_reset_to_the_product_text()
    {
        var key = "nav.test_" + Guid.NewGuid().ToString("N")[..8];
        var set = await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { en = "Wing", ur = "ونگ" } }, Ct);

        Expect(HttpStatusCode.OK, set);
        Assert.Equal(2, set.Json.GetProperty("items").GetArrayLength());
        var all = await _h.GetAsync(_admin, "/api/cfg/label-overrides", Ct);
        Assert.Contains(all.Json.GetProperty("items").EnumerateArray(), r => r.GetProperty("key").GetString() == key && r.GetProperty("language").GetString() == "ur" && r.GetProperty("text").GetString() == "ونگ");
        var seen = await _h.GetAsync(_admin, "/api/kernel/labels", Ct);
        Assert.Equal("Wing", seen.Json.GetProperty("en").GetProperty(key).GetString());
        Assert.Equal("ونگ", seen.Json.GetProperty("ur").GetProperty(key).GetString());

        // Only English changes; Urdu stays. Null resets one language; blank resets too.
        Expect(HttpStatusCode.OK, await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { en = "Section" } }, Ct));
        Expect(HttpStatusCode.OK, await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { ur = (string?)null } }, Ct));
        var after = await _h.GetAsync(_admin, "/api/kernel/labels", Ct);
        Assert.Equal("Section", after.Json.GetProperty("en").GetProperty(key).GetString());
        Assert.False(after.Json.TryGetProperty("ur", out var ur) && ur.TryGetProperty(key, out _));
        Expect(HttpStatusCode.OK, await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { en = "  " } }, Ct));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.labels where key = $1", Ct, key));
    }

    [Fact]
    public async Task FR_KRN_MET_22_a_a_company_text_is_seen_for_that_company_only_and_must_name_a_company_of_the_customer()
    {
        var company = await _h.CompanyAsync(Ct);
        var other = await _h.CompanyAsync(Ct);
        var key = "nav.test_" + Guid.NewGuid().ToString("N")[..8];
        Expect(HttpStatusCode.OK, await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { en = "Wing" } }, Ct));
        Expect(HttpStatusCode.OK, await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "company", companyId = company, texts = new { en = "Branch office" } }, Ct));

        Assert.Equal("Branch office", (await _h.GetAsync(_admin, $"/api/kernel/labels?companyId={company}", Ct)).Json.GetProperty("en").GetProperty(key).GetString());
        Assert.Equal("Wing", (await _h.GetAsync(_admin, $"/api/kernel/labels?companyId={other}", Ct)).Json.GetProperty("en").GetProperty(key).GetString());
        Assert.Equal("errors.labels.unknown_company", Fail(await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "company", companyId = Guid.NewGuid(), texts = new { en = "x" } }, Ct), "companyId"));
        Assert.Equal("errors.labels.company_required", Fail(await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "company", texts = new { en = "x" } }, Ct), "companyId"));
        Assert.Equal("errors.labels.company_not_allowed", Fail(await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", companyId = company, texts = new { en = "x" } }, Ct), "companyId"));
    }

    [Fact]
    public async Task FR_ADM_STU_25_b_a_label_must_be_plain_text_of_1_to_300_characters_for_a_well_formed_key()
    {
        var key = "nav.test_" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal("errors.labels.text_length", Fail(await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { en = new string('x', 301) } }, Ct), "texts.en"));
        Assert.Equal("errors.labels.text_invalid", Fail(await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { en = "two\nlines" } }, Ct), "texts.en"));
        Assert.Equal("errors.labels.text_invalid", Fail(await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { en = 5 } }, Ct), "texts.en"));
        Assert.Equal("errors.labels.invalid_key", Fail(await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key = "bad key;", layer = "tenant", texts = new { en = "x" } }, Ct), "key"));
        Assert.Equal("errors.labels.invalid_layer", Fail(await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "pack", texts = new { en = "x" } }, Ct), "layer"));
        Assert.Equal("errors.labels.texts_required", Fail(await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { } }, Ct), "texts"));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.labels where key = $1", Ct, key));
    }

    [Fact]
    public async Task FR_ADM_STU_25_c_label_changes_need_the_label_permission_and_are_in_the_change_log()
    {
        var reader = await UserWithAsync(["cfg.labels.read", "iam.self.manage"]);
        var key = "nav.test_" + Guid.NewGuid().ToString("N")[..8];

        Expect(HttpStatusCode.OK, await _h.GetAsync(reader, "/api/cfg/label-overrides", Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PutAsync(reader, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { en = "x" } }, Ct));
        Expect(HttpStatusCode.Forbidden, await _h.GetAsync(await UserWithAsync(["iam.self.manage"]), "/api/cfg/label-overrides", Ct));
        Expect(HttpStatusCode.OK, await _h.PutAsync(_admin, "/api/cfg/label-overrides", new { key, layer = "tenant", texts = new { en = "Wing" } }, Ct));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'cfg.labels' and changes::text like '%' || $1 || '%'", Ct, key) > 0);
    }

    // ---------------------------------------------------------------- option lists

    [Fact]
    public async Task FR_KRN_MET_01_a_an_option_list_takes_items_in_order_and_an_item_is_switched_off_never_removed()
    {
        var (list, _) = await ListAsync("a_pos", "b_pos", "o_pos");
        var detail = await _h.GetAsync(_admin, $"/api/cfg/option-lists/{list}", Ct);
        var items = detail.Json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["a_pos", "b_pos", "o_pos"], items.Select(i => i.GetProperty("code").GetString()));
        Assert.Equal([10, 20, 30], items.Select(i => i.GetProperty("seq").GetInt32()));

        var order = await _h.PostAsync(_admin, $"/api/cfg/option-lists/{list}/items/order", new { itemIds = items.AsEnumerable().Reverse().Select(i => i.GetProperty("id").GetGuid()) }, Ct);
        Expect(HttpStatusCode.OK, order);
        Assert.Equal(["o_pos", "b_pos", "a_pos"], order.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()));

        var first = order.Json.GetProperty("items")[0];
        var off = await _h.PutAsync(_admin, $"/api/cfg/option-lists/{list}/items/{first.GetProperty("id").GetGuid()}", new { label = new { en = "O positive", ur = "او مثبت" }, isActive = false, rowVersion = first.GetProperty("rowVersion").GetInt64() }, Ct);
        Expect(HttpStatusCode.OK, off);
        Assert.False(off.Json.GetProperty("items")[0].GetProperty("isActive").GetBoolean());
        Assert.Equal("او مثبت", off.Json.GetProperty("items")[0].GetProperty("label").GetProperty("ur").GetString());
        Expect(HttpStatusCode.MethodNotAllowed, await _h.SendDeleteAsync(_admin, $"/api/cfg/option-lists/{list}/items/{first.GetProperty("id").GetGuid()}"));
        var summary = await _h.GetAsync(_admin, "/api/cfg/option-lists", Ct);
        Assert.Equal(3, summary.Json.GetProperty("items").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == list).GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task FR_KRN_MET_01_b_codes_are_fixed_and_unique_a_stale_edit_is_refused_and_an_order_must_name_every_item()
    {
        var (list, _) = await ListAsync("a_pos", "b_pos");
        var duplicate = await _h.PostAsync(_admin, $"/api/cfg/option-lists/{list}/items", new { code = "a_pos", label = new { en = "again" } }, Ct);
        Expect(HttpStatusCode.Conflict, duplicate);
        Assert.Equal("errors.option_lists.code_taken", Fail(duplicate, "code"));
        Assert.Equal("errors.option_lists.invalid_code", Fail(await _h.PostAsync(_admin, $"/api/cfg/option-lists/{list}/items", new { code = "Bad Code", label = new { en = "x" } }, Ct), "code"));
        Assert.Equal("errors.metadata.name_required", Fail(await _h.PostAsync(_admin, $"/api/cfg/option-lists/{list}/items", new { code = "c_pos", label = new { ur = "صرف اردو" } }, Ct), "label"));
        Assert.Equal("errors.option_lists.order_invalid", Fail(await _h.PostAsync(_admin, $"/api/cfg/option-lists/{list}/items/order", new { itemIds = new[] { Guid.NewGuid() } }, Ct), "itemIds"));

        var detail = await _h.GetAsync(_admin, $"/api/cfg/option-lists/{list}", Ct);
        var edit = new { name = new { en = "Blood group", ur = "خون" }, rowVersion = detail.Json.GetProperty("rowVersion").GetInt64() };
        Expect(HttpStatusCode.OK, await _h.PutAsync(_admin, $"/api/cfg/option-lists/{list}", edit, Ct));
        Expect(HttpStatusCode.Conflict, await _h.PutAsync(_admin, $"/api/cfg/option-lists/{list}", edit, Ct));
        Expect(HttpStatusCode.NotFound, await _h.GetAsync(_admin, $"/api/cfg/option-lists/{Guid.NewGuid()}", Ct));
    }

    [Fact]
    public async Task FR_KRN_MET_01_c_a_system_list_can_be_extended_and_an_item_the_product_ships_cannot_be_switched_off()
    {
        var list = Guid.CreateVersion7();
        var shipped = Guid.CreateVersion7();
        var system = BaselineSeed.SystemUserId;
        await _h.ExecAsync("""
            insert into cfg.option_lists (id, code, name_i18n, is_system, created_at, created_by, updated_at, updated_by)
            values ($1, $2, '{"en":"Exit reasons"}', true, now(), $3, now(), $3)
            """, Ct, list, "sys" + Guid.NewGuid().ToString("N")[..8], system);
        await _h.ExecAsync("""
            insert into cfg.option_list_items (id, list_id, code, label_i18n, seq, is_active, attributes, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'resignation', '{"en":"Resignation"}', 10, true, '{"system":true}', now(), $3, now(), $3)
            """, Ct, shipped, list, system);

        Expect(HttpStatusCode.Created, await _h.PostAsync(_admin, $"/api/cfg/option-lists/{list}/items", new { code = "relocation", label = new { en = "Relocation" } }, Ct));
        var refused = await _h.PutAsync(_admin, $"/api/cfg/option-lists/{list}/items/{shipped}", new { label = new { en = "Resignation" }, isActive = false, rowVersion = 1 }, Ct);
        Expect(HttpStatusCode.BadRequest, refused);
        Assert.Equal("errors.option_lists.system_item", Fail(refused, "isActive"));
        var renamed = await _h.PutAsync(_admin, $"/api/cfg/option-lists/{list}/items/{shipped}", new { label = new { en = "Resignation", ur = "استعفیٰ" }, isActive = true, rowVersion = 1 }, Ct);
        Expect(HttpStatusCode.OK, renamed);
        Assert.True(renamed.Json.GetProperty("isSystem").GetBoolean());
        Expect(HttpStatusCode.MethodNotAllowed, await _h.SendDeleteAsync(_admin, $"/api/cfg/option-lists/{list}"));
    }

    [Fact]
    public async Task FR_KRN_MET_01_d_option_list_changes_need_their_permissions()
    {
        var reader = await UserWithAsync(["cfg.option_lists.read", "iam.self.manage"]);

        Expect(HttpStatusCode.OK, await _h.GetAsync(reader, "/api/cfg/option-lists", Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(reader, "/api/cfg/option-lists", new { code = "x_list", name = new { en = "X" } }, Ct));
        Expect(HttpStatusCode.Forbidden, await _h.GetAsync(await UserWithAsync(["iam.self.manage"]), "/api/cfg/option-lists", Ct));
    }

    // ---------------------------------------------------------------- definitions

    [Fact]
    public async Task FR_KRN_MET_01_e_a_choice_field_names_an_option_list_and_its_key_type_and_list_never_change()
    {
        var (list, _) = await ListAsync("a_pos", "b_pos");
        var def = await DefineAsync("choice", list: list, required: true);

        var edit = await _h.PutAsync(_admin, $"/api/cfg/custom-fields/{def.Id}", new { label = new { en = "Blood group", ur = "خون کا گروپ" }, help = new { en = "As on the card" }, isRequired = false, section = "Medical", seq = 5, rowVersion = def.RowVersion }, Ct);

        Expect(HttpStatusCode.OK, edit);
        Assert.Equal(def.Key, edit.Json.GetProperty("fieldKey").GetString());
        Assert.Equal("choice", edit.Json.GetProperty("dataType").GetString());
        Assert.Equal(list, edit.Json.GetProperty("optionListId").GetGuid());
        Assert.Equal(2, edit.Json.GetProperty("version").GetInt32());
        Assert.Equal("Medical", edit.Json.GetProperty("section").GetString());
        var listed = await _h.GetAsync(_admin, "/api/cfg/custom-fields?entityType=org.org_units", Ct);
        Assert.Contains(listed.Json.GetProperty("items").EnumerateArray(), d => d.GetProperty("id").GetGuid() == def.Id);
        var entities = await _h.GetAsync(_admin, "/api/cfg/custom-field-entities", Ct);
        Assert.Equal(["hrm.employments", "hrm.leave_requests", "hrm.persons", "hrm.positions", "org.org_units"], entities.Json.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("entityType").GetString()));
    }

    [Fact]
    public async Task FR_KRN_MET_05_a_a_definition_is_refused_for_a_bad_key_type_list_limits_default_or_unique_setting()
    {
        var (list, _) = await ListAsync("a_pos");
        object Body(string type, string? key = null, object? validation = null, Guid? optionList = null, bool unique = false, object? defaultValue = null, string entity = "org.org_units") =>
            new { entityType = entity, fieldKey = key ?? Key(), dataType = type, label = new { en = "Label" }, validation, optionListId = optionList, isUnique = unique, defaultValue };

        async Task<string> Refused(object body, string field)
        {
            var answer = await _h.PostAsync(_admin, "/api/cfg/custom-fields", body, Ct);
            Expect(HttpStatusCode.BadRequest, answer);
            return Fail(answer, field);
        }

        Assert.Equal("errors.custom.def.key_invalid", await Refused(Body("text", "Bad-Key"), "fieldKey"));
        Assert.Equal("errors.custom.def.entity_unknown", await Refused(Body("text", entity: "org.companies"), "entityType"));
        Assert.Equal("errors.custom.def.type_not_available", await Refused(Body("file"), "dataType"));
        Assert.Equal("errors.custom.def.type_invalid", await Refused(Body("money"), "dataType"));
        Assert.Equal("errors.custom.def.option_list_required", await Refused(Body("choice"), "optionListId"));
        Assert.Equal("errors.custom.def.option_list_not_for_type", await Refused(Body("text", optionList: list), "optionListId"));
        Assert.Equal("errors.custom.def.option_list_unknown", await Refused(Body("choice", optionList: Guid.NewGuid()), "optionListId"));
        Assert.Equal("errors.custom.def.validation_not_for_type", await Refused(Body("number", validation: new { maxLength = 4 }), "validation"));
        Assert.Equal("errors.custom.def.validation_range", await Refused(Body("number", validation: new { min = 9, max = 1 }), "validation"));
        Assert.Equal("errors.custom.def.pattern_invalid", await Refused(Body("text", validation: new { pattern = "(?<=a)b" }), "validation"));
        Assert.Equal("errors.custom.def.unique_not_for_type", await Refused(Body("bool", unique: true), "isUnique"));
        Assert.Equal("errors.custom.def.unique_default", await Refused(Body("text", unique: true, defaultValue: "same"), "defaultValue"));
        Assert.Equal("errors.custom.def.default_invalid", await Refused(Body("choice", optionList: list, defaultValue: "zzz"), "defaultValue"));
        Assert.Equal("errors.custom.def.default_invalid", await Refused(Body("number", validation: new { max = 5 }, defaultValue: 9), "defaultValue"));
        Assert.Equal("errors.metadata.name_required", await Refused(new { entityType = "org.org_units", fieldKey = Key(), dataType = "text", label = new { ur = "صرف اردو" } }, "label"));

        var def = await DefineAsync("text", key: "dup_" + Guid.NewGuid().ToString("N")[..8]);
        var again = await _h.PostAsync(_admin, "/api/cfg/custom-fields", Body("text", def.Key), Ct);
        Expect(HttpStatusCode.Conflict, again);
        Assert.Equal("errors.custom.def.key_taken", Fail(again, "fieldKey"));
    }

    [Fact]
    public async Task FR_KRN_MET_05_b_definitions_are_retired_and_restored_never_deleted_and_need_the_definition_permissions()
    {
        var def = await DefineAsync("text");
        var retired = await _h.PostAsync(_admin, $"/api/cfg/custom-fields/{def.Id}/retire", new { rowVersion = def.RowVersion }, Ct);
        Expect(HttpStatusCode.OK, retired);
        Assert.Equal("retired", retired.Json.GetProperty("status").GetString());
        var restored = await _h.PostAsync(_admin, $"/api/cfg/custom-fields/{def.Id}/restore", new { rowVersion = retired.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal("active", restored.Json.GetProperty("status").GetString());
        Expect(HttpStatusCode.Conflict, await _h.PostAsync(_admin, $"/api/cfg/custom-fields/{def.Id}/retire", new { rowVersion = def.RowVersion }, Ct));
        Expect(HttpStatusCode.MethodNotAllowed, await _h.SendDeleteAsync(_admin, $"/api/cfg/custom-fields/{def.Id}"));

        var reader = await UserWithAsync(["cfg.custom_field_defs.read", "iam.self.manage"]);
        Expect(HttpStatusCode.OK, await _h.GetAsync(reader, "/api/cfg/custom-fields", Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(reader, $"/api/cfg/custom-fields/{def.Id}/retire", new { rowVersion = 1 }, Ct));
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(reader, "/api/cfg/custom-fields", new { entityType = "org.org_units", fieldKey = Key(), dataType = "text", label = new { en = "x" } }, Ct));
        // The database holds the same line: a key or a type/list mismatch cannot be written by any other path.
        await using var owner = await _h.OwnerAsync(null, Ct);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => AccessHarness.ExecAsync(owner, """
            insert into cfg.custom_field_defs (id, entity_type, field_key, data_type, label_i18n, validation, is_required, is_unique, seq, version, status, created_at, created_by, updated_at, updated_by)
            values ($1, 'org.org_units', 'choice_without_list', 'choice', '{"en":"x"}', '{}', false, false, 1, 1, 'active', now(), $2, now(), $2)
            """, Ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId));
        Assert.Equal("ck_custom_field_defs_option_list", ex.ConstraintName);
    }

    // ---------------------------------------------------------------- values on generated writes

    [Fact]
    public async Task FR_KRN_MET_02_a_a_required_choice_field_is_refused_when_missing_or_not_in_the_list_and_saved_when_given()
    {
        var company = await _h.CompanyAsync(Ct);
        var type = await UnitTypeAsync();
        var (list, _) = await ListAsync("a_pos", "b_pos");
        var blood = await DefineAsync("choice", list: list, required: true);

        var missing = await CreateUnitAsync(_admin, company, type, custom: null);
        var empty = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?>());
        var wrong = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [blood.Key] = "zzz" });
        var typed = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [blood.Key] = 7 });
        var unknown = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [blood.Key] = "a_pos", ["never_defined"] = "x" });
        var fine = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [blood.Key] = "a_pos" });

        Expect(HttpStatusCode.BadRequest, missing);
        Assert.Equal("errors.custom.required", Fail(missing, "custom." + blood.Key));
        Assert.Equal("errors.custom.required", Fail(empty, "custom." + blood.Key));
        Assert.Equal("errors.custom.not_in_list", Fail(wrong, "custom." + blood.Key));
        Assert.Equal("errors.custom.invalid_type", Fail(typed, "custom." + blood.Key));
        Assert.Equal("errors.custom.unknown_field", Fail(unknown, "custom.never_defined"));
        Expect(HttpStatusCode.Created, fine);
        Assert.Equal("a_pos", fine.Json.GetProperty("custom").GetProperty(blood.Key).GetString());
        Assert.Equal("a_pos", await _h.ScalarAsync<string>("select custom ->> $2 from org.org_units where id = $1", Ct, fine.Json.GetProperty("id").GetGuid(), blood.Key));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from org.org_units where company_id = $1 and code <> $2 and not custom ? $3", Ct, company, fine.Json.GetProperty("code").GetString(), blood.Key));
    }

    [Fact]
    public async Task FR_KRN_MET_02_b_every_type_is_checked_on_create_and_update_and_a_default_fills_a_missing_value_on_create_only()
    {
        var company = await _h.CompanyAsync(Ct);
        var type = await UnitTypeAsync();
        var seats = await DefineAsync("number", new { min = 1, max = 50 });
        var budget = await DefineAsync("decimal", new { min = "0" });
        var opened = await DefineAsync("date", new { minDate = "2020-01-01" });
        var remote = await DefineAsync("bool");
        var tag = await DefineAsync("text", new { maxLength = 5, pattern = "[A-Z]+" });
        var level = await DefineAsync("text", defaultValue: "standard");

        var bad = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [seats.Key] = 51, [budget.Key] = "-1", [opened.Key] = "2019-12-31", [remote.Key] = "yes", [tag.Key] = "abcdef" });
        var good = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [seats.Key] = 12, [budget.Key] = 1250.5m, [opened.Key] = "2026-03-01", [remote.Key] = true, [tag.Key] = "ABC" });

        Expect(HttpStatusCode.BadRequest, bad);
        Assert.Equal("errors.custom.too_large", Fail(bad, "custom." + seats.Key));
        Assert.Equal("errors.custom.too_small", Fail(bad, "custom." + budget.Key));
        Assert.Equal("errors.custom.too_small", Fail(bad, "custom." + opened.Key));
        Assert.Equal("errors.custom.invalid_type", Fail(bad, "custom." + remote.Key));
        Assert.Equal("errors.custom.too_long", Fail(bad, "custom." + tag.Key));
        Expect(HttpStatusCode.Created, good);
        var custom = good.Json.GetProperty("custom");
        Assert.Equal(12, custom.GetProperty(seats.Key).GetInt32());
        Assert.Equal("1250.5", custom.GetProperty(budget.Key).GetString());
        Assert.True(custom.GetProperty(remote.Key).GetBoolean());
        Assert.Equal("standard", custom.GetProperty(level.Key).GetString());

        // The default is not applied when a value is given or on update (a cleared field stays cleared).
        var given = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [seats.Key] = 1, [level.Key] = "premium" });
        Assert.Equal("premium", given.Json.GetProperty("custom").GetProperty(level.Key).GetString());
        var cleared = await UpdateUnitAsync(_admin, good.Json, new Dictionary<string, object?> { [seats.Key] = 13 });
        Expect(HttpStatusCode.OK, cleared);
        Assert.False(cleared.Json.GetProperty("custom").TryGetProperty(level.Key, out _));
        Assert.Equal(13, cleared.Json.GetProperty("custom").GetProperty(seats.Key).GetInt32());
    }

    [Fact]
    public async Task FR_KRN_MET_02_c_a_unique_value_is_refused_in_the_same_company_and_allowed_in_another()
    {
        var company = await _h.CompanyAsync(Ct);
        var other = await _h.CompanyAsync(Ct);
        var type = await UnitTypeAsync();
        var badge = await DefineAsync("text", unique: true);
        var value = "BDG-" + Guid.NewGuid().ToString("N")[..6];

        var first = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [badge.Key] = value });
        var same = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [badge.Key] = value.ToLowerInvariant() });
        var elsewhere = await CreateUnitAsync(_admin, other, type, new Dictionary<string, object?> { [badge.Key] = value });
        var kept = await UpdateUnitAsync(_admin, first.Json, new Dictionary<string, object?> { [badge.Key] = value });
        var second = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [badge.Key] = value + "-2" });
        var clash = await UpdateUnitAsync(_admin, second.Json, new Dictionary<string, object?> { [badge.Key] = value });

        Expect(HttpStatusCode.Created, first);
        Expect(HttpStatusCode.BadRequest, same);
        Assert.Equal("errors.custom.duplicate", Fail(same, "custom." + badge.Key));
        Expect(HttpStatusCode.Created, elsewhere);
        Expect(HttpStatusCode.OK, kept);
        Expect(HttpStatusCode.BadRequest, clash);
        Assert.Equal("errors.custom.duplicate", Fail(clash, "custom." + badge.Key));
        var many = await _h.ScalarAsync<long>("select count(*) from org.org_units where company_id = $1 and lower(custom ->> $2) = lower($3)", Ct, company, badge.Key, value);
        Assert.Equal(1, many);
    }

    [Fact]
    public async Task FR_KRN_MET_02_d_two_saves_of_the_same_unique_value_at_once_cannot_both_pass()
    {
        var company = await _h.CompanyAsync(Ct);
        var type = await UnitTypeAsync();
        var badge = await DefineAsync("text", unique: true);
        var value = "RACE-" + Guid.NewGuid().ToString("N")[..6];

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [badge.Key] = value })));

        Assert.Equal(1, answers.Count(a => a.Status == HttpStatusCode.Created));
        Assert.Equal(5, answers.Count(a => a.Status == HttpStatusCode.BadRequest && a.Body.Contains("errors.custom.duplicate", StringComparison.Ordinal)));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from org.org_units where company_id = $1 and custom ->> $2 = $3", Ct, company, badge.Key, value));
    }

    [Fact]
    public async Task FR_KRN_MET_05_c_a_retired_field_keeps_its_values_which_cannot_be_changed_and_is_no_longer_required()
    {
        var company = await _h.CompanyAsync(Ct);
        var type = await UnitTypeAsync();
        var note = await DefineAsync("text", required: true);
        var unit = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [note.Key] = "keep me" });
        Expect(HttpStatusCode.Created, unit);

        Expect(HttpStatusCode.OK, await _h.PostAsync(_admin, $"/api/cfg/custom-fields/{note.Id}/retire", new { rowVersion = note.RowVersion }, Ct));

        var untouched = await UpdateUnitAsync(_admin, unit.Json, new Dictionary<string, object?>());
        var changed = await UpdateUnitAsync(_admin, (await _h.GetAsync(_admin, $"{Units}/{unit.Json.GetProperty("id").GetGuid()}", Ct)).Json, new Dictionary<string, object?> { [note.Key] = "new text" });
        var fresh = await CreateUnitAsync(_admin, company, type, custom: null);

        Expect(HttpStatusCode.OK, untouched);
        Assert.Equal("keep me", untouched.Json.GetProperty("custom").GetProperty(note.Key).GetString());
        Expect(HttpStatusCode.BadRequest, changed);
        Assert.Equal("errors.custom.field_retired", Fail(changed, "custom." + note.Key));
        Expect(HttpStatusCode.Created, fresh);
        Assert.False(fresh.Json.GetProperty("custom").TryGetProperty(note.Key, out _));
        Assert.Equal("keep me", await _h.ScalarAsync<string>("select custom ->> $2 from org.org_units where id = $1", Ct, unit.Json.GetProperty("id").GetGuid(), note.Key));
    }

    [Fact]
    public async Task FR_KRN_MET_02_e_an_option_switched_off_stays_valid_for_the_record_that_holds_it_and_is_refused_for_others()
    {
        var company = await _h.CompanyAsync(Ct);
        var type = await UnitTypeAsync();
        var (list, _) = await ListAsync("a_pos", "b_pos");
        var blood = await DefineAsync("choice", list: list);
        var unit = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [blood.Key] = "a_pos" });
        var detail = (await _h.GetAsync(_admin, $"/api/cfg/option-lists/{list}", Ct)).Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "a_pos");
        Expect(HttpStatusCode.OK, await _h.PutAsync(_admin, $"/api/cfg/option-lists/{list}/items/{detail.GetProperty("id").GetGuid()}", new { label = new { en = "A" }, isActive = false, rowVersion = detail.GetProperty("rowVersion").GetInt64() }, Ct));

        var again = await UpdateUnitAsync(_admin, unit.Json, new Dictionary<string, object?> { [blood.Key] = "a_pos" });
        var other = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [blood.Key] = "a_pos" });

        Expect(HttpStatusCode.OK, again);
        Expect(HttpStatusCode.BadRequest, other);
        Assert.Equal("errors.custom.not_in_list", Fail(other, "custom." + blood.Key));
    }

    // ---------------------------------------------------------------- field security

    [Fact]
    public async Task FR_KRN_MET_02_f_field_rules_on_a_custom_key_hide_it_from_answers_and_refuse_a_change_while_a_required_hidden_field_is_not_demanded()
    {
        var company = await _h.CompanyAsync(Ct);
        var type = await UnitTypeAsync();
        var blood = await DefineAsync("text", required: true);
        var visible = await DefineAsync("text");
        var stored = await CreateUnitAsync(_admin, company, type, new Dictionary<string, object?> { [blood.Key] = "BG-1234", [visible.Key] = "shown" });
        var permissions = new[] { "org.org_units.read", "org.org_units.create", "org.org_units.update", "iam.self.manage" };
        var hidden = await UserWithAsync(permissions, [("org.org_units", "custom." + blood.Key, "hidden")]);
        var readOnly = await UserWithAsync(permissions, [("org.org_units", "custom." + blood.Key, "read_only")]);
        var masked = await UserWithAsync(permissions, [("org.org_units", "custom." + blood.Key, "masked")]);
        var unitId = stored.Json.GetProperty("id").GetGuid();

        var seen = await _h.GetAsync(hidden, $"{Units}/{unitId}", Ct);
        Assert.False(seen.Json.GetProperty("custom").TryGetProperty(blood.Key, out _));
        Assert.Equal("shown", seen.Json.GetProperty("custom").GetProperty(visible.Key).GetString());
        Assert.Equal("****1234", (await _h.GetAsync(masked, $"{Units}/{unitId}", Ct)).Json.GetProperty("custom").GetProperty(blood.Key).GetString());
        Assert.Equal("BG-1234", (await _h.GetAsync(readOnly, $"{Units}/{unitId}", Ct)).Json.GetProperty("custom").GetProperty(blood.Key).GetString());

        var tryHidden = await CreateUnitAsync(hidden, company, type, new Dictionary<string, object?> { [blood.Key] = "O+" });
        var tryReadOnly = await CreateUnitAsync(readOnly, company, type, new Dictionary<string, object?> { [blood.Key] = "O+" });
        var withoutIt = await CreateUnitAsync(hidden, company, type, new Dictionary<string, object?> { [visible.Key] = "ok" });
        var updateKeeps = await UpdateUnitAsync(hidden, (await _h.GetAsync(hidden, $"{Units}/{unitId}", Ct)).Json, new Dictionary<string, object?> { [visible.Key] = "changed" });

        Expect(HttpStatusCode.BadRequest, tryHidden);
        Assert.Equal("errors.access.field_not_editable", Fail(tryHidden, "custom." + blood.Key));
        Expect(HttpStatusCode.BadRequest, tryReadOnly);
        Expect(HttpStatusCode.Created, withoutIt);
        Expect(HttpStatusCode.OK, updateKeeps);
        Assert.Equal("BG-1234", await _h.ScalarAsync<string>("select custom ->> $2 from org.org_units where id = $1", Ct, unitId, blood.Key));
    }

    [Fact]
    public async Task FR_KRN_MET_02_g_the_field_matrix_lists_each_custom_field_with_its_name_and_the_form_read_follows_field_security()
    {
        var company = await _h.CompanyAsync(Ct);
        var hiddenKey = await DefineAsync("text");
        var shownKey = await DefineAsync("text", required: true);
        var role = await _h.RoleAsync(Ct, ["org.org_units.read", "iam.self.manage"], [("org.org_units", "all")], [("org.org_units", "custom." + hiddenKey.Key, "hidden"), ("org.org_units", "custom." + shownKey.Key, "read_only")]);
        var user = await _h.UserAsync(Ct);
        await _h.AssignAsync(user, role, null, _h.Today.AddDays(-1), null, Ct);

        var preview = await _h.GetAsync(_admin, $"/api/iam/roles/{role}/field-preview?resource=org.org_units", Ct);
        var rows = preview.Json.GetProperty("fields").EnumerateArray().ToList();
        var shown = rows.Single(r => r.GetProperty("field").GetString() == "custom." + shownKey.Key);
        Assert.Equal("read_only", shown.GetProperty("access").GetString());
        Assert.Equal("Field " + shownKey.Key, shown.GetProperty("label").GetProperty("en").GetString());
        Assert.Equal("hidden", rows.Single(r => r.GetProperty("field").GetString() == "custom." + hiddenKey.Key).GetProperty("access").GetString());

        var form = await _h.GetAsync(user, $"/api/kernel/custom-fields/org.org_units?companyId={company}", Ct);
        Expect(HttpStatusCode.OK, form);
        var fields = form.Json.GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(fields, f => f.GetProperty("fieldKey").GetString() == hiddenKey.Key);
        var one = fields.Single(f => f.GetProperty("fieldKey").GetString() == shownKey.Key);
        Assert.Equal("readonly", one.GetProperty("access").GetString());
        Assert.True(one.GetProperty("isRequired").GetBoolean());
        Expect(HttpStatusCode.NotFound, await _h.GetAsync(user, "/api/kernel/custom-fields/org.companies", Ct));
        Expect(HttpStatusCode.NotFound, await _h.GetAsync(await UserWithAsync(["iam.self.manage"]), "/api/kernel/custom-fields/org.org_units", Ct));
    }

    // ---------------------------------------------------------------- hand-written writes (HR)

    [Fact]
    public async Task FR_KRN_MET_02_h_a_required_custom_field_on_employments_is_refused_when_missing_at_hire_and_stored_when_given()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var company = await _h.CompanyAsync(Ct);
        var place = await _h.PlaceAsync(company, Ct);
        var (list, _) = await ListAsync("a_pos", "b_pos");
        var key = Key();
        Expect(HttpStatusCode.Created, await _h.PostAsync(_admin, "/api/cfg/custom-fields", new { entityType = "hrm.employments", fieldKey = key, dataType = "choice", label = new { en = "Blood group" }, isRequired = true, optionListId = list }, Ct));
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        object Body(object? custom) => new
        {
            personId = person, companyId = company, employeeType = "permanent", joiningDate = "2026-01-05", custom,
            assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "Officer" },
        };

        var missing = await _h.PostAsync(hr, "/api/hrm/employments", Body(null), Ct);
        var wrong = await _h.PostAsync(hr, "/api/hrm/employments", Body(new Dictionary<string, object?> { [key] = "zzz" }), Ct);
        var fine = await _h.PostAsync(hr, "/api/hrm/employments", Body(new Dictionary<string, object?> { [key] = "b_pos" }), Ct);

        Expect(HttpStatusCode.BadRequest, missing);
        Assert.Equal("errors.custom.required", Fail(missing, "custom." + key));
        Assert.Equal("errors.custom.not_in_list", Fail(wrong, "custom." + key));
        Expect(HttpStatusCode.Created, fine);
        Assert.Equal("b_pos", await _h.ScalarAsync<string>("select custom ->> $2 from hrm.employments where id = $1", Ct, fine.Json.GetProperty("id").GetGuid(), key));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.employments where company_id = $1", Ct, company));
    }

    [Fact]
    public async Task FR_KRN_MET_02_i_custom_values_of_a_person_are_checked_on_create_and_update_and_an_employee_field_must_name_an_employment()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var number = Key();
        var buddy = Key();
        Expect(HttpStatusCode.Created, await _h.PostAsync(_admin, "/api/cfg/custom-fields", new { entityType = "hrm.persons", fieldKey = number, dataType = "number", label = new { en = "Shoe size" }, validation = new { min = 20, max = 60 } }, Ct));
        Expect(HttpStatusCode.Created, await _h.PostAsync(_admin, "/api/cfg/custom-fields", new { entityType = "hrm.persons", fieldKey = buddy, dataType = "employee", label = new { en = "Buddy" } }, Ct));
        var employment = (Guid)(await _h.RowAsync("hrm.employments", await _h.CompanyAsync(Ct), null, Ct))["id"]!;
        object Person(object custom) => new { firstName = "Custom", lastName = "Person" + Guid.NewGuid().ToString("N")[..6], gender = "female", dateOfBirth = "1990-04-01", custom };

        var bad = await _h.PostAsync(hr, "/api/hrm/persons", Person(new Dictionary<string, object?> { [number] = 99 }), Ct);
        var ghost = await _h.PostAsync(hr, "/api/hrm/persons", Person(new Dictionary<string, object?> { [buddy] = Guid.NewGuid() }), Ct);
        var fine = await _h.PostAsync(hr, "/api/hrm/persons", Person(new Dictionary<string, object?> { [number] = 42, [buddy] = employment }), Ct);

        Expect(HttpStatusCode.BadRequest, bad);
        Assert.Equal("errors.custom.too_large", Fail(bad, "custom." + number));
        Assert.Equal("errors.custom.unknown_reference", Fail(ghost, "custom." + buddy));
        Expect(HttpStatusCode.Created, fine);
        var id = fine.Json.GetProperty("id").GetGuid();
        Assert.Equal("42", await _h.ScalarAsync<string>("select custom ->> $2 from hrm.persons where id = $1", Ct, id, number));

        // An update without a custom object leaves the values; one with a bad value is refused and nothing changes.
        var version = fine.Json.GetProperty("rowVersion").GetInt64();
        var rename = await _h.PutAsync(hr, $"/api/hrm/persons/{id}", new { firstName = "Renamed", rowVersion = version }, Ct);
        Expect(HttpStatusCode.OK, rename);
        Assert.Equal("42", await _h.ScalarAsync<string>("select custom ->> $2 from hrm.persons where id = $1", Ct, id, number));
        var worse = await _h.PutAsync(hr, $"/api/hrm/persons/{id}", new { custom = new Dictionary<string, object?> { [number] = 5 }, rowVersion = rename.Json.GetProperty("rowVersion").GetInt64() }, Ct);
        Expect(HttpStatusCode.BadRequest, worse);
        Assert.Equal("errors.custom.too_small", Fail(worse, "custom." + number));
        Assert.Equal("42", await _h.ScalarAsync<string>("select custom ->> $2 from hrm.persons where id = $1", Ct, id, number));
    }
}
