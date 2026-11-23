#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;

namespace Erp.IntegrationTests.Employments;

/// <summary>
/// Employee payment accounts and statutory registrations (slice S1.20d, Build items 14 and 15) through the real API pipeline, the
/// real access engine, maker-checker and a real PostgreSQL: FR-HRM-EMP-06 (several accounts, IBAN validation, split by remainder,
/// percent or amount, effective dates, maker-checker), FR-HRM-PAY-24 (payment modes), FR-HRM-EMP-18 (the same account on another
/// person warns and can open a review case), GHR-02 (encrypted, masked by default, reveal access-logged), FR-XM-32 (nobody approves
/// their own change), FR-HRM-CPL-02 data part (registrations per scheme, no contribution rules) and the permission, data-scope and
/// field-security rules of every endpoint.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PayDetailsTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Employments = "/api/hrm/employments";
    private readonly string ValidIban = NewIban();
    private readonly string OtherIban = NewIban();
    private readonly string Number = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private AccessHarness _h = null!;
    private Guid _bank;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => _h.Today;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A valid Pakistani IBAN of its own (24 characters, check digits computed), so tests never share an account.</summary>
    private static string NewIban()
    {
        var bban = "MEZN" + string.Concat(Enumerable.Range(0, 16).Select(_ => (char)('0' + Random.Shared.Next(10))));
        var digits = string.Concat((bban + "PK00").Select(ch => char.IsAsciiDigit(ch) ? ch.ToString() : (ch - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var check = 98 - (int)(System.Numerics.BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) % 97);
        return $"PK{check:00}{bban}";
    }

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await using var owner = await _h.OwnerAsync(null, Ct);
        await AccessHarness.ExecAsync(owner, """
            insert into ref.banks (id, code, name_i18n, country_code, created_at, created_by, updated_at, updated_by)
            values ($1, 'PAYBANK', '{"en":"Pay Bank"}', 'PK', now(), $2, now(), $2) on conflict do nothing
            """, Ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId);
        _bank = await AccessHarness.ScalarAsync<Guid>(owner, "select id from ref.banks where code = 'PAYBANK'", Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Guid> PersonAsync() => (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;

    /// <summary>A company of its own; payment account changes need a second person unless <paramref name="approval"/> is false.</summary>
    private async Task<(Guid Company, (Guid OrgUnit, Guid Branch) Place)> CompanyAsync(bool approval = true)
    {
        var company = await _h.CompanyAsync(Ct);
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var second = await _h.UserAsync(Ct, null, "tenant_admin");
        var held = await _h.PutAsync(admin, "/api/cfg/settings/hrm.employment_events.approval_kinds", new { layer = "company", scopeId = company, value = Array.Empty<string>(), reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
        if (!approval)
        {
            var off = await _h.PutAsync(admin, "/api/cfg/settings/hrm.bank_accounts.requires_approval", new { layer = "company", scopeId = company, value = false, reason = "test" }, Ct);
            Assert.True(off.Status == HttpStatusCode.Accepted, off.Body); // switching approval off waits for a second person too (FR-XM-32)
            Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{off.Json.GetProperty("requestId").GetGuid()}/approve", new { reason = "agreed" }, Ct)).Status);
        }

        return (company, await _h.PlaceAsync(company, Ct));
    }

    private async Task<Guid> HireAsync(Guid user, Guid company, (Guid OrgUnit, Guid Branch) place, Guid? person = null, Guid? manager = null)
    {
        var answer = await _h.PostAsync(user, Employments, new
        {
            personId = person ?? await PersonAsync(),
            companyId = company,
            employeeType = "permanent",
            joiningDate = Iso(Today.AddDays(-60)),
            assignment = new { orgUnitId = place.OrgUnit, branchId = place.Branch, title = "Officer", managerEmploymentId = manager },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return answer.Json.GetProperty("id").GetGuid();
    }

    private Task<Answer> ChangeAsync(Guid user, Guid employment, object[] changes, string? duplicates = null) =>
        _h.PostAsync(user, $"{Employments}/{employment}/bank-accounts", new { changes, reason = "new salary account", duplicates }, Ct);

    private object Add(string? iban = "", string? number = null, string kind = "remainder", decimal? value = null, int? priority = null, DateOnly? from = null, string mode = "bank_transfer") => new
    {
        action = "add",
        paymentMode = mode,
        bankId = mode is "bank_transfer" or "mobile_wallet" ? _bank : (Guid?)null,
        accountTitle = "Ayesha Khan",
        iban = iban == "" ? ValidIban : iban,
        accountNumber = number,
        splitKind = kind,
        splitValue = value,
        priority,
        validFrom = Iso(from ?? Today.AddDays(-60)),
    };

    private async Task<JsonElement[]> ListAsync(Guid user, Guid employment, DateOnly? asOf = null)
    {
        var answer = await _h.GetAsync(user, $"{Employments}/{employment}/bank-accounts" + (asOf is { } d ? $"?asOf={Iso(d)}" : string.Empty), Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return [.. answer.Json.GetProperty("items").EnumerateArray()];
    }

    private Task<Answer> ApproveAsync(Guid user, Guid request) => _h.PostAsync(user, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "checked" }, Ct);

    private async Task<Guid> UserWithAsync(string[] permissions, (string, object)[] scopes, (string, string, string)[]? fields = null)
    {
        var user = await _h.UserAsync(Ct);
        await _h.AssignAsync(user, await _h.RoleAsync(Ct, permissions, scopes, fields), null, _h.Today.AddDays(-1), null, Ct);
        return user;
    }

    private static string FieldError(Answer a, string field) => a.Json.GetProperty("errors").GetProperty(field)[0].GetString()!;

    // ------------------------------------------------------------------ maker-checker (FR-HRM-EMP-06, FR-XM-32)

    [Fact]
    public async Task FR_HRM_EMP_06_FR_XM_32_Acceptance_a_new_bank_account_waits_for_a_second_person_who_is_not_the_requester()
    {
        var maker = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var employment = await HireAsync(maker, company, place);

        var held = await ChangeAsync(maker, employment, [Add()]);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        Assert.Equal("pending", held.Text("status"));
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.employee_bank_accounts where employment_id = $1", Ct, employment));

        // R-233 (S1.21a): the account list says that a change waits, without its content, and whose it is.
        var waiting = await _h.GetAsync(checker, $"{Employments}/{employment}/bank-accounts", Ct);
        var wait = Assert.Single(waiting.Json.GetProperty("pending").EnumerateArray());
        Assert.Equal(request, wait.GetProperty("requestId").GetGuid());
        Assert.False(wait.GetProperty("mine").GetBoolean());
        Assert.True((await _h.GetAsync(maker, $"{Employments}/{employment}/bank-accounts", Ct)).Json.GetProperty("pending")[0].GetProperty("mine").GetBoolean());
        Assert.DoesNotContain(ValidIban[4..], waiting.Body, StringComparison.Ordinal);

        // The requester cannot approve; the second HR administrator sees it described (masked IBAN, never the full number).
        var own = await ApproveAsync(maker, request);
        Assert.Equal(HttpStatusCode.Forbidden, own.Status);
        Assert.Equal("errors.maker_checker.own_request", own.Json.GetProperty("title").GetString());
        var inbox = await _h.GetAsync(checker, "/api/iam/maker-checker-requests/pending", Ct);
        Assert.True(inbox.Status == HttpStatusCode.OK, inbox.Body);
        var card = inbox.Json.GetProperty("toDecide").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == request);
        Assert.Equal("hrm.employee_bank_accounts.change", card.GetProperty("kind").GetString());
        Assert.True(card.GetProperty("canApprove").GetBoolean(), card.ToString());
        Assert.Contains("****" + ValidIban[^4..], card.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ValidIban, inbox.Body, StringComparison.Ordinal);

        var approved = await ApproveAsync(checker, request);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        var rows = await ListAsync(checker, employment);
        var row = Assert.Single(rows);
        Assert.Equal("****" + ValidIban[^4..], row.GetProperty("iban").GetString());
        Assert.Equal("remainder", row.GetProperty("splitKind").GetString());
        Assert.Empty((await _h.GetAsync(checker, $"{Employments}/{employment}/bank-accounts", Ct)).Json.GetProperty("pending").EnumerateArray()); // decided: nothing waits any more

        // Stored encrypted with a blind index; the plain IBAN is nowhere in the row; the change log names the approver (GHR-09).
        await using var owner = await _h.OwnerAsync(null, Ct);
        var raw = await AccessHarness.ScalarAsync<byte[]>(owner, "select iban from hrm.employee_bank_accounts where employment_id = $1", Ct, employment);
        Assert.DoesNotContain(ValidIban[4..], System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.True(await AccessHarness.ScalarAsync<bool>(owner, "select iban_hmac is not null from hrm.employee_bank_accounts where employment_id = $1", Ct, employment));
        Assert.True(await AccessHarness.ScalarAsync<long>(owner, "select count(*) from aud.change_log where entity_type = 'hrm.employee_bank_accounts' and actor_user_id = $1", Ct, checker) >= 1);
    }

    [Fact]
    public async Task FR_HRM_EMP_06_FR_XM_32_a_held_change_is_void_when_the_account_changed_meanwhile_and_the_employee_never_approves_their_own()
    {
        var maker = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var employment = await HireAsync(maker, company, place);
        var first = await ChangeAsync(maker, employment, [Add()]);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(checker, first.Json.GetProperty("requestId").GetGuid())).Status);
        var row = Assert.Single(await ListAsync(maker, employment));
        var id = row.GetProperty("id").GetGuid();
        var version = row.GetProperty("rowVersion").GetInt64();

        // Two requests on the same account: the second is void once the first is applied.
        var a = await ChangeAsync(maker, employment, [new { action = "change", id, rowVersion = version, accountTitle = "A Khan" }]);
        var b = await ChangeAsync(maker, employment, [new { action = "change", id, rowVersion = version, accountTitle = "B Khan" }]);
        Assert.Equal(HttpStatusCode.Accepted, a.Status);
        Assert.Equal(HttpStatusCode.Accepted, b.Status);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(checker, a.Json.GetProperty("requestId").GetGuid())).Status);
        var stale = await ApproveAsync(checker, b.Json.GetProperty("requestId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, stale.Status);
        Assert.Equal("errors.hrm.bank_account.stale", stale.Json.GetProperty("title").GetString());
        Assert.Equal("A Khan", Assert.Single(await ListAsync(maker, employment)).GetProperty("accountTitle").GetString());

        // The employee linked to the employment cannot approve a change of their own accounts, even holding the permission.
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(maker, $"{Employments}/{employment}/user", new { userId = checker, rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, employment) }, Ct)).Status);
        var current = Assert.Single(await ListAsync(maker, employment));
        var c = await ChangeAsync(maker, employment, [new { action = "change", id, rowVersion = current.GetProperty("rowVersion").GetInt64(), iban = OtherIban }]);
        var mine = await ApproveAsync(checker, c.Json.GetProperty("requestId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, mine.Status);
        Assert.Equal("errors.hrm.bank_account.own_account", mine.Json.GetProperty("title").GetString());
    }

    // ------------------------------------------------------------------ validation and split (FR-HRM-EMP-06, FR-HRM-PAY-24)

    [Fact]
    public async Task FR_HRM_EMP_06_FR_HRM_PAY_24_an_invalid_IBAN_and_bank_details_on_cash_are_refused()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync(approval: false);
        var employment = await HireAsync(hr, company, place);

        var checksum = await ChangeAsync(hr, employment, [Add(iban: ValidIban[..2] + (ValidIban[2..4] == "99" ? "98" : (int.Parse(ValidIban[2..4], System.Globalization.CultureInfo.InvariantCulture) + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture)) + ValidIban[4..])]);
        Assert.Equal(HttpStatusCode.BadRequest, checksum.Status);
        Assert.Equal("errors.hrm.bank_account.iban_invalid", FieldError(checksum, "changes[0].iban"));
        var length = await ChangeAsync(hr, employment, [Add(iban: ValidIban[..^1])]); // 23 characters: Pakistan has 24
        Assert.Equal("errors.hrm.bank_account.iban_invalid", FieldError(length, "changes[0].iban"));
        var country = await ChangeAsync(hr, employment, [Add(iban: "1" + ValidIban[1..])]);
        Assert.Equal("errors.hrm.bank_account.iban_invalid", FieldError(country, "changes[0].iban"));
        var noNumber = await ChangeAsync(hr, employment, [Add(iban: null)]);
        Assert.Equal("errors.hrm.bank_account.number_or_iban", FieldError(noNumber, "changes[0].accountNumber"));
        var cash = await _h.PostAsync(hr, $"{Employments}/{employment}/bank-accounts", new { changes = new[] { new { action = "add", paymentMode = "cash", splitKind = "remainder", iban = ValidIban } } }, Ct);
        Assert.Equal("errors.hrm.bank_account.not_for_mode", FieldError(cash, "changes[0].iban"));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.employee_bank_accounts where employment_id = $1", Ct, employment));

        // Spaces and lower case are accepted and stored normalized; cash needs no bank details.
        var ok = await ChangeAsync(hr, employment, [Add(iban: string.Join(' ', ValidIban.ToLowerInvariant().Chunk(4).Select(c => new string(c))))]);
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body);
        Assert.Equal("applied", ok.Text("status"));
        var accounts = await ListAsync(hr, employment);
        var reveal = await _h.GetAsync(hr, $"/api/hrm/bank-accounts/{accounts[0].GetProperty("id").GetGuid()}/reveal", Ct);
        Assert.Equal(ValidIban, reveal.Text("iban"));
    }

    [Fact]
    public async Task FR_HRM_EMP_06_the_split_has_one_remainder_and_at_most_100_percent_on_every_date_and_changes_keep_history()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync(approval: false);
        var employment = await HireAsync(hr, company, place);

        // A percent account alone has no remainder; two remainders are refused; above 100 percent is refused.
        var alone = await ChangeAsync(hr, employment, [Add(kind: "percent", value: 40)]);
        Assert.Equal("errors.hrm.bank_account.no_remainder", alone.Json.GetProperty("title").GetString());
        var two = await ChangeAsync(hr, employment, [Add(), Add(iban: OtherIban, priority: 2)]);
        Assert.Equal("errors.hrm.bank_account.two_remainders", two.Json.GetProperty("title").GetString());
        var over = await ChangeAsync(hr, employment, [Add(), Add(iban: OtherIban, kind: "percent", value: 60, priority: 2), Add(iban: null, number: Number, kind: "percent", value: 41, priority: 3)]);
        Assert.Equal("errors.hrm.bank_account.percent_over_100", over.Json.GetProperty("title").GetString());
        var badValue = await ChangeAsync(hr, employment, [Add(kind: "percent", value: 120)]);
        Assert.Equal("errors.hrm.bank_account.split_value", FieldError(badValue, "changes[0].splitValue"));

        // A remainder account and a 40 percent account, saved together.
        var saved = await ChangeAsync(hr, employment, [Add(priority: 1), Add(iban: OtherIban, kind: "percent", value: 40, priority: 2)]);
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
        var rows = await ListAsync(hr, employment);
        var remainder = rows.Single(r => r.GetProperty("splitKind").GetString() == "remainder");
        var percent = rows.Single(r => r.GetProperty("splitKind").GetString() == "percent");

        // The remainder moves from one account to the other in one request, from a later date: history is kept (GHR-03).
        var from = Today.AddDays(10);
        var swap = await ChangeAsync(hr, employment,
        [
            new { action = "change", id = remainder.GetProperty("id").GetGuid(), rowVersion = remainder.GetProperty("rowVersion").GetInt64(), effectiveFrom = Iso(from), splitKind = "amount", splitValue = 25000m },
            new { action = "change", id = percent.GetProperty("id").GetGuid(), rowVersion = percent.GetProperty("rowVersion").GetInt64(), effectiveFrom = Iso(from), splitKind = "remainder" },
        ]);
        Assert.True(swap.Status == HttpStatusCode.OK, swap.Body);
        Assert.Equal(4, (await ListAsync(hr, employment)).Length);
        var before = await ListAsync(hr, employment, from.AddDays(-1));
        Assert.Equal("remainder", before.Single(r => r.GetProperty("priority").GetInt32() == 1).GetProperty("splitKind").GetString());
        var after = await ListAsync(hr, employment, from);
        Assert.Equal("amount", after.Single(r => r.GetProperty("priority").GetInt32() == 1).GetProperty("splitKind").GetString());
        Assert.Equal(25000m, after.Single(r => r.GetProperty("priority").GetInt32() == 1).GetProperty("splitValue").GetDecimal());
        Assert.Equal("remainder", after.Single(r => r.GetProperty("priority").GetInt32() == 2).GetProperty("splitKind").GetString());

        // Ending the only remainder account while the amount account goes on is refused; ending both on the same day is fine.
        var later = await ListAsync(hr, employment, from);
        var amount = later.Single(r => r.GetProperty("priority").GetInt32() == 1);
        var rest = later.Single(r => r.GetProperty("priority").GetInt32() == 2);
        var endOne = await ChangeAsync(hr, employment, [new { action = "end", id = rest.GetProperty("id").GetGuid(), rowVersion = rest.GetProperty("rowVersion").GetInt64(), validTo = Iso(from.AddDays(30)) }]);
        Assert.Equal("errors.hrm.bank_account.no_remainder", endOne.Json.GetProperty("title").GetString());
        var endBoth = await ChangeAsync(hr, employment,
        [
            new { action = "end", id = rest.GetProperty("id").GetGuid(), rowVersion = rest.GetProperty("rowVersion").GetInt64(), validTo = Iso(from.AddDays(30)) },
            new { action = "end", id = amount.GetProperty("id").GetGuid(), rowVersion = amount.GetProperty("rowVersion").GetInt64(), validTo = Iso(from.AddDays(30)) },
        ]);
        Assert.True(endBoth.Status == HttpStatusCode.OK, endBoth.Body);
        Assert.Empty(await ListAsync(hr, employment, from.AddDays(30)));

        // A removal of an account never used for a payment.
        var gone = await ListAsync(hr, employment, from);
        var remove = await ChangeAsync(hr, employment, gone.Select(r => (object)new { action = "remove", id = r.GetProperty("id").GetGuid(), rowVersion = r.GetProperty("rowVersion").GetInt64() }).ToArray());
        Assert.True(remove.Status == HttpStatusCode.OK, remove.Body);
        Assert.Equal(2, (await ListAsync(hr, employment)).Length);
    }

    [Fact]
    public async Task FR_HRM_EMP_06_the_database_refuses_rows_that_break_the_rules_even_without_the_service()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync(approval: false);
        var employment = await HireAsync(hr, company, place);
        await using var owner = await _h.OwnerAsync(null, Ct);
        async Task<string?> InsertAsync(string mode, string kind, decimal? value, int priority, Guid? bank, bool withIban)
        {
            try
            {
                await AccessHarness.ExecAsync(owner, """
                    insert into hrm.employee_bank_accounts (id, company_id, employment_id, payment_mode, bank_id, account_title, iban, iban_hmac, split_kind, split_value, priority,
                        valid_from, created_at, created_by, updated_at, updated_by)
                    values ($1, $2, $3, $4, $5, 'T', $6, $6, $7, $8, $9, $10, now(), $11, now(), $11)
                    """, Ct, Guid.CreateVersion7(), company, employment, mode, bank, withIban ? new byte[] { 1, 2 } : null, kind, value, priority, Today, BaselineSeed.SystemUserId);
                return null;
            }
            catch (PostgresException ex)
            {
                return ex.ConstraintName;
            }
        }

        Assert.Equal("ck_employee_bank_accounts_bank_fields_required", await InsertAsync("bank_transfer", "remainder", null, 1, null, true));
        Assert.Equal("ck_employee_bank_accounts_bank_fields_required", await InsertAsync("cash", "remainder", null, 1, _bank, false));
        Assert.Equal("ck_employee_bank_accounts_split_value", await InsertAsync("cash", "remainder", 10m, 1, null, false));
        Assert.Equal("ck_employee_bank_accounts_priority", await InsertAsync("cash", "remainder", null, 0, null, false));
        Assert.Equal("ck_employee_bank_accounts_split", await InsertAsync("cash", "percent", 50m, 1, null, false)); // no remainder (checked at commit)
        Assert.Null(await InsertAsync("cash", "remainder", null, 1, null, false));
        Assert.Equal("ck_employee_bank_accounts_split", await InsertAsync("cheque", "remainder", null, 2, null, false)); // a second remainder
    }

    // ------------------------------------------------------------------ the "ghost employee" check (FR-HRM-EMP-18)

    [Fact]
    public async Task FR_HRM_EMP_18_FR_HRM_EMP_06_the_same_account_number_on_another_employee_warns_and_review_opens_a_case()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync(approval: false);
        var first = await HireAsync(hr, company, place);
        var second = await HireAsync(hr, company, place);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(hr, first, [Add(iban: null, number: Number[..4] + "-" + Number[4..])])).Status);

        // Typed another way, at the same bank: the same account. HR must answer the warning.
        var warned = await ChangeAsync(hr, second, [Add(iban: null, number: Number)]);
        Assert.Equal(HttpStatusCode.Conflict, warned.Status);
        Assert.Equal("errors.hrm.bank_account.possible_duplicates", warned.Json.GetProperty("title").GetString());
        var firstPerson = await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, first);
        Assert.Equal(firstPerson, warned.Json.GetProperty("matches")[0].GetProperty("personId").GetGuid());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.employee_bank_accounts where employment_id = $1", Ct, second));

        var review = await ChangeAsync(hr, second, [Add(iban: null, number: Number)], duplicates: "review");
        Assert.True(review.Status == HttpStatusCode.OK, review.Body);
        var secondPerson = await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, second);
        Assert.Equal("open", await _h.ScalarAsync<string>("select status from hrm.person_duplicate_cases where person_id = $1 and other_person_id = $2 and match_kind = 'bank_account'", Ct, secondPerson, firstPerson));
        var queue = await _h.GetAsync(hr, "/api/hrm/duplicates", Ct);
        Assert.Contains(secondPerson.ToString(), queue.Body, StringComparison.OrdinalIgnoreCase);

        // The same number at another bank is another account: no warning.
        await using var owner = await _h.OwnerAsync(null, Ct);
        await AccessHarness.ExecAsync(owner, """
            insert into ref.banks (id, code, name_i18n, country_code, created_at, created_by, updated_at, updated_by)
            values ($1, 'OTHERBANK', '{"en":"Other Bank"}', 'PK', now(), $2, now(), $2) on conflict do nothing
            """, Ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId);
        var otherBank = await AccessHarness.ScalarAsync<Guid>(owner, "select id from ref.banks where code = 'OTHERBANK'", Ct);
        var third = await HireAsync(hr, company, place);
        var elsewhere = await _h.PostAsync(hr, $"{Employments}/{third}/bank-accounts", new
        {
            changes = new[] { new { action = "add", paymentMode = "bank_transfer", bankId = otherBank, accountTitle = "Ali", accountNumber = Number, splitKind = "remainder" } },
        }, Ct);
        Assert.True(elsewhere.Status == HttpStatusCode.OK, elsewhere.Body);
    }

    // ------------------------------------------------------------------ masking, reveal and access (GHR-02, FR-KRN-IAM-12, -13)

    [Fact]
    public async Task GHR_02_FR_HRM_EMP_06_numbers_are_masked_by_default_and_a_full_view_needs_the_permission_and_is_access_logged()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync(approval: false);
        var employment = await HireAsync(hr, company, place);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(hr, employment, [Add(iban: ValidIban, number: Number[..4] + "-" + Number[4..])])).Status);
        var row = Assert.Single(await ListAsync(hr, employment));
        var id = row.GetProperty("id").GetGuid();
        Assert.Equal("****" + Number[^4..], row.GetProperty("accountNumber").GetString());
        Assert.Equal("****" + ValidIban[^4..], row.GetProperty("iban").GetString());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.access_log where entity_id = $1", Ct, id));

        string[] reads = ["hrm.employments.read", "hrm.employee_bank_accounts.read"];
        (string, object)[] all = [("hrm.employments", "all"), ("hrm.employee_bank_accounts", "all")];
        var reader = await UserWithAsync(reads, all);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(reader, $"/api/hrm/bank-accounts/{id}/reveal", Ct)).Status);
        var masked = await UserWithAsync([.. reads, "hrm.employee_bank_accounts.reveal"], all, [("hrm.employee_bank_accounts", "iban", "masked"), ("hrm.employee_bank_accounts", "split_value", "hidden")]);
        var refused = await _h.GetAsync(masked, $"/api/hrm/bank-accounts/{id}/reveal", Ct);
        Assert.Equal("errors.hrm.bank_account.reveal_masked", refused.Json.GetProperty("title").GetString());
        Assert.False(Assert.Single(await ListAsync(masked, employment)).TryGetProperty("splitValue", out _)); // field rule hides it

        var full = await _h.GetAsync(hr, $"/api/hrm/bank-accounts/{id}/reveal", Ct);
        Assert.True(full.Status == HttpStatusCode.OK, full.Body);
        Assert.Equal(Number[..4] + "-" + Number[4..], full.Text("accountNumber"));
        Assert.Equal(ValidIban, full.Text("iban"));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.access_log where entity_id = $1 and actor_user_id = $2 and action = 'view' and fields = '{account_number,iban}'", Ct, id, hr));

        // The generated writes are closed (R-113): only the service writes.
        var generated = await _h.PostAsync(hr, "/api/hrm/employee-bank-accounts", new { employmentId = employment, paymentMode = "cash", splitKind = "remainder", priority = 5, validFrom = Iso(Today) }, Ct);
        Assert.True(generated.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, generated.Body);
    }

    [Fact]
    public async Task FR_HRM_EMP_06_FR_KRN_IAM_12_FR_KRN_IAM_13_account_changes_follow_the_permission_the_scope_and_field_security()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync(approval: false);
        var (other, _) = await CompanyAsync(approval: false);
        var employment = await HireAsync(hr, company, place);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(hr, employment, [Add()])).Status);
        var row = Assert.Single(await ListAsync(hr, employment));
        object Change(string title) => new { action = "change", id = row.GetProperty("id").GetGuid(), rowVersion = row.GetProperty("rowVersion").GetInt64(), accountTitle = title };

        string[] reads = ["hrm.employments.read", "hrm.employee_bank_accounts.read"];
        (string, object)[] all = [("hrm.employments", "all"), ("hrm.employee_bank_accounts", "all")];
        var reader = await UserWithAsync(reads, all);
        Assert.Equal(HttpStatusCode.Forbidden, (await ChangeAsync(reader, employment, [Add(iban: OtherIban, kind: "percent", value: 10, priority: 2)])).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ChangeAsync(reader, employment, [Change("X")])).Status);

        // Writing only in another company: the add is out of scope, the change cannot see the account.
        var elsewhere = await _h.ScopeAsync("companies", new { company_ids = new[] { other } }, Ct);
        var outside = await UserWithAsync([.. reads, "hrm.employee_bank_accounts.create", "hrm.employee_bank_accounts.update"], [("hrm.employments", "all"), ("hrm.employee_bank_accounts", elsewhere)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await ChangeAsync(outside, employment, [Add(iban: OtherIban, kind: "percent", value: 10, priority: 2)])).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ChangeAsync(outside, employment, [Change("X")])).Status);
        Assert.Empty(await ListAsync(outside, employment));

        // A role limited to the other company cannot see the employment at all.
        var onlyOther = await _h.UserAsync(Ct);
        await _h.AssignAsync(onlyOther, await _h.RoleAsync(Ct, [.. reads, "hrm.employee_bank_accounts.update"], all), other, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(onlyOther, $"{Employments}/{employment}/bank-accounts", Ct)).Status);

        // In scope with the title read-only: refused by field, nothing changed.
        var readOnly = await UserWithAsync([.. reads, "hrm.employee_bank_accounts.update"], all, [("hrm.employee_bank_accounts", "account_title", "read_only")]);
        var field = await ChangeAsync(readOnly, employment, [Change("X")]);
        Assert.Equal("errors.access.field_not_editable", FieldError(field, "account_title"));
        Assert.Equal("Ayesha Khan", Assert.Single(await ListAsync(hr, employment)).GetProperty("accountTitle").GetString());
    }

    // ------------------------------------------------------------------ statutory registrations (FR-HRM-CPL-02 data part)

    [Fact]
    public async Task FR_HRM_CPL_02_registrations_per_scheme_are_applicable_or_exempt_with_a_reason_and_keep_their_history()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var employment = await HireAsync(hr, company, place);
        var path = $"{Employments}/{employment}/statutory-registrations";

        var noReason = await _h.PostAsync(hr, path, new { scheme = "pessi", isApplicable = false }, Ct);
        Assert.Equal("errors.hrm.registration.exemption_reason_required", FieldError(noReason, "exemptionReason"));
        var badScheme = await _h.PostAsync(hr, path, new { scheme = "1eobi", isApplicable = true }, Ct);
        Assert.Equal("errors.hrm.registration.scheme_format", FieldError(badScheme, "scheme"));
        var reasonWhenApplies = await _h.PostAsync(hr, path, new { scheme = "eobi", isApplicable = true, exemptionReason = "x" }, Ct);
        Assert.Equal("errors.hrm.registration.exemption_reason_not_allowed", FieldError(reasonWhenApplies, "exemptionReason"));

        var eobi = await _h.PostAsync(hr, path, new { scheme = "EOBI", isApplicable = true, registrationNo = "EOBI-" + Number, validFrom = Iso(Today.AddDays(-60)) }, Ct);
        Assert.True(eobi.Status == HttpStatusCode.Created, eobi.Body);
        Assert.Equal("eobi", eobi.Text("scheme"));
        var exempt = await _h.PostAsync(hr, path, new { scheme = "pessi", isApplicable = false, exemptionReason = "Wage above the coverage of the scheme" }, Ct);
        Assert.True(exempt.Status == HttpStatusCode.Created, exempt.Body);
        var overlap = await _h.PostAsync(hr, path, new { scheme = "eobi", isApplicable = true, validFrom = Iso(Today) }, Ct);
        Assert.Equal("errors.hrm.registration.overlaps", FieldError(overlap, "validFrom"));

        // A change from a later date ends the row and starts a new one (history kept); the first day is never moved.
        var id = eobi.Json.GetProperty("id").GetGuid();
        var moved = await _h.PutAsync(hr, $"/api/hrm/statutory-registrations/{id}", new { validFrom = Iso(Today), rowVersion = 1 }, Ct);
        Assert.Equal("errors.hrm.registration.effective_from", FieldError(moved, "validFrom"));
        var changed = await _h.PutAsync(hr, $"/api/hrm/statutory-registrations/{id}", new { effectiveFrom = Iso(Today.AddDays(5)), isApplicable = false, exemptionReason = "Left the scheme", rowVersion = 1 }, Ct);
        Assert.True(changed.Status == HttpStatusCode.Created, changed.Body);
        Assert.Equal(id, changed.Json.GetProperty("endedId").GetGuid());
        var list = await _h.GetAsync(hr, $"{path}?asOf={Iso(Today)}", Ct);
        var now = list.Json.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("scheme").GetString() == "eobi");
        Assert.True(now.GetProperty("isApplicable").GetBoolean());
        Assert.Equal("****" + Number[^4..], now.GetProperty("registrationNo").GetString());
        var later = await _h.GetAsync(hr, $"{path}?asOf={Iso(Today.AddDays(5))}", Ct);
        Assert.False(later.Json.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("scheme").GetString() == "eobi").GetProperty("isApplicable").GetBoolean());

        // No contribution rule or rate is stored anywhere in the registration (AS-4): only these columns exist.
        var columns = await _h.ScalarAsync<string[]>("select array_agg(column_name::text order by column_name) from information_schema.columns where table_schema = 'hrm' and table_name = 'employment_statutory_registrations'", Ct);
        Assert.DoesNotContain(columns, c => c.Contains("rate", StringComparison.Ordinal) || c.Contains("ceiling", StringComparison.Ordinal) || c.Contains("amount", StringComparison.Ordinal));

        // A mistaken entry is removed.
        var pessi = exempt.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _h.DeleteAsync(hr, $"/api/hrm/statutory-registrations/{pessi}?rowVersion=1", Ct)).Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.employment_statutory_registrations where id = $1", Ct, pessi));
    }

    [Fact]
    public async Task FR_HRM_CPL_02_GHR_02_a_registration_number_of_another_person_is_refused_and_a_full_view_is_access_logged()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (other, _) = await CompanyAsync();
        var first = await HireAsync(hr, company, place);
        var second = await HireAsync(hr, company, place);
        var created = await _h.PostAsync(hr, $"{Employments}/{first}/statutory-registrations", new { scheme = "eobi", isApplicable = true, registrationNo = Number }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var id = created.Json.GetProperty("id").GetGuid();
        var duplicate = await _h.PostAsync(hr, $"{Employments}/{second}/statutory-registrations", new { scheme = "eobi", isApplicable = true, registrationNo = Number[..5] + " " + Number[5..] }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        Assert.Equal("errors.hrm.registration.duplicate", duplicate.Json.GetProperty("title").GetString());
        var otherScheme = await _h.PostAsync(hr, $"{Employments}/{second}/statutory-registrations", new { scheme = "pf", isApplicable = true, registrationNo = Number }, Ct);
        Assert.Equal(HttpStatusCode.Created, otherScheme.Status); // the number of another scheme is another registration

        string[] reads = ["hrm.employments.read", "hrm.employment_statutory_registrations.read"];
        (string, object)[] all = [("hrm.employments", "all"), ("hrm.employment_statutory_registrations", "all")];
        var reader = await UserWithAsync(reads, all);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(reader, $"/api/hrm/statutory-registrations/{id}/reveal", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(reader, $"{Employments}/{first}/statutory-registrations", new { scheme = "pf", isApplicable = true }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(reader, $"/api/hrm/statutory-registrations/{id}", new { registrationNo = "1", rowVersion = 1 }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.DeleteAsync(reader, $"/api/hrm/statutory-registrations/{id}?rowVersion=1", Ct)).Status);

        var elsewhere = await _h.ScopeAsync("companies", new { company_ids = new[] { other } }, Ct);
        var outside = await UserWithAsync([.. reads, "hrm.employment_statutory_registrations.create", "hrm.employment_statutory_registrations.update"], [("hrm.employments", "all"), ("hrm.employment_statutory_registrations", elsewhere)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(outside, $"{Employments}/{first}/statutory-registrations", new { scheme = "pf", isApplicable = true }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PutAsync(outside, $"/api/hrm/statutory-registrations/{id}", new { registrationNo = "1", rowVersion = 1 }, Ct)).Status);
        var hidden = await UserWithAsync([.. reads, "hrm.employment_statutory_registrations.update", "hrm.employment_statutory_registrations.reveal"], all,
            [("hrm.employment_statutory_registrations", "registration_no", "hidden")]);
        Assert.Equal("errors.access.field_not_editable", FieldError(await _h.PutAsync(hidden, $"/api/hrm/statutory-registrations/{id}", new { registrationNo = "1", rowVersion = 1 }, Ct), "registration_no"));
        Assert.Equal("errors.hrm.registration.reveal_masked", (await _h.GetAsync(hidden, $"/api/hrm/statutory-registrations/{id}/reveal", Ct)).Json.GetProperty("title").GetString());

        var full = await _h.GetAsync(hr, $"/api/hrm/statutory-registrations/{id}/reveal", Ct);
        Assert.Equal(Number, full.Text("registrationNo"));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.access_log where entity_id = $1 and actor_user_id = $2 and fields = '{registration_no}'", Ct, id, hr));
    }

    // ------------------------------------------------------------------ review S1.20d findings 1, 3, 4, 5, 6, 7

    [Fact]
    public async Task FR_HRM_EMP_18_FR_XM_32_review_1_two_waiting_requests_with_one_account_cannot_both_pass_and_the_approver_sees_the_warning()
    {
        var maker = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var first = await HireAsync(maker, company, place);
        var second = await HireAsync(maker, company, place);
        var firstPerson = await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, first);
        var secondPerson = await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, second);

        // Both wait at the same time: no warning yet, nothing stored.
        var a = await ChangeAsync(maker, first, [Add(iban: null, number: Number)]);
        var b = await ChangeAsync(maker, second, [Add(iban: null, number: Number)]);
        Assert.Equal(HttpStatusCode.Accepted, a.Status);
        Assert.Equal(HttpStatusCode.Accepted, b.Status);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(checker, a.Json.GetProperty("requestId").GetGuid())).Status);

        // The second is checked again at approval: the account now belongs to another person, so the request is void.
        var inbox = await _h.GetAsync(checker, "/api/iam/maker-checker-requests/pending", Ct);
        var card = inbox.Json.GetProperty("toDecide").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == b.Json.GetProperty("requestId").GetGuid());
        Assert.False(card.GetProperty("canApprove").GetBoolean());
        var refused = await ApproveAsync(checker, b.Json.GetProperty("requestId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.hrm.bank_account.duplicate_since", refused.Json.GetProperty("title").GetString());
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.employee_bank_accounts where employment_id = $1", Ct, second));

        // Asked again, the warning appears; the maker's "different people" travels with the request and the approver sees it.
        var warned = await ChangeAsync(maker, second, [Add(iban: null, number: Number)]);
        Assert.Equal("errors.hrm.bank_account.possible_duplicates", warned.Json.GetProperty("title").GetString());
        var answered = await ChangeAsync(maker, second, [Add(iban: null, number: Number)], duplicates: "distinct");
        Assert.True(answered.Status == HttpStatusCode.Accepted, answered.Body);
        var request = answered.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.person_duplicate_cases where person_id = $1 and match_kind = 'bank_account'", Ct, secondPerson)); // not decided by the maker
        var seen = (await _h.GetAsync(checker, "/api/iam/maker-checker-requests/pending", Ct)).Json.GetProperty("toDecide").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == request);
        var details = seen.GetProperty("details").EnumerateArray().ToList();
        var same = details.Single(d => d.GetProperty("label").GetString() == "hrm.bank_account.detail.same_account");
        Assert.StartsWith(await _h.ScalarAsync<string>("select person_no from hrm.persons where id = $1", Ct, firstPerson), same.GetProperty("names").GetProperty("en").GetString(), StringComparison.Ordinal);
        Assert.Equal("hrm.bank_account.answer.distinct", details.Single(d => d.GetProperty("label").GetString() == "hrm.bank_account.detail.answer").GetProperty("text").GetString());
        Assert.Equal("hrm.bank_account.mode.bank_transfer", details.Single(d => d.GetProperty("label").GetString() == "hrm.bank_account.detail.payment_mode").GetProperty("text").GetString());
        Assert.Equal("****" + Number[^4..], details.Single(d => d.GetProperty("label").GetString() == "changes.detail.accountNumber").GetProperty("text").GetString());

        // Approved: the case is decided as different people by the approver, not the maker.
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(checker, request)).Status);
        Assert.Equal(checker, await _h.ScalarAsync<Guid>("select decided_by from hrm.person_duplicate_cases where person_id = $1 and other_person_id = $2 and match_kind = 'bank_account' and status = 'distinct'", Ct, secondPerson, firstPerson));
    }

    [Fact]
    public async Task FR_HRM_EMP_18_review_7_a_rejected_request_leaves_no_review_case_and_an_approved_one_opens_it()
    {
        var maker = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var first = await HireAsync(maker, company, place);
        var second = await HireAsync(maker, company, place);
        var secondPerson = await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, second);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(checker, (await ChangeAsync(maker, first, [Add()])).Json.GetProperty("requestId").GetGuid())).Status);

        var held = await ChangeAsync(maker, second, [Add()], duplicates: "review");
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        var rejected = await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{held.Json.GetProperty("requestId").GetGuid()}/reject", new { reason = "not this account" }, Ct);
        Assert.Equal(HttpStatusCode.OK, rejected.Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.person_duplicate_cases where person_id = $1", Ct, secondPerson));

        var again = await ChangeAsync(maker, second, [Add()], duplicates: "review");
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(checker, again.Json.GetProperty("requestId").GetGuid())).Status);
        Assert.Equal("open", await _h.ScalarAsync<string>("select status from hrm.person_duplicate_cases where person_id = $1 and match_kind = 'bank_account'", Ct, secondPerson));
    }

    [Fact]
    public async Task FR_HRM_EMP_06_review_3_a_request_naming_an_unknown_bank_is_refused_before_it_is_held()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var employment = await HireAsync(hr, company, place);
        var refused = await _h.PostAsync(hr, $"{Employments}/{employment}/bank-accounts", new
        {
            changes = new[] { new { action = "add", paymentMode = "bank_transfer", bankId = Guid.CreateVersion7(), accountTitle = "A", iban = ValidIban, splitKind = "remainder" } },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.hrm.bank_account.bank_unknown", FieldError(refused, "bankId"));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from iam.maker_checker_requests where action_type = 'hrm.employee_bank_accounts.change' and subject_id = $1", Ct, employment));
    }

    [Fact]
    public async Task FR_HRM_EMP_06_review_5_6_two_accounts_swap_priorities_in_one_change_and_dates_on_a_change_are_refused()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync(approval: false);
        var employment = await HireAsync(hr, company, place);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(hr, employment, [Add(priority: 1), Add(iban: OtherIban, kind: "percent", value: 40, priority: 2)])).Status);
        var rows = await ListAsync(hr, employment);
        var one = rows.Single(r => r.GetProperty("priority").GetInt32() == 1);
        var two = rows.Single(r => r.GetProperty("priority").GetInt32() == 2);
        var swap = await ChangeAsync(hr, employment,
        [
            new { action = "change", id = one.GetProperty("id").GetGuid(), rowVersion = one.GetProperty("rowVersion").GetInt64(), priority = 2 },
            new { action = "change", id = two.GetProperty("id").GetGuid(), rowVersion = two.GetProperty("rowVersion").GetInt64(), priority = 1 },
        ]);
        Assert.True(swap.Status == HttpStatusCode.OK, swap.Body);
        var after = await ListAsync(hr, employment);
        Assert.Equal("percent", after.Single(r => r.GetProperty("priority").GetInt32() == 1).GetProperty("splitKind").GetString());
        Assert.Equal("remainder", after.Single(r => r.GetProperty("priority").GetInt32() == 2).GetProperty("splitKind").GetString());

        var current = after.Single(r => r.GetProperty("priority").GetInt32() == 1);
        var dated = await ChangeAsync(hr, employment, [new { action = "change", id = current.GetProperty("id").GetGuid(), rowVersion = current.GetProperty("rowVersion").GetInt64(), accountTitle = "X", validTo = Iso(Today.AddDays(30)) }]);
        Assert.Equal("errors.hrm.bank_account.dates_on_change", FieldError(dated, "changes[0].validTo"));
    }

    [Fact]
    public async Task FR_HRM_EMP_06_FR_KRN_IAM_12_review_4_approvers_outside_the_records_neither_see_nor_decide_and_own_and_team_scopes_hold()
    {
        var maker = await _h.UserAsync(Ct, null, "hr_admin");
        var (company, place) = await CompanyAsync();
        var (other, _) = await CompanyAsync();
        var managerEmployment = await HireAsync(maker, company, place);
        var employment = await HireAsync(maker, company, place, manager: managerEmployment);
        var stranger = await HireAsync(maker, company, place);
        var held = await ChangeAsync(maker, employment, [Add()]);
        var request = held.Json.GetProperty("requestId").GetGuid();

        // An approver whose bank-account scope is another company, and one who may read but not update there: neither sees nor decides.
        string[] approve = ["hrm.employments.read", "hrm.employee_bank_accounts.read", "hrm.employee_bank_accounts.approve", "iam.maker_checker_requests.read"];
        var elsewhere = await _h.ScopeAsync("companies", new { company_ids = new[] { other } }, Ct);
        var outside = await UserWithAsync(approve, [("hrm.employments", "all"), ("hrm.employee_bank_accounts", elsewhere)]);
        var pending = await _h.GetAsync(outside, "/api/iam/maker-checker-requests/pending", Ct);
        Assert.DoesNotContain(request.ToString(), pending.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(outside, request)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(outside, $"/api/iam/maker-checker-requests/{request}/reject", new { reason = "no" }, Ct)).Status);
        // An approver who may read the accounts but holds no update right on them (VisibleAsync asks read and update).
        var readOnly = await UserWithAsync(approve, [("hrm.employments", "all"), ("hrm.employee_bank_accounts", "all")]);
        Assert.DoesNotContain(request.ToString(), (await _h.GetAsync(readOnly, "/api/iam/maker-checker-requests/pending", Ct)).Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(readOnly, request)).Status);
        Assert.Equal("pending", await _h.ScalarAsync<string>("select status from iam.maker_checker_requests where id = $1", Ct, request));

        // A remove without the delete permission is refused.
        var approver = await _h.UserAsync(Ct, null, "hr_admin");
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(approver, request)).Status);
        var row = Assert.Single(await ListAsync(maker, employment));
        var noDelete = await UserWithAsync(["hrm.employments.read", "hrm.employee_bank_accounts.read", "hrm.employee_bank_accounts.update"], [("hrm.employments", "all"), ("hrm.employee_bank_accounts", "all")]);
        Assert.Equal(HttpStatusCode.Forbidden, (await ChangeAsync(noDelete, employment, [new { action = "remove", id = row.GetProperty("id").GetGuid(), rowVersion = row.GetProperty("rowVersion").GetInt64() }])).Status);

        // Own scope: the employee linked to the employment sees their own accounts (masked) and nobody else's.
        var self = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(maker, $"{Employments}/{employment}/user", new { userId = self, rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, employment) }, Ct)).Status);
        await _h.AssignAsync(self, await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employee_bank_accounts.read"], [("hrm.employments", "own"), ("hrm.employee_bank_accounts", "own")]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal("****" + ValidIban[^4..], Assert.Single(await ListAsync(self, employment)).GetProperty("iban").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(self, $"{Employments}/{stranger}/bank-accounts", Ct)).Status);

        // Team scope: the line manager sees the accounts of their direct report, not of another employee.
        var boss = await _h.UserAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(maker, $"{Employments}/{managerEmployment}/user", new { userId = boss, rowVersion = await _h.ScalarAsync<long>("select row_version from hrm.employments where id = $1", Ct, managerEmployment) }, Ct)).Status);
        await _h.AssignAsync(boss, await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employee_bank_accounts.read"], [("hrm.employments", "direct_team"), ("hrm.employee_bank_accounts", "direct_team")]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Single(await ListAsync(boss, employment));
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(boss, $"{Employments}/{stranger}/bank-accounts", Ct)).Status);

        // A registration readable but outside the update scope is refused.
        var registration = await _h.PostAsync(maker, $"{Employments}/{employment}/statutory-registrations", new { scheme = "eobi", isApplicable = true }, Ct);
        var readable = await _h.UserAsync(Ct);
        await _h.AssignAsync(readable, await _h.RoleAsync(Ct, ["hrm.employments.read", "hrm.employment_statutory_registrations.read"],
            [("hrm.employments", "all"), ("hrm.employment_statutory_registrations", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        await _h.AssignAsync(readable, await _h.RoleAsync(Ct, ["hrm.employment_statutory_registrations.update"], [("hrm.employment_statutory_registrations", elsewhere)]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(readable, $"/api/hrm/statutory-registrations/{registration.Json.GetProperty("id").GetGuid()}", new { registrationNo = "1", rowVersion = 1 }, Ct)).Status);
    }
}
