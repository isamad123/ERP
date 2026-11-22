#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.Org;

/// <summary>
/// Organization set-up (slice S1.14a, CR-070): a new company with its numbering schemes and company limit, registrations and
/// bank accounts with masking, access log and maker-checker, location checks and the holiday year copy, through the real
/// API pipeline, the real access engine and a real PostgreSQL.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class OrganizationAdminTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static object NewCompany(string code, string currency = "USD") => new
    {
        code,
        nameI18n = new { en = "Company " + code, ur = "کمپنی " + code },
        legalName = "Company " + code + " (Private) Limited",
        countryCode = "PK",
        baseCurrencyCode = currency,
        defaultLanguage = "en",
        costCenterModel = "none",
        status = "active",
        isActive = true,
    };

    private static void Expect(HttpStatusCode expected, Erp.IntegrationTests.Identity.Answer answer) =>
        Assert.True(answer.Status == expected, $"expected {expected}, got {answer.Status}: {answer.Body}");

    private async Task<T> InAsync<T>(string? code, string sql, params object[] values)
    {
        await using var c = await _h.OwnerAsync(code, Ct);
        return await AccessHarness.ScalarAsync<T>(c, sql, Ct, values);
    }

    /// <summary>Reference rows the country pack will install (S1.23): a bank and a second currency for the tests.</summary>
    private async Task ReferenceDataAsync(string? code)
    {
        await using var owner = await _h.OwnerAsync(code, Ct);
        await AccessHarness.ExecAsync(owner, """
            insert into ref.currencies (code, name_i18n, symbol, decimals, is_active) values ('USD', '{"en":"US dollar"}', '$', 2, true) on conflict do nothing
            """, Ct);
        await AccessHarness.ExecAsync(owner, """
            insert into ref.banks (id, code, name_i18n, country_code, created_at, created_by, updated_at, updated_by)
            values ($1, 'TESTBANK', '{"en":"Test Bank"}', 'PK', now(), $2, now(), $2) on conflict do nothing
            """, Ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId);
    }

    private async Task SetMakerCheckerAsync(string? code, bool on)
    {
        await using var owner = await _h.OwnerAsync(code, Ct);
        await AccessHarness.ExecAsync(owner, """
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, 'iam.maker_checker.sensitive_roles', 'tenant', null, $2::jsonb, now(), $3, now(), $3)
            on conflict (setting_key, layer, scope_id) do update set value = excluded.value
            """, Ct, Guid.CreateVersion7(), on ? "true" : "false", BaselineSeed.SystemUserId);
    }

    [Fact]
    public async Task FR_ADM_ORG_01_S114a_a_new_company_with_another_currency_has_a_branch_and_default_numbering_schemes_at_once()
    {
        await ReferenceDataAsync(null);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var defaultCompany = await _h.DefaultCompanyAsync(Ct);
        var expected = await _h.ScalarAsync<long>("select count(*) from cfg.numbering_schemes where company_id = $1", Ct, defaultCompany);
        Assert.True(expected > 0);
        var code = "N" + Guid.NewGuid().ToString("N")[..8];

        var created = await _h.PostAsync(admin, "/api/org/companies", NewCompany(code), Ct);

        Expect(HttpStatusCode.Created, created);
        var id = created.Json.GetProperty("id").GetGuid();
        Assert.Equal("USD", created.Json.GetProperty("baseCurrencyCode").GetString());
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from org.branches where company_id = $1 and not is_deleted", Ct, id));
        Assert.Equal(expected, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_schemes where company_id = $1 and status = 'active'", Ct, id));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from cfg.numbering_schemes where company_id = $1 and (gap_free or branch_id is not null)", Ct, id));
    }

    [Fact]
    public async Task FR_KRN_IAM_18_S114a_at_the_company_limit_the_create_is_refused_and_leaves_no_company_or_scheme_behind()
    {
        var code = await _h.ProvisionAsync("o1", Ct);
        var admin = await _h.UserAsync(Ct, code, "tenant_admin");
        var active = await InAsync<long>(code, "select count(*) from org.companies where not is_deleted and status = 'active'");
        await fx.SetClaimAsync(code, "limits,companies", (int)active, Ct);
        var companies = await InAsync<long>(code, "select count(*) from org.companies");
        var schemes = await InAsync<long>(code, "select count(*) from cfg.numbering_schemes");

        var refused = await _h.PostAsync(admin, "/api/org/companies", NewCompany("L" + Guid.NewGuid().ToString("N")[..8]), Ct, code);

        Expect(HttpStatusCode.Conflict, refused);
        Assert.Contains("companies", refused.Body, StringComparison.Ordinal);
        Assert.Equal(companies, await InAsync<long>(code, "select count(*) from org.companies"));
        Assert.Equal(schemes, await InAsync<long>(code, "select count(*) from cfg.numbering_schemes"));
    }

    [Fact]
    public async Task FR_ADM_ORG_01_S114a_registration_value_is_masked_for_a_masking_role_in_full_for_an_administrator_and_every_full_view_is_logged()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var created = await _h.PostAsync(admin, $"/api/org/companies/{company}/registrations", new { kind = "ntn", authority = "FBR", value = "1234567-8", validFrom = "2026-01-01" }, Ct);
        Expect(HttpStatusCode.Created, created);
        var id = created.Json.GetProperty("id").GetGuid();

        var logged = await _h.ScalarAsync<long>("select count(*) from aud.access_log where entity_type = 'org.company_registrations' and actor_user_id = $1", Ct, admin);
        var full = await _h.GetAsync(admin, $"/api/org/companies/{company}/registrations", Ct);
        Expect(HttpStatusCode.OK, full);
        var row = full.Json.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        Assert.Equal("1234567-8", row.GetProperty("value").GetString());
        Assert.Equal("full", row.GetProperty("valueAccess").GetString());
        Assert.Equal(logged + 1, await _h.ScalarAsync<long>("select count(*) from aud.access_log where entity_type = 'org.company_registrations' and actor_user_id = $1", Ct, admin));

        var maskedRole = await _h.RoleAsync(Ct, ["org.company_registrations.read"], null, [("org.company_registrations", "value", "masked")]);
        var masked = await _h.UserAsync(Ct);
        await _h.AssignAsync(masked, maskedRole, null, _h.Today, null, Ct);
        var seen = await _h.GetAsync(masked, $"/api/org/companies/{company}/registrations", Ct);
        var maskedRow = seen.Json.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        Assert.Equal("****67-8", maskedRow.GetProperty("value").GetString());
        Assert.Equal("masked", maskedRow.GetProperty("valueAccess").GetString());
        Assert.DoesNotContain("1234567", seen.Body, StringComparison.Ordinal);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.access_log where entity_type = 'org.company_registrations' and actor_user_id = $1", Ct, masked));

        var hiddenRole = await _h.RoleAsync(Ct, ["org.company_registrations.read"], null, [("org.company_registrations", "value", "hidden")]);
        var hidden = await _h.UserAsync(Ct);
        await _h.AssignAsync(hidden, hiddenRole, null, _h.Today, null, Ct);
        var none = await _h.GetAsync(hidden, $"/api/org/companies/{company}/registrations", Ct);
        Assert.Equal(JsonValueKind.Null, none.Json.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id).GetProperty("value").ValueKind);
        Assert.DoesNotContain("1234567", none.Body, StringComparison.Ordinal);

        // A reader can neither create nor change.
        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(masked, $"/api/org/companies/{company}/registrations", new { kind = "strn", authority = "FBR", value = "999" }, Ct));
    }

    [Fact]
    public async Task FR_KRN_ORG_01_S114a_registration_is_stored_encrypted_with_a_blind_index_and_the_change_log_never_holds_the_value()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.CompanyAsync(Ct);
        var created = await _h.PostAsync(admin, $"/api/org/companies/{company}/registrations", new { kind = "eobi", authority = "EOBI", value = "EOBI-SECRET-7788", validFrom = "2026-01-01" }, Ct);
        var id = created.Json.GetProperty("id").GetGuid();

        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from org.company_registrations where id = $1 and value_hmac is not null and position('EOBI-SECRET'::bytea in value) = 0", Ct, id));
        var changes = await _h.ScalarAsync<string>("select string_agg(changes::text, ' ') from aud.change_log where entity_type = 'org.company_registrations' and entity_id = $1", Ct, id);
        Assert.Contains("redacted", changes, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", changes, StringComparison.Ordinal);

        // A same kind and authority cannot overlap in time (effective dating); a later period can follow.
        Expect(HttpStatusCode.Conflict, await _h.PostAsync(admin, $"/api/org/companies/{company}/registrations", new { kind = "eobi", authority = "EOBI", value = "other", validFrom = "2026-06-01" }, Ct));
        var rowVersion = (await _h.GetAsync(admin, $"/api/org/companies/{company}/registrations", Ct)).Json.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id).GetProperty("rowVersion").GetInt64();
        Expect(HttpStatusCode.OK, await _h.PutAsync(admin, $"/api/org/company-registrations/{id}", new { validTo = "2026-12-31", rowVersion }, Ct));
        Expect(HttpStatusCode.Conflict, await _h.PutAsync(admin, $"/api/org/company-registrations/{id}", new { validTo = "2027-12-31", rowVersion }, Ct)); // stale version
        Expect(HttpStatusCode.Created, await _h.PostAsync(admin, $"/api/org/companies/{company}/registrations", new { kind = "eobi", authority = "EOBI", value = "next period", validFrom = "2027-01-01" }, Ct));
        Expect(HttpStatusCode.NotFound, await _h.GetAsync(admin, $"/api/org/companies/{Guid.NewGuid()}/registrations", Ct));
    }

    [Fact]
    public async Task FR_KRN_IAM_14_S114a_a_new_bank_account_waits_for_a_second_administrator_then_appears_with_masked_numbers_in_the_log_and_audit()
    {
        var code = await _h.ProvisionAsync("o2", Ct);
        await ReferenceDataAsync(code);
        var maker = await _h.UserAsync(Ct, code, "tenant_admin");
        var checker = await _h.UserAsync(Ct, code, "tenant_admin");
        var company = await InAsync<Guid>(code, "select id from org.companies where not is_deleted order by created_at limit 1");
        var bank = await InAsync<string>(code, "select code from ref.banks order by code limit 1");
        var body = new { bankCode = bank, accountTitle = "Salary account", accountNumber = "0011223344556", iban = "PK36SCBL0000001123456702", currencyCode = "PKR", reason = "new payroll bank" };

        var held = await _h.PostAsync(maker, $"/api/org/companies/{company}/bank-accounts", body, Ct, code);

        Expect(HttpStatusCode.Accepted, held);
        Assert.Equal("pending", held.Json.GetProperty("outcome").GetString());
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(0L, await InAsync<long>(code, "select count(*) from org.company_bank_accounts"));
        var payload = await InAsync<string>(code, "select payload::text from iam.maker_checker_requests where id = $1", request);
        Assert.DoesNotContain("0011223344556", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("SCBL", payload, StringComparison.Ordinal);

        var pending = await _h.GetAsync(checker, "/api/iam/maker-checker-requests/pending", Ct, code);
        var item = pending.Json.GetProperty("toDecide").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == request);
        Assert.Equal("org.bank_account.save", item.GetProperty("kind").GetString());
        Assert.True(item.GetProperty("canApprove").GetBoolean());
        Assert.DoesNotContain("0011223344556", item.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("****4556", item.GetRawText(), StringComparison.Ordinal);

        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(maker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct, code));
        Expect(HttpStatusCode.OK, await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "checked" }, Ct, code));

        var list = await _h.GetAsync(checker, $"/api/org/companies/{company}/bank-accounts", Ct, code);
        var account = Assert.Single(list.Json.EnumerateArray());
        Assert.Equal("0011223344556", account.GetProperty("accountNumber").GetString());
        Assert.Equal("PK36SCBL0000001123456702", account.GetProperty("iban").GetString());
        Assert.Equal(1L, await InAsync<long>(code, "select count(*) from aud.access_log where entity_type = 'org.company_bank_accounts' and actor_user_id = $1", checker));
        var changes = await InAsync<string>(code, "select string_agg(changes::text, ' ') from aud.change_log where entity_type = 'org.company_bank_accounts'");
        Assert.DoesNotContain("0011223344556", changes, StringComparison.Ordinal);
        Assert.DoesNotContain("SCBL", changes, StringComparison.Ordinal);

        // A change and a removal wait as well; an edit made meanwhile makes the held change void.
        var id = account.GetProperty("id").GetGuid();
        var version = account.GetProperty("rowVersion").GetInt64();
        var change = await _h.PutAsync(maker, $"/api/org/company-bank-accounts/{id}", new { accountTitle = "Renamed", rowVersion = version }, Ct, code);
        Expect(HttpStatusCode.Accepted, change);
        var remove = await _h.PostAsync(maker, $"/api/org/company-bank-accounts/{id}/remove", new { rowVersion = version, reason = "closed" }, Ct, code);
        Expect(HttpStatusCode.Accepted, remove);
        Expect(HttpStatusCode.OK, await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{change.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "ok" }, Ct, code));
        var stale = await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{remove.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "ok" }, Ct, code);
        Expect(HttpStatusCode.Conflict, stale);
        Assert.Equal("Renamed", (await _h.GetAsync(checker, $"/api/org/companies/{company}/bank-accounts", Ct, code)).Json[0].GetProperty("accountTitle").GetString());
    }

    [Fact]
    public async Task FR_KRN_IAM_14_S114a_with_maker_checker_off_a_bank_account_is_saved_and_removed_at_once_and_bad_values_are_refused()
    {
        var code = await _h.ProvisionAsync("o3", Ct);
        await ReferenceDataAsync(code);
        await SetMakerCheckerAsync(code, on: false);
        var admin = await _h.UserAsync(Ct, code, "tenant_admin");
        var company = await InAsync<Guid>(code, "select id from org.companies where not is_deleted order by created_at limit 1");
        var bank = await InAsync<string>(code, "select code from ref.banks order by code limit 1");

        Expect(HttpStatusCode.BadRequest, await _h.PostAsync(admin, $"/api/org/companies/{company}/bank-accounts", new { bankCode = "NO-SUCH-BANK", accountTitle = "T", accountNumber = "1", currencyCode = "PKR" }, Ct, code));
        Expect(HttpStatusCode.BadRequest, await _h.PostAsync(admin, $"/api/org/companies/{company}/bank-accounts", new { bankCode = bank, accountTitle = "T", accountNumber = "1", currencyCode = "ZZZ" }, Ct, code));
        Expect(HttpStatusCode.BadRequest, await _h.PostAsync(admin, $"/api/org/companies/{company}/bank-accounts", new { bankCode = bank, accountTitle = "T", accountNumber = "1", iban = "PK00 1234", currencyCode = "PKR" }, Ct, code));
        Expect(HttpStatusCode.BadRequest, await _h.PostAsync(admin, $"/api/org/companies/{company}/bank-accounts", new { bankCode = bank, accountTitle = " ", accountNumber = "1", currencyCode = "PKR" }, Ct, code));

        var created = await _h.PostAsync(admin, $"/api/org/companies/{company}/bank-accounts", new { bankCode = bank, accountTitle = "Direct", accountNumber = "5566778899", currencyCode = "PKR" }, Ct, code);
        Expect(HttpStatusCode.Created, created);
        var id = created.Json.GetProperty("id").GetGuid();
        var version = (await _h.GetAsync(admin, $"/api/org/companies/{company}/bank-accounts", Ct, code)).Json[0].GetProperty("rowVersion").GetInt64();
        Expect(HttpStatusCode.OK, await _h.PostAsync(admin, $"/api/org/company-bank-accounts/{id}/remove", new { rowVersion = version }, Ct, code));
        Assert.Empty((await _h.GetAsync(admin, $"/api/org/companies/{company}/bank-accounts", Ct, code)).Json.EnumerateArray());
    }

    [Fact]
    public async Task FR_ADM_ORG_02_S114a_a_location_with_a_latitude_outside_minus_90_to_90_a_bad_time_zone_or_bad_hours_is_refused_and_good_ones_are_saved()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.DefaultCompanyAsync(Ct);
        var branch = await _h.ScalarAsync<Guid>("select id from org.branches where company_id = $1 and not is_deleted order by created_at limit 1", Ct, company);
        object Site(string code, decimal lat = 24.8607m, string zone = "Asia/Karachi", object? hours = null) => new
        {
            companyId = company, branchId = branch, code, nameI18n = new { en = "Site " + code }, address = new { line1 = "1 Main Road", city = "Karachi", postalCode = "74000", country = "PK" },
            regionCode = "SD", latitude = lat, longitude = 67.0011m, timeZone = zone, operatingHours = hours, isActive = true,
        };

        var tooHigh = await _h.PostAsync(admin, "/api/org/locations", Site("LOC" + Guid.NewGuid().ToString("N")[..6], 91m), Ct);
        Expect(HttpStatusCode.BadRequest, tooHigh);
        Assert.Contains("ck_locations_latitude_range", tooHigh.Body, StringComparison.Ordinal);
        var badZone = await _h.PostAsync(admin, "/api/org/locations", Site("LOC" + Guid.NewGuid().ToString("N")[..6], zone: "Mars/Olympus"), Ct);
        Assert.Contains("ck_locations_time_zone", badZone.Body, StringComparison.Ordinal);
        var backwards = await _h.PostAsync(admin, "/api/org/locations", Site("LOC" + Guid.NewGuid().ToString("N")[..6], hours: new { mon = new { open = "17:00", close = "09:00" } }), Ct);
        Assert.Contains("ck_locations_operating_hours", backwards.Body, StringComparison.Ordinal);
        var unknownDay = await _h.PostAsync(admin, "/api/org/locations", Site("LOC" + Guid.NewGuid().ToString("N")[..6], hours: new { funday = new { open = "09:00", close = "17:00" } }), Ct);
        Assert.Contains("ck_locations_operating_hours", unknownDay.Body, StringComparison.Ordinal);

        var saved = await _h.PostAsync(admin, "/api/org/locations", Site("LOC" + Guid.NewGuid().ToString("N")[..6], hours: new { mon = new { open = "09:00", close = "17:00" }, sun = new { closed = true } }), Ct);
        Expect(HttpStatusCode.Created, saved);
        Assert.Equal("Asia/Karachi", saved.Json.GetProperty("timeZone").GetString());
        Assert.Equal("09:00", saved.Json.GetProperty("operatingHours").GetProperty("mon").GetProperty("open").GetString());
        Assert.True(saved.Json.GetProperty("operatingHours").GetProperty("sun").GetProperty("closed").GetBoolean());
    }

    [Fact]
    public async Task FR_ADM_LOC_04_S114a_a_holiday_year_is_copied_forward_with_moon_dates_tentative_then_one_is_confirmed_and_pack_calendars_are_refused()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var calendar = await _h.PostAsync(admin, "/api/ref/calendars", new { code = "CAL" + Guid.NewGuid().ToString("N")[..6], nameI18n = new { en = "Pakistan holidays" }, countryCode = "PK", source = "tenant" }, Ct);
        Expect(HttpStatusCode.Created, calendar);
        var id = calendar.Json.GetProperty("id").GetGuid();
        foreach (var (date, kind, name) in new[] { ("2026-03-23", "public", "Pakistan Day"), ("2026-03-20", "religious", "Eid ul-Fitr"), ("2026-02-28", "public", "Leap check"), ("2024-02-29", "public", "Leap day") })
        {
            Expect(HttpStatusCode.Created, await _h.PostAsync(admin, "/api/ref/holidays", new { calendarId = id, holidayDate = date, nameI18n = new { en = name }, kind, isTentative = false }, Ct));
        }

        var copy = await _h.PostAsync(admin, $"/api/ref/calendars/{id}/copy-year", new { fromYear = 2026, toYear = 2027 }, Ct);

        Expect(HttpStatusCode.OK, copy);
        Assert.Equal(3, copy.Json.GetProperty("created").GetInt32());
        var rows = (await _h.GetAsync(admin, $"/api/ref/holidays?calendarId={id}&limit=100", Ct)).Json.GetProperty("items").EnumerateArray()
            .Where(e => e.GetProperty("holidayDate").GetString()!.StartsWith("2027", StringComparison.Ordinal)).ToDictionary(e => e.GetProperty("holidayDate").GetString()!);
        Assert.False(rows["2027-03-23"].GetProperty("isTentative").GetBoolean());
        Assert.True(rows["2027-03-20"].GetProperty("isTentative").GetBoolean());
        // Running it again keeps what is there.
        var again = await _h.PostAsync(admin, $"/api/ref/calendars/{id}/copy-year", new { fromYear = 2026, toYear = 2027 }, Ct);
        Assert.Equal(0, again.Json.GetProperty("created").GetInt32());
        Assert.Equal(3, again.Json.GetProperty("skipped").GetInt32());
        // The leap day cannot move to a common year.
        var leap = await _h.PostAsync(admin, $"/api/ref/calendars/{id}/copy-year", new { fromYear = 2024, toYear = 2025 }, Ct);
        Assert.Equal(0, leap.Json.GetProperty("created").GetInt32());
        Assert.Equal(1, leap.Json.GetProperty("skipped").GetInt32());

        // Confirm the Eid date: moved by one day and no longer tentative.
        var eid = rows["2027-03-20"];
        var confirmed = await _h.PutAsync(admin, $"/api/ref/holidays/{eid.GetProperty("id").GetGuid()}", new { calendarId = id, holidayDate = "2027-03-21", nameI18n = new { en = "Eid ul-Fitr" }, kind = "religious", isTentative = false, rowVersion = eid.GetProperty("rowVersion").GetInt64() }, Ct);
        Expect(HttpStatusCode.OK, confirmed);
        Assert.False(confirmed.Json.GetProperty("isTentative").GetBoolean());

        Expect(HttpStatusCode.BadRequest, await _h.PostAsync(admin, $"/api/ref/calendars/{id}/copy-year", new { fromYear = 2026, toYear = 2026 }, Ct));
        Expect(HttpStatusCode.NotFound, await _h.PostAsync(admin, $"/api/ref/calendars/{Guid.NewGuid()}/copy-year", new { fromYear = 2026, toYear = 2027 }, Ct));
        var pack = await _h.ScalarAsync<Guid?>("select id from ref.calendars where source = 'pack' limit 1", Ct);
        if (pack is { } packId)
        {
            Expect(HttpStatusCode.Forbidden, await _h.PostAsync(admin, $"/api/ref/calendars/{packId}/copy-year", new { fromYear = 2026, toYear = 2027 }, Ct));
        }

        Expect(HttpStatusCode.Forbidden, await _h.PostAsync(await _h.UserAsync(Ct, null, "business_user"), $"/api/ref/calendars/{id}/copy-year", new { fromYear = 2026, toYear = 2027 }, Ct));
    }

    [Fact]
    public void FR_KRN_IAM_14_S114a_iban_check_digits_follow_iso_13616()
    {
        Assert.True(Erp.Kernel.Infrastructure.Org.OrganizationAdministrationService.IsValidIban("PK36SCBL0000001123456702"));
        Assert.True(Erp.Kernel.Infrastructure.Org.OrganizationAdministrationService.IsValidIban("GB82WEST12345698765432"));
        Assert.False(Erp.Kernel.Infrastructure.Org.OrganizationAdministrationService.IsValidIban("PK37SCBL0000001123456702"));
        Assert.False(Erp.Kernel.Infrastructure.Org.OrganizationAdministrationService.IsValidIban("PK36"));
    }
}
