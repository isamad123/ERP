#pragma warning disable CA1861 // literal test data arrays, each used once
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Packs;
using static Erp.IntegrationTests.Packs.PackTestSupport;

namespace Erp.IntegrationTests.Packs;

/// <summary>
/// The pack loader (slice S1.23b, Build items 7 to 12) against a real control plane, real customer databases, the real API pipeline and
/// the real access engine: FR-KRN-CFG-01 (signed, versioned packs with a manifest, compatible versions, dependencies and merge rules),
/// FR-KRN-CFG-02 and FR-HRM-POL-06 (install and upgrade keep the customer's changes, report conflicts with both values, never touch
/// transactional rows), FR-ADM-PKG-01 (pack manager), FR-KRN-TEN-02 (provisioning installs the selected packs), FR-ADM-ONB-01 (the
/// wizard's pack step), GHR-07 (pack policies per company of the pack's country) and FR-XM-32 (nobody approves their own install).
/// Each test has customers of its own.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PackTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Packs = "/api/cfg/packs";
    private AccessHarness _h = null!;
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    /// <summary>The company's date (Asia/Karachi) on the harness clock; the harness's own <c>Today</c> is the UTC date, a day behind after 19:00 UTC.</summary>
    private DateOnly CompanyToday => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);


    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await KeyAsync(fx);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(string Code, Guid Admin)> CustomerAsync(string prefix)
    {
        var code = await _h.ProvisionAsync(prefix + Guid.NewGuid().ToString("N")[..4], Ct);
        return (code, await _h.UserAsync(Ct, code, "tenant_admin"));
    }

    private async Task<long> CountAsync(string code, string sql, params object[] values)
    {
        await using var c = await _h.OwnerAsync(code, Ct);
        return await AccessHarness.ScalarAsync<long>(c, sql, Ct, values);
    }

    private async Task<string?> TextAsync(string code, string sql, params object[] values)
    {
        await using var c = await _h.OwnerAsync(code, Ct);
        return await AccessHarness.ScalarAsync<string?>(c, sql, Ct, values);
    }

    // ------------------------------------------------------------------ FR-KRN-TEN-02: provisioning installs the selected packs

    [Fact]
    public async Task FR_KRN_TEN_02_FR_KRN_CFG_01_a_signed_pack_installs_on_a_fresh_customer_through_provisioning()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0", Content(id));
        var code = "q" + Guid.NewGuid().ToString("N")[..5] + fx.Suffix;
        fx.Track(code);
        var provisioner = new TenantProvisioner(fx.Options, fx.Secrets, fx.Clock, fx.Migrations) { PackHandlers = Handlers };
        var result = await provisioner.ProvisionAsync(fx.Admin, fx.Request(code) with { Packs = [id] }, null, Ct);
        Assert.False(result.AlreadyProvisioned);

        Assert.Equal("1.0.0", await TextAsync(code, "select version from cfg.installed_packs where pack_id = $1 and status = 'installed'", id));
        Assert.Equal(8, await CountAsync(code, "select count(*) from cfg.pack_items where pack_id = $1 and not is_overridden and applied_hash is not null", id));
        Assert.Equal(2, await CountAsync(code, "select count(*) from cfg.option_list_items i join cfg.option_lists l on l.id = i.list_id where l.code = $1 and l.is_system", Code(id)));
        Assert.Equal("7", await TextAsync(code, "select value::text from cfg.setting_values where setting_key = 'cfg.numbering.fiscal_year_start_month' and layer = 'pack'"));
        Assert.Equal(1, await CountAsync(code, "select count(*) from ref.calendars c join ref.holidays h on h.calendar_id = c.id where c.code = $1 and c.source = 'pack'", Code(id) + "-cal"));
        Assert.Equal(1, await CountAsync(code, "select count(*) from ref.banks where country_code = 'PK' and code = $1 and swift_code = 'TESTPKKA'", Code(id)));
        Assert.Equal(1, await CountAsync(code, "select count(*) from cfg.labels where key = $1 and layer = 'pack' and language = 'en'", "labels.test." + Code(id)));
        // Every install is audited: the installed pack and each object it created (FR-KRN-CFG-02 "audited").
        Assert.Equal(1, await CountAsync(code, "select count(*) from aud.change_log where entity_type = 'cfg.installed_packs' and action = 'create'"));
        Assert.True(await CountAsync(code, "select count(*) from aud.change_log where entity_type in ('cfg.option_list_items', 'ref.holidays', 'ref.banks', 'ref.calendars') and changes @> '[{\"field\":\"source\",\"after\":\"pack\"}]'") >= 5);
        await using var cp = await fx.OpenControlPlaneAsync(Ct);
        Assert.Equal(1, await TenancyFixture.CountAsync(cp, "select count(*) from cp.platform_audit a join cp.tenants t on t.id = a.tenant_id where t.code = $1 and a.action = 'pack.install'", Ct, code));
    }

    [Fact]
    public async Task FR_KRN_TEN_02_provisioning_refuses_a_pack_the_entitlement_does_not_license()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0", Content(id), entitlement: "pack.country.sa");
        var code = "r" + Guid.NewGuid().ToString("N")[..5] + fx.Suffix;
        fx.Track(code);
        var provisioner = new TenantProvisioner(fx.Options, fx.Secrets, fx.Clock, fx.Migrations) { PackHandlers = Handlers };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.ProvisionAsync(fx.Admin, fx.Request(code) with { Packs = [id] }, null, Ct));
        Assert.Contains("pack.country.sa", error.Message, StringComparison.Ordinal);
        Assert.Equal("provisioning", (await fx.TenantAsync(code, Ct)).Status);
    }

    // ------------------------------------------------------------------ FR-KRN-CFG-01: signatures and hashes

    [Fact]
    public async Task FR_KRN_CFG_01_a_pack_with_one_changed_byte_or_an_unknown_or_revoked_key_is_refused()
    {
        var id = NewId();
        var signed = await SignedAsync(fx, Source(fx, id, "1.0.0", Content(id)));

        // One changed byte in an object file: its hash no longer matches the signed manifest.
        var tampered = Rewrite(signed, e => e.StartsWith("objects/bank/", StringComparison.Ordinal), bytes => { bytes[^3] ^= 0x01; return bytes; });
        var hash = Assert.Throws<InvalidDataException>(() => PackArchive.Open(tampered));
        Assert.Contains("does not match its hash", hash.Message, StringComparison.Ordinal);

        // A changed manifest (a hash replaced to cover a changed file): the signature no longer verifies.
        var forged = Rewrite(signed, e => e == PackArchive.ManifestFile, bytes => System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace("Release 1.0.0", "Release 1.0.X", StringComparison.Ordinal)));
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => PackCatalog.PublishAsync(fx.Options.ConnectionString, fx.Admin, forged, fx.Clock, Ct));
        Assert.Contains("signature is not valid", bad.Message, StringComparison.Ordinal);

        // Signed by a key the platform does not know.
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var unknown = await SignedAsync(fx, Source(fx, id, "1.0.0", Content(id)), stranger, "unknown-" + fx.Suffix);
        Assert.Contains("not a registered pack key", (await Assert.ThrowsAsync<InvalidOperationException>(() => PackCatalog.PublishAsync(fx.Options.ConnectionString, fx.Admin, unknown, fx.Clock, Ct))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_KRN_CFG_01_a_published_pack_whose_key_was_revoked_afterwards_is_refused_at_install()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyId = "rev-" + Guid.NewGuid().ToString("N")[..8];
        await new Erp.Kernel.Infrastructure.ControlPlane.Platform.ControlPlaneAdmin(fx.Options, fx.Secrets, fx.Clock).AddSigningKeyAsync(fx.Admin, keyId, "pack", key.ExportSubjectPublicKeyInfo(), Ct);
        await using (var cp = await fx.OpenControlPlaneAsync(Ct))
        {
            await TenancyFixture.ExecAsync(cp, "update cp.signing_keys set valid_from = valid_from - interval '1 day' where key_id = $1", Ct, keyId);
        }

        var id = NewId();
        var file = await SignedAsync(fx, Source(fx, id, "1.0.0", Content(id)), key, keyId);
        await PackCatalog.PublishAsync(fx.Options.ConnectionString, fx.Admin, file, fx.Clock, Ct);
        await using (var cp = await fx.OpenControlPlaneAsync(Ct))
        {
            await TenancyFixture.ExecAsync(cp, "update cp.signing_keys set revoked_at = now() where key_id = $1", Ct, keyId);
        }

        var (code, admin) = await CustomerAsync("v");
        var refused = await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("errors.packs.signature", refused.Body, StringComparison.Ordinal);
        Assert.Contains("revoked", refused.Body, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(code, "select count(*) from cfg.installed_packs where pack_id = $1", id));
    }

    // ------------------------------------------------------------------ FR-KRN-CFG-02 / FR-HRM-POL-06: upgrade keeps the customer's values

    [Fact]
    public async Task FR_KRN_CFG_02_FR_HRM_POL_06_an_upgrade_keeps_the_customers_two_changes_lists_both_values_and_adds_the_new_holiday()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0", Content(id));
        var (code, admin) = await CustomerAsync("u");
        var installed = await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code);
        Assert.True(installed.Status == HttpStatusCode.OK, installed.Body);
        Assert.Equal(8, installed.Json.GetProperty("result").GetProperty("added").GetInt32());

        // The customer renames a pack option-list item and changes a pack setting default (tenant layer).
        var lists = await _h.GetAsync(admin, "/api/cfg/option-lists", Ct, code);
        var list = lists.Json.GetProperty("items").EnumerateArray().Single(l => l.GetProperty("code").GetString() == Code(id));
        var detail = await _h.GetAsync(admin, "/api/cfg/option-lists/" + list.GetProperty("id").GetGuid(), Ct, code);
        var spouse = detail.Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "spouse");
        var renamed = await _h.PutAsync(admin, $"/api/cfg/option-lists/{list.GetProperty("id").GetGuid()}/items/{spouse.GetProperty("id").GetGuid()}",
            new { label = new { en = "Husband or wife", ur = "شوہر یا بیوی" }, rowVersion = spouse.GetProperty("rowVersion").GetInt64() }, Ct, code);
        Assert.True(renamed.Status == HttpStatusCode.OK, renamed.Body);
        var setting = await _h.PutAsync(admin, "/api/cfg/settings/cfg.numbering.fiscal_year_start_month", new { layer = "tenant", value = 1, reason = "calendar year" }, Ct, code);
        Assert.True(setting.Status == HttpStatusCode.OK, setting.Body);

        // Version 1.0.1 changes both, adds a holiday and drops the bank.
        var next = Content(id, fiscalMonth: 8, itemLabel: "Spouse (married)")
            .Where(o => o.Type != "bank")
            .Append(new TestObject("holiday", Code(id) + "-cal:2031-03-23", new { name = new { en = "Pakistan Day", ur = "یومِ پاکستان" }, kind = "public" }))
            .ToList();
        await PublishAsync(fx, id, "1.0.1", next, notes: "Adds Pakistan Day");

        var overview = await _h.GetAsync(admin, Packs, Ct, code);
        var pack = overview.Json.GetProperty("installed").EnumerateArray().Single(p => p.GetProperty("packId").GetString() == id);
        Assert.Equal(2, pack.GetProperty("overridden").GetInt32());
        Assert.Equal("1.0.1", pack.GetProperty("updates")[0].GetProperty("version").GetString());
        Assert.Equal("Adds Pakistan Day", pack.GetProperty("updates")[0].GetProperty("releaseNotes").GetProperty("en").GetString());

        var upgraded = await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { version = "1.0.1" }, Ct, code);
        Assert.True(upgraded.Status == HttpStatusCode.OK, upgraded.Body);
        var report = upgraded.Json.GetProperty("result");
        Assert.Equal(("1.0.1", "1.0.0"), (report.GetProperty("version").GetString(), report.GetProperty("previousVersion").GetString()));
        Assert.Equal(2, report.GetProperty("kept").GetInt32());
        Assert.Equal(1, report.GetProperty("added").GetInt32());
        Assert.Equal(1, report.GetProperty("retired").GetInt32());

        // The customer's values stay; the new holiday is there; the bank the pack dropped is switched off, not deleted.
        Assert.Equal("Husband or wife", await TextAsync(code, "select i.label_i18n->>'en' from cfg.option_list_items i join cfg.option_lists l on l.id = i.list_id where l.code = $1 and i.code = 'spouse'", Code(id)));
        Assert.Equal("1", await TextAsync(code, "select value::text from cfg.setting_values where setting_key = 'cfg.numbering.fiscal_year_start_month' and layer = 'tenant'"));
        Assert.Equal("7", await TextAsync(code, "select value::text from cfg.setting_values where setting_key = 'cfg.numbering.fiscal_year_start_month' and layer = 'pack'"));
        Assert.Equal(1, await CountAsync(code, "select count(*) from ref.holidays h join ref.calendars c on c.id = h.calendar_id where c.code = $1 and h.holiday_date = '2031-03-23'", Code(id) + "-cal"));
        Assert.Equal(1, await CountAsync(code, "select count(*) from ref.banks where code = $1 and not is_active", Code(id)));
        Assert.Equal("1.0.1", await TextAsync(code, "select version from cfg.installed_packs where pack_id = $1", id));

        // The diff view lists both conflicts with the pack's new value and the customer's value.
        var items = await _h.GetAsync(admin, $"{Packs}/{id}/items?changed=true", Ct, code);
        Assert.True(items.Status == HttpStatusCode.OK, items.Body);
        var conflicts = items.Json.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("conflictVersion").GetString() == "1.0.1").ToList();
        Assert.Equal(["option_list_item", "setting"], conflicts.Select(c => c.GetProperty("type").GetString()).Order(StringComparer.Ordinal).ToArray());
        var item = conflicts.Single(c => c.GetProperty("type").GetString() == "option_list_item");
        Assert.Equal("Husband or wife", item.GetProperty("customerValue").GetProperty("label").GetProperty("en").GetString());
        Assert.Equal("Spouse (married)", item.GetProperty("conflictValue").GetProperty("label").GetProperty("en").GetString());
        var month = conflicts.Single(c => c.GetProperty("type").GetString() == "setting");
        Assert.Equal((1, 8), (month.GetProperty("customerValue").GetProperty("value").GetInt32(), month.GetProperty("conflictValue").GetProperty("value").GetInt32()));

        // "Take the pack value" (ruling Q7): an ordinary audited change; the conflict is resolved.
        var taken = await _h.PostAsync(admin, $"{Packs}/items/{item.GetProperty("id").GetGuid()}/take-pack-value", new { reason = "agreed" }, Ct, code);
        Assert.True(taken.Status == HttpStatusCode.OK, taken.Body);
        Assert.Equal("Spouse (married)", await TextAsync(code, "select i.label_i18n->>'en' from cfg.option_list_items i join cfg.option_lists l on l.id = i.list_id where l.code = $1 and i.code = 'spouse'", Code(id)));
        var takeMonth = await _h.PostAsync(admin, $"{Packs}/items/{month.GetProperty("id").GetGuid()}/take-pack-value", new { }, Ct, code);
        Assert.True(takeMonth.Status == HttpStatusCode.OK, takeMonth.Body);
        Assert.Equal(0, await CountAsync(code, "select count(*) from cfg.setting_values where setting_key = 'cfg.numbering.fiscal_year_start_month' and layer = 'tenant'"));
        Assert.Equal("8", await TextAsync(code, "select value::text from cfg.setting_values where setting_key = 'cfg.numbering.fiscal_year_start_month' and layer = 'pack'"));
        var after = await _h.GetAsync(admin, $"{Packs}/{id}/items?changed=true", Ct, code);
        Assert.Equal(0, after.Json.GetProperty("total").GetInt32());
        Assert.Equal(2, await CountAsync(code, "select count(*) from aud.change_log where entity_type = 'cfg.pack_items' and changes @> '[{\"field\":\"taken_pack_value\"}]' and actor_user_id = $1", admin));
    }

    [Fact]
    public async Task FR_KRN_CFG_02_an_upgrade_refuses_an_older_version_an_incompatible_one_and_a_missing_dependency()
    {
        var id = NewId();
        await PublishAsync(fx, id, "2.0.0", Content(id));
        var (code, admin) = await CustomerAsync("w");
        var first = await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code);
        Assert.True(first.Status == HttpStatusCode.OK, first.Body);
        Assert.Equal(HttpStatusCode.Conflict, (await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code)).Status); // already installed
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { version = "9.9.9" }, Ct, code)).Status);

        // A newer version for another product release is published but not installable here.
        var later = Source(fx, id, "2.1.0", Content(id), compatibleFrom: "1.1");
        await PackCatalog.PublishAsync(fx.Options.ConnectionString, fx.Admin, await SignedAsync(fx, later), fx.Clock, Ct);
        var incompatible = await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { version = "2.1.0" }, Ct, code);
        Assert.Equal(HttpStatusCode.Conflict, incompatible.Status);
        Assert.Contains("errors.packs.incompatible", incompatible.Body, StringComparison.Ordinal);

        // A pack that needs another pack installed first.
        var dependent = NewId();
        var folder = Source(fx, dependent, "1.0.0", [new TestObject("label", "labels.test." + Code(dependent), new { texts = new { en = "Dependent" } })],
            dependencies: [new { id = NewId(), from = "1.0.0" }]);
        await PackCatalog.PublishAsync(fx.Options.ConnectionString, fx.Admin, await SignedAsync(fx, folder), fx.Clock, Ct);
        var missing = await _h.PostAsync(admin, Packs + "/install", new { packId = dependent }, Ct, code);
        Assert.Equal(HttpStatusCode.Conflict, missing.Status);
        Assert.Contains("errors.packs.dependency_missing", missing.Body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ FR-ADM-PKG-01, FR-ADM-ONB-01: manager, wizard step, licence, permissions

    [Fact]
    public async Task FR_ADM_PKG_01_FR_ADM_ONB_01_the_manager_lists_available_packs_and_the_wizard_step_is_done_once_one_is_installed()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0", Content(id));
        var unlicensed = NewId();
        await PublishAsync(fx, unlicensed, "1.0.0", Content(unlicensed), entitlement: "pack.country.sa");
        var (code, admin) = await CustomerAsync("m");

        var before = await _h.GetAsync(admin, "/api/cfg/readiness/", Ct, code);
        Assert.True(before.Status == HttpStatusCode.OK, before.Body);
        Assert.Contains("\"step_packs\"", before.Body, StringComparison.Ordinal);
        Assert.Equal("pending", Step(before.Json, "step_packs").GetProperty("status").GetString());

        var overview = await _h.GetAsync(admin, Packs, Ct, code);
        Assert.True(overview.Status == HttpStatusCode.OK, overview.Body);
        Assert.Equal("1.0", overview.Json.GetProperty("productVersion").GetString());
        var available = overview.Json.GetProperty("available").EnumerateArray().ToDictionary(p => p.GetProperty("packId").GetString()!, p => p);
        Assert.True(available[id].GetProperty("licensed").GetBoolean());
        Assert.False(available[unlicensed].GetProperty("licensed").GetBoolean());
        Assert.Contains(available[id].GetProperty("contents").EnumerateArray(), c => c.GetProperty("type").GetString() == "holiday" && c.GetProperty("count").GetInt32() == 1);

        var refused = await _h.PostAsync(admin, Packs + "/install", new { packId = unlicensed }, Ct, code);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("errors.packs.not_licensed", refused.Body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code)).Status);
        var after = await _h.GetAsync(admin, "/api/cfg/readiness/", Ct, code);
        Assert.Equal("done", Step(after.Json, "step_packs").GetProperty("status").GetString());
        var installed = (await _h.GetAsync(admin, Packs, Ct, code)).Json.GetProperty("installed").EnumerateArray().Single(p => p.GetProperty("packId").GetString() == id);
        Assert.Equal(("1.0.0", true, 0, 0), (installed.GetProperty("version").GetString(), installed.GetProperty("compatible").GetBoolean(),
            installed.GetProperty("overridden").GetInt32(), installed.GetProperty("conflicts").GetInt32()));
    }

    [Fact]
    public async Task FR_ADM_PKG_01_only_holders_of_the_pack_permissions_read_install_and_upgrade()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0", Content(id));
        var (code, _) = await CustomerAsync("p");
        var business = await _h.UserAsync(Ct, code, "business_user");
        var company = await _h.UserAsync(Ct, code, "company_admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(business, Packs, Ct, code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(business, $"{Packs}/{id}/items", Ct, code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(company, Packs + "/install", new { packId = id }, Ct, code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(company, $"{Packs}/{id}/upgrade", new { }, Ct, code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(company, $"{Packs}/items/{Guid.NewGuid()}/take-pack-value", new { }, Ct, code)).Status);
        Assert.Equal(0, await CountAsync(code, "select count(*) from cfg.installed_packs"));
    }

    // ------------------------------------------------------------------ FR-XM-32: maker-checker on installs

    [Fact]
    public async Task FR_ADM_PKG_01_FR_XM_32_with_approval_on_an_install_waits_for_a_second_person_who_is_not_the_requester()
    {
        var id = NewId();
        await PublishAsync(fx, id, "1.0.0", Content(id));
        var (code, admin) = await CustomerAsync("a");
        var second = await _h.UserAsync(Ct, code, "tenant_admin");
        var on = await _h.PutAsync(admin, "/api/cfg/settings/cfg.packs.requires_approval", new { layer = "tenant", value = true, reason = "four eyes" }, Ct, code);
        Assert.True(on.Status == HttpStatusCode.Accepted, on.Body); // changing the setting itself waits for a second person
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{on.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct, code)).Status);

        var held = await _h.PostAsync(admin, Packs + "/install", new { packId = id, reason = "country content" }, Ct, code);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(0, await CountAsync(code, "select count(*) from cfg.installed_packs where pack_id = $1", id));
        var pending = (await _h.GetAsync(admin, Packs, Ct, code)).Json.GetProperty("pending");
        Assert.Equal(("install", id, true), (pending[0].GetProperty("action").GetString(), pending[0].GetProperty("packId").GetString(), pending[0].GetProperty("mine").GetBoolean()));

        var own = await _h.PostAsync(admin, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct, code);
        Assert.NotEqual(HttpStatusCode.OK, own.Status);
        var approved = await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "agreed" }, Ct, code);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal("1.0.0", await TextAsync(code, "select version from cfg.installed_packs where pack_id = $1", id));
        Assert.Equal(1, await CountAsync(code, "select count(*) from aud.change_log where entity_type = 'cfg.installed_packs' and actor_user_id = $1", second));
    }

    // ------------------------------------------------------------------ GHR-07, FR-HRM-POL-04/06: pack policies per company of the country

    [Fact]
    public async Task GHR_07_FR_HRM_POL_06_pack_policies_are_created_per_company_of_the_country_and_a_customer_version_is_kept_on_upgrade()
    {
        var id = NewId();
        var policy = new TestObject("policy", "test_leave:PACK_" + Code(id)[..8].ToUpperInvariant(), new { name = new { en = "Pack leave", ur = "پیک رخصت" }, parameters = new { days = 10 } });
        await PublishAsync(fx, id, "1.0.0", [policy]);
        var (code, admin) = await CustomerAsync("h");
        var hr = await _h.UserAsync(Ct, code, "hr_admin");
        await using (var c = await _h.OwnerAsync(code, Ct))
        {
            await AccessHarness.ExecAsync(c, """
                insert into org.companies (id, code, name_i18n, legal_name, country_code, base_currency_code, default_language, cost_center_model, status, created_at, created_by, updated_at, updated_by)
                values ($1, 'SA1', '{"en":"Saudi company"}', 'Saudi company', 'SA', 'PKR', 'en', 'none', 'active', now(), $2, now(), $2)
                """, Ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId);
        }

        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code)).Status);
        Assert.Equal(1, await CountAsync(code, "select count(*) from hrm.policies p join org.companies c on c.id = p.company_id where p.source = 'pack' and btrim(c.country_code) = 'PK'"));
        Assert.Equal(0, await CountAsync(code, "select count(*) from hrm.policies p join org.companies c on c.id = p.company_id where btrim(c.country_code) = 'SA'"));
        var policyId = Guid.Parse((await TextAsync(code, "select id::text from hrm.policies where source = 'pack'"))!);
        Assert.Equal("{\"days\": 10}", await TextAsync(code, "select parameters::text from hrm.policy_versions where policy_id = $1 and status = 'active'", policyId));

        // HR overrides the pack's value with a version of their own (S1.23a: is_overridden), from tomorrow.
        var draft = await _h.PostAsync(hr, $"/api/hrm/policies/{policyId}/versions", new { parameters = new { days = 15 } }, Ct, code);
        Assert.True(draft.Status == HttpStatusCode.Created, draft.Body);
        var activated = await _h.PostAsync(hr, $"/api/hrm/policy-versions/{draft.Json.GetProperty("id").GetGuid()}/activate",
            new { effectiveFrom = Policies.PolicyTestSupport.Iso(CompanyToday.AddDays(1)), rowVersion = draft.Json.GetProperty("rowVersion").GetInt64() }, Ct, code);
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Body);
        _h.Clock.Advance(TimeSpan.FromDays(2));
        try
        {
            await PublishAsync(fx, id, "1.0.1", [policy with { Value = new { name = new { en = "Pack leave", ur = "پیک رخصت" }, parameters = new { days = 12 } } }]);
            var upgraded = await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { }, Ct, code);
            Assert.True(upgraded.Status == HttpStatusCode.OK, upgraded.Body);
            Assert.Equal(1, upgraded.Json.GetProperty("result").GetProperty("kept").GetInt32());
            Assert.Equal("{\"days\": 15}", await TextAsync(code, "select parameters::text from hrm.policy_versions where policy_id = $1 and status = 'active' and valid_to is null", policyId));
        }
        finally
        {
            _h.Clock.Reset();
        }
    }

    [Fact]
    public async Task FR_KRN_CFG_02_an_unchanged_pack_policy_takes_the_new_version_from_the_upgrade_date()
    {
        var id = NewId();
        var key = "test_leave:PK_" + Code(id)[..8].ToUpperInvariant();
        await PublishAsync(fx, id, "1.0.0", [new TestObject("policy", key, new { name = new { en = "Pack leave" }, parameters = new { days = 10 } })]);
        var (code, admin) = await CustomerAsync("g");
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(admin, Packs + "/install", new { packId = id }, Ct, code)).Status);
        await PublishAsync(fx, id, "1.0.1", [new TestObject("policy", key, new { name = new { en = "Pack leave" }, parameters = new { days = 12 } })]);
        var upgraded = await _h.PostAsync(admin, $"{Packs}/{id}/upgrade", new { }, Ct, code);
        Assert.True(upgraded.Status == HttpStatusCode.OK, upgraded.Body);
        Assert.Equal(1, upgraded.Json.GetProperty("result").GetProperty("updated").GetInt32());
        // A version in force is never edited: version 1 keeps 10 days, version 2 (12 days) starts the next day (same-day upgrade).
        Assert.Equal(2, await CountAsync(code, "select count(*) from hrm.policy_versions v join hrm.policies p on p.id = v.policy_id where p.source = 'pack' and v.status = 'active'"));
        Assert.Equal("{\"days\": 10}", await TextAsync(code, "select v.parameters::text from hrm.policy_versions v join hrm.policies p on p.id = v.policy_id where p.source = 'pack' and v.version = 1"));
        Assert.Equal("{\"days\": 12}", await TextAsync(code, "select v.parameters::text from hrm.policy_versions v join hrm.policies p on p.id = v.policy_id where p.source = 'pack' and v.version = 2"));
    }

    private static JsonElement Step(JsonElement overview, string key) =>
        overview.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("taskKey").GetString() == key);

    private static byte[] Rewrite(byte[] pack, Func<string, bool> which, Func<byte[], byte[]> change)
    {
        using var source = new ZipArchive(new MemoryStream(pack), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var done = false;
            foreach (var entry in source.Entries)
            {
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                var bytes = copy.ToArray();
                if (!done && which(entry.FullName))
                {
                    bytes = change(bytes);
                    done = true;
                }

                using var target = zip.CreateEntry(entry.FullName).Open();
                target.Write(bytes);
            }
        }

        return output.ToArray();
    }
}
