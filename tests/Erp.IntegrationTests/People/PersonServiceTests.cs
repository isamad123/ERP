#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.People;

/// <summary>
/// The person services of slice S1.20a (Build items 1 to 4) through the real API pipeline, the real access engine and a real
/// PostgreSQL: persons and their sections (FR-HRM-EMP-01, FR-HRM-EMP-15, FR-XM-28), identifiers with pack validation, encryption
/// and masking (FR-HRM-EMP-05, FR-HRM-EMP-16, GHR-02), duplicate detection and the review queue (FR-HRM-EMP-18), merge with
/// maker-checker (rulings Q4, Q5; FR-XM-32), data scopes and field security on every write, audit with the acting user (GHR-09).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class PersonServiceTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Persons = "/api/hrm/persons";
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

    private static string Cnic() => string.Concat(Enumerable.Range(0, 13).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Dashed(string cnic) => $"{cnic[..5]}-{cnic[5..12]}-{cnic[12..]}";

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static string Birth() => new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(0, 12000)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(Guid Id, long Version, string PersonNo)> CreateAsync(Guid user, object body)
    {
        var answer = await _h.PostAsync(user, Persons, body, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        return (answer.Json.GetProperty("id").GetGuid(), answer.Json.GetProperty("rowVersion").GetInt64(), answer.Json.GetProperty("personNo").GetString()!);
    }

    private Task<(Guid Id, long Version, string PersonNo)> PlainAsync(Guid user, string? birth = null, string? first = null) =>
        CreateAsync(user, new { firstName = first ?? Unique("Plain"), lastName = "Person", gender = "male", dateOfBirth = birth });

    private async Task<JsonElement> RowsAsync(Guid user, string list, Guid person)
    {
        var answer = await _h.GetAsync(user, $"{list}?personId={person}&limit=200", Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
        return answer.Json.GetProperty("items");
    }

    private Task<Guid> EmployAsync(Guid person, Guid company, string status = "active") =>
        _h.RowAsync("hrm.employments", company, new Dictionary<string, object?> { ["person_id"] = person, ["status"] = status, ["is_primary"] = true }, Ct)
            .ContinueWith(t => (Guid)t.Result["id"]!, TaskScheduler.Default);

    /// <summary>The merge-approval setting changes only with a second administrator (review S1.20a finding 3, FR-XM-32).</summary>
    private async Task SetMergeApprovalAsync(Guid admin, Guid second, bool on)
    {
        var held = await _h.PutAsync(admin, "/api/cfg/settings/hrm.person_merge.requires_approval", new { layer = "tenant", value = on, reason = "test" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(admin, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct)).Status);
        var approved = await _h.PostAsync(second, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "agreed" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
    }

    private Task<string> MergeReasonAsync(Guid person) =>
        _h.ScalarAsync<string>("""
            select c->>'after' from aud.change_log l, jsonb_array_elements(l.changes) c
            where l.entity_type = 'hrm.persons' and l.entity_id = $1 and c->>'field' = 'reason'
              and exists (select 1 from jsonb_array_elements(l.changes) e where e->>'field' = 'event' and e->>'after' = 'hrm.person_merged')
            """, Ct, person);

    // ------------------------------------------------------------------ persons and sections

    [Fact]
    public async Task FR_HRM_EMP_01_FR_HRM_EMP_15_FR_XM_28_GHR_09_a_person_with_every_section_is_created_numbered_and_audited_with_the_acting_user()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var cnic = Cnic();
        var (id, version, personNo) = await CreateAsync(hr, new
        {
            firstName = "Ayesha", middleName = "Noor", lastName = Unique("Khan"), nameLocal = "عائشہ", fatherOrSpouseName = "Imran Khan",
            gender = "female", dateOfBirth = Birth(), maritalStatus = "married", nationalityCode = "PK",
            identifiers = new[] { new { kind = "cnic", countryCode = "PK", value = cnic, expiryDate = "2031-05-01" } },
            contacts = new[] { new { kind = "mobile", value = "0300-1234567", isPrimary = true } },
            addresses = new[] { new { kind = "permanent", address = new { line1 = "House 7", city = "Lahore" }, validFrom = "2015-01-01" } },
            relations = new object[] { new { relation = "child", fullName = "Sara", dateOfBirth = "2015-02-02", isDependent = true, identifier = "35202-1111111-1" }, new { relation = "spouse", fullName = "Imran", isDependent = false } },
            qualifications = new[] { new { kind = "education", title = "MBA", institution = "LUMS" } },
        });
        Assert.Equal(1, version);
        Assert.Matches(@"^P-\d{6}$", personNo); // kernel numbering, tenant-wide series (FR-HRM-EMP-04 default P-{seq:6})

        // Read back through the generated reads: one person, its sections; the identifier is masked (last four only).
        var read = await _h.GetAsync(hr, $"{Persons}/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal("Ayesha", read.Json.GetProperty("firstName").GetString());
        Assert.Equal("married", read.Json.GetProperty("maritalStatus").GetString());
        var identifier = (await RowsAsync(hr, "/api/hrm/person-identifiers", id))[0];
        Assert.Equal(cnic[^4..], identifier.GetProperty("valueLast4").GetString());
        Assert.False(identifier.TryGetProperty("value", out _));
        Assert.Equal(2, (await RowsAsync(hr, "/api/hrm/person-relations", id)).GetArrayLength()); // BR-03: separate records
        Assert.Equal(1, (await RowsAsync(hr, "/api/hrm/person-qualifications", id)).GetArrayLength());

        // Stored encrypted: neither the CNIC nor the relation's identifier is in the database as text.
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.person_identifiers where position(convert_to($1, 'UTF8') in value) > 0", Ct, cnic));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.person_relations where position(convert_to('35202', 'UTF8') in identifier) > 0 and person_id = $1", Ct, id));

        // GHR-09: every row's creation is in the change log under the acting HR user.
        Assert.Equal(7L, await _h.ScalarAsync<long>("""
            select count(*) from aud.change_log where actor_user_id = $1 and action = 'create'
              and entity_id in (select id from hrm.persons where id = $2 union all select id from hrm.person_identifiers where person_id = $2
                                union all select id from hrm.person_contacts where person_id = $2 union all select id from hrm.person_addresses where person_id = $2
                                union all select id from hrm.person_relations where person_id = $2 union all select id from hrm.person_qualifications where person_id = $2)
            """, Ct, hr, id));
    }

    [Fact]
    public async Task FR_HRM_EMP_01_person_fields_are_validated_and_religion_and_custom_fields_wait_for_their_slices()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var bad = await _h.PostAsync(hr, Persons, new { firstName = " ", gender = "x", dateOfBirth = "2999-01-01", maritalStatus = "complicated", religion = "any", custom = new { a = 1 } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var errors = bad.Json.GetProperty("errors");
        Assert.Equal("errors.hrm.field.required", errors.GetProperty("firstName")[0].GetString());
        Assert.Equal("errors.hrm.field.unknown_code", errors.GetProperty("gender")[0].GetString());
        Assert.Equal("errors.hrm.date.future", errors.GetProperty("dateOfBirth")[0].GetString());
        Assert.Equal("errors.hrm.field.unknown_code", errors.GetProperty("maritalStatus")[0].GetString());
        Assert.Equal("errors.hrm.person.religion_not_asked", errors.GetProperty("religion")[0].GetString());
        Assert.Equal("errors.hrm.person.custom_not_available", errors.GetProperty("custom")[0].GetString());

        var country = await _h.PostAsync(hr, Persons, new { firstName = Unique("N"), gender = "male", nationalityCode = "QQ" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, country.Status); // not in ref.countries: the foreign key refuses it
        Assert.Equal("errors.data.reference_not_found", country.Json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task FR_HRM_EMP_01_a_change_is_concurrency_checked_and_clears_only_optional_fields()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (id, version, _) = await PlainAsync(hr, Birth());
        var changed = await _h.PutAsync(hr, $"{Persons}/{id}", new { lastName = "Changed", clear = new[] { "dateOfBirth" }, rowVersion = version }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        Assert.Equal(version + 1, changed.Json.GetProperty("rowVersion").GetInt64());
        var stale = await _h.PutAsync(hr, $"{Persons}/{id}", new { lastName = "Again", rowVersion = version }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        var clearName = await _h.PutAsync(hr, $"{Persons}/{id}", new { clear = new[] { "firstName" }, rowVersion = version + 1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, clearName.Status);
        var read = await _h.GetAsync(hr, $"{Persons}/{id}", Ct);
        Assert.Equal("Changed", read.Json.GetProperty("lastName").GetString());
        Assert.Equal(JsonValueKind.Null, read.Json.GetProperty("dateOfBirth").ValueKind);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_id = $1 and action = 'update' and actor_user_id = $2", Ct, id, hr));
    }

    [Fact]
    public async Task S120a_generated_writes_of_the_person_tables_are_closed_ruling_Q1()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (id, _, _) = await PlainAsync(hr);
        var contact = await _h.PostAsync(hr, $"{Persons}/{id}/contacts", new { kind = "phone", value = "042-1" }, Ct);
        Assert.Equal(HttpStatusCode.Created, contact.Status);
        foreach (var (method, path) in new[] { ("POST", "/api/hrm/person-contacts"), ("PUT", $"/api/hrm/person-contacts/{contact.Json.GetProperty("id").GetGuid()}"), ("DELETE", $"/api/hrm/persons/{id}"), ("POST", "/api/hrm/nominations") })
        {
            var answer = method switch
            {
                "POST" => await _h.PostAsync(hr, path, new { personId = id, kind = "phone", value = "x" }, Ct),
                "PUT" => await _h.PutAsync(hr, path, new { value = "x", rowVersion = 1 }, Ct),
                _ => await _h.DeleteAsync(hr, path + "?rowVersion=1", Ct),
            };
            Assert.True(answer.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {path}: {(int)answer.Status}");
        }
    }

    // ------------------------------------------------------------------ identifiers

    [Fact]
    public async Task FR_HRM_EMP_05_FR_HRM_EMP_16_identifiers_follow_the_pack_catalogue_rules_ruling_Q3()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (id, _, _) = await PlainAsync(hr);
        async Task<Answer> Add(object body) => await _h.PostAsync(hr, $"{Persons}/{id}/identifiers", body, Ct);

        Assert.Equal("errors.hrm.identifier.format", (await Add(new { kind = "cnic", countryCode = "PK", value = "12345-123" })).Json.GetProperty("errors").GetProperty("value")[0].GetString());
        Assert.Equal("errors.hrm.identifier.format", (await Add(new { kind = "cnic", countryCode = "PK", value = "1234-51234567-1" })).Json.GetProperty("errors").GetProperty("value")[0].GetString());
        Assert.Equal("errors.hrm.identifier.unknown_kind", (await Add(new { kind = "iqama", countryCode = "PK", value = "1" })).Json.GetProperty("errors").GetProperty("kind")[0].GetString());
        Assert.Equal("errors.hrm.identifier.no_expiry", (await Add(new { kind = "ntn", countryCode = "PK", value = "1234567-8", expiryDate = "2030-01-01" })).Json.GetProperty("errors").GetProperty("expiryDate")[0].GetString());

        // 13 digits with or without the dash layout; stored in the dash layout; a passport of any country.
        var cnic = Cnic();
        Assert.Equal(HttpStatusCode.Created, (await Add(new { kind = "cnic", countryCode = "PK", value = cnic, issueDate = "2020-01-01", expiryDate = "2030-01-01" })).Status);
        Assert.Equal(HttpStatusCode.Created, (await Add(new { kind = "passport", countryCode = "GB", value = Unique("P").ToUpperInvariant()[..9] })).Status);
        var catalogue = await _h.GetAsync(hr, "/api/hrm/person-catalogue", Ct);
        Assert.Contains(catalogue.Json.GetProperty("identifierKinds").EnumerateArray(), k => k.GetProperty("code").GetString() == "cnic" && k.GetProperty("status").GetString() == "provisional");

        var row = (await RowsAsync(hr, "/api/hrm/person-identifiers", id)).EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "cnic");
        var reveal = await _h.GetAsync(hr, $"{Persons}/{id}/identifiers/{row.GetProperty("id").GetGuid()}/value", Ct);
        Assert.Equal(Dashed(cnic), reveal.Json.GetProperty("value").GetString());
    }

    [Fact]
    public async Task FR_HRM_EMP_18_an_identical_CNIC_on_a_second_person_is_refused_whatever_its_layout()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var cnic = Cnic();
        var (first, _, firstNo) = await CreateAsync(hr, new { firstName = Unique("A"), gender = "male", identifiers = new[] { new { kind = "cnic", countryCode = "PK", value = cnic } } });

        var again = await _h.PostAsync(hr, Persons, new { firstName = Unique("B"), gender = "male", identifiers = new[] { new { kind = "cnic", countryCode = "PK", value = Dashed(cnic) } } }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal("errors.hrm.identifier.duplicate", again.Json.GetProperty("title").GetString());
        var match = again.Json.GetProperty("matches")[0];
        Assert.Equal(first, match.GetProperty("personId").GetGuid()); // the HR user may see that person: named, for a rehire (BR-01)
        Assert.Equal(firstNo, match.GetProperty("personNo").GetString());

        var (other, _, _) = await PlainAsync(hr);
        var added = await _h.PostAsync(hr, $"{Persons}/{other}/identifiers", new { kind = "cnic", countryCode = "PK", value = cnic }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, added.Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from hrm.person_identifiers where person_id = $1", Ct, other));
    }

    [Fact]
    public async Task GHR_02_FR_HRM_EMP_05_a_full_identifier_needs_the_reveal_permission_and_every_view_is_in_the_access_log()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (id, _, _) = await CreateAsync(hr, new { firstName = Unique("R"), gender = "female", identifiers = new[] { new { kind = "cnic", countryCode = "PK", value = Cnic() } } });
        var identifier = (await RowsAsync(hr, "/api/hrm/person-identifiers", id))[0].GetProperty("id").GetGuid();
        var path = $"{Persons}/{id}/identifiers/{identifier}/value";

        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, ["hrm.persons.read", "hrm.person_identifiers.read"], [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(reader, path, Ct)).Status);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.access_log where entity_id = $1", Ct, identifier));

        var masked = await _h.UserAsync(Ct);
        await _h.AssignAsync(masked, await _h.RoleAsync(Ct, ["hrm.persons.read", "hrm.person_identifiers.read", "hrm.person_identifiers.reveal"], [("*", "all")],
            [("hrm.person_identifiers", "value_last4", "masked")]), null, _h.Today.AddDays(-1), null, Ct);
        var refused = await _h.GetAsync(masked, path, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("errors.hrm.identifier.reveal_masked", refused.Json.GetProperty("title").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(hr, path, Ct)).Status);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.access_log where entity_id = $1 and actor_user_id = $2 and fields = '{value}' and action = 'view'", Ct, identifier, hr));
    }

    // ------------------------------------------------------------------ duplicates

    [Fact]
    public async Task FR_HRM_EMP_18_same_name_and_birth_date_is_a_warning_that_HR_confirms_or_sends_to_the_review_queue()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var first = Unique("Bilal");
        var birth = Birth();
        var (existing, _, _) = await PlainAsync(hr, birth, first);

        // Asked: refused with the match, nothing created.
        var count = await _h.ScalarAsync<long>("select count(*) from hrm.persons", Ct);
        var asked = await _h.PostAsync(hr, Persons, new { firstName = "  " + first.ToUpperInvariant(), lastName = "person", gender = "male", dateOfBirth = birth }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, asked.Status);
        Assert.Equal("errors.hrm.person.possible_duplicates", asked.Json.GetProperty("title").GetString());
        Assert.Equal(existing, asked.Json.GetProperty("matches")[0].GetProperty("personId").GetGuid());
        Assert.Equal(count, await _h.ScalarAsync<long>("select count(*) from hrm.persons", Ct));

        // Sent to review: created, with an open case listed in the queue.
        var (reviewed, _, _) = await CreateAsync(hr, new { firstName = first, lastName = "Person", gender = "male", dateOfBirth = birth, duplicates = "review" });
        var queue = await _h.GetAsync(hr, "/api/hrm/duplicates?limit=200", Ct);
        var item = queue.Json.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("person").GetProperty("id").GetGuid() == reviewed);
        Assert.Equal(existing, item.GetProperty("other").GetProperty("id").GetGuid());
        Assert.Equal("name_and_birth_date", item.GetProperty("matchKind").GetString());

        // HR decides they are two people: closed as distinct, by whom and when.
        var caseId = item.GetProperty("id").GetGuid();
        var decided = await _h.PostAsync(hr, $"/api/hrm/duplicates/{caseId}/distinct", new { note = "different fathers", rowVersion = item.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.True(decided.Status == HttpStatusCode.OK, decided.Body);
        Assert.Equal("distinct", await _h.ScalarAsync<string>("select status from hrm.person_duplicate_cases where id = $1", Ct, caseId));
        Assert.Equal(hr, await _h.ScalarAsync<Guid>("select decided_by from hrm.person_duplicate_cases where id = $1", Ct, caseId));

        // Confirmed distinct at once: created, the confirmation recorded as a decided case.
        var (confirmed, _, _) = await CreateAsync(hr, new { firstName = first, lastName = "Person", gender = "male", dateOfBirth = birth, duplicates = "distinct" });
        Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from hrm.person_duplicate_cases where person_id = $1 and status = 'distinct' and decided_by = $2", Ct, confirmed, hr));
    }

    // ------------------------------------------------------------------ merge

    [Fact]
    public async Task FR_HRM_EMP_18_FR_XM_32_a_merge_waits_for_a_second_person_moves_the_records_and_retires_the_duplicate()
    {
        var maker = await _h.UserAsync(Ct, null, "hr_admin");
        var checker = await _h.UserAsync(Ct, null, "hr_admin");
        var company = await _h.DefaultCompanyAsync(Ct);
        var (dup, _, dupNo) = await CreateAsync(maker, new
        {
            firstName = Unique("Dup"), gender = "male",
            identifiers = new[] { new { kind = "passport", countryCode = "PK", value = Unique("M").ToUpperInvariant()[..9] } },
            contacts = new[] { new { kind = "mobile", value = "0300-0000000", isPrimary = true } },
            addresses = new[] { new { kind = "current", address = new { line1 = "Flat 2" }, validFrom = "2022-01-01" } },
            qualifications = new[] { new { kind = "language", title = "Punjabi" } },
        });
        var (survivor, _, survivorNo) = await CreateAsync(maker, new
        {
            firstName = Unique("Keep"), gender = "male",
            contacts = new[] { new { kind = "mobile", value = "0311-1111111", isPrimary = true } },
        });
        var employment = await EmployAsync(dup, company);

        var held = await _h.PostAsync(maker, $"{Persons}/{dup}/merge", new { intoPersonId = survivor, reason = "same CNIC on paper file" }, Ct);
        Assert.True(held.Status == HttpStatusCode.Accepted, held.Body);
        var request = held.Json.GetProperty("requestId").GetGuid();
        Assert.Equal(dup, await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, employment)); // nothing moved yet

        // The requester cannot decide it (FR-XM-32); the second HR administrator sees it described in the inbox.
        var own = await _h.PostAsync(maker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, own.Status);
        Assert.Equal("errors.maker_checker.own_request", own.Json.GetProperty("title").GetString());
        var inbox = await _h.GetAsync(checker, "/api/iam/maker-checker-requests/pending", Ct);
        Assert.True(inbox.Status == HttpStatusCode.OK, inbox.Body);
        var card = inbox.Json.GetProperty("toDecide").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == request);
        Assert.Equal("hrm.person.merge", card.GetProperty("kind").GetString());
        Assert.True(card.GetProperty("canApprove").GetBoolean());
        Assert.StartsWith(dupNo, card.GetProperty("subject").GetProperty("en").GetString(), StringComparison.Ordinal);
        Assert.StartsWith(survivorNo, card.GetProperty("details")[0].GetProperty("names").GetProperty("en").GetString(), StringComparison.Ordinal);
        var mine = await _h.GetAsync(maker, "/api/iam/maker-checker-requests/pending", Ct);
        Assert.False(mine.Json.GetProperty("mine").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == request).GetProperty("canApprove").GetBoolean());

        var approved = await _h.PostAsync(checker, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "checked the file" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);

        // Moved to the survivor: employment, identifier, qualification, contact (not primary: the survivor has one); address kind it lacked.
        Assert.Equal(survivor, await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, employment));
        Assert.Equal(0L, await _h.ScalarAsync<long>("""
            select (select count(*) from hrm.person_identifiers where person_id = $1) + (select count(*) from hrm.person_qualifications where person_id = $1)
                 + (select count(*) from hrm.person_contacts where person_id = $1) + (select count(*) from hrm.person_addresses where person_id = $1)
            """, Ct, dup));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.person_contacts where person_id = $1 and is_primary", Ct, survivor));
        Assert.Equal(survivor, await _h.ScalarAsync<Guid>("select merged_into_person_id from hrm.persons where id = $1", Ct, dup));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where actor_user_id = $1 and action = 'update' and entity_id in ($2, $3)", Ct, checker, dup, employment) >= 2);
        Assert.Equal("approved", await _h.ScalarAsync<string>("select status from iam.maker_checker_requests where id = $1", Ct, request));
        Assert.Equal("same CNIC on paper file", await MergeReasonAsync(dup)); // the merge audit keeps the requester's reason (review finding 5)

        // The retired person keeps its id and history but cannot be used.
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(maker, $"{Persons}/{dup}", Ct)).Status);
        var change = await _h.PutAsync(maker, $"{Persons}/{dup}", new { lastName = "x", rowVersion = 2 }, Ct);
        Assert.Equal("errors.hrm.person.retired", change.Json.GetProperty("title").GetString());
        var section = await _h.PostAsync(maker, $"{Persons}/{dup}/contacts", new { kind = "phone", value = "1" }, Ct);
        Assert.Equal("errors.hrm.person.retired", section.Json.GetProperty("title").GetString());
        var again = await _h.PostAsync(maker, $"{Persons}/{survivor}/merge", new { intoPersonId = dup, reason = "back" }, Ct);
        Assert.Equal("errors.hrm.person.retired", again.Json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task FR_HRM_EMP_18_ruling_Q4_a_merge_is_refused_when_both_persons_have_an_active_primary_employment_and_applies_at_once_with_approval_off()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var admin = await _h.UserAsync(Ct, null, "tenant_admin", "hr_admin");
        var secondAdmin = await _h.UserAsync(Ct, null, "tenant_admin");
        var company = await _h.DefaultCompanyAsync(Ct);
        var (a, _, _) = await PlainAsync(hr);
        var (b, _, _) = await PlainAsync(hr);
        var (c, _, _) = await PlainAsync(hr);
        await EmployAsync(a, company);
        await EmployAsync(b, company, "on_notice");
        await EmployAsync(c, company, "separated");

        var both = await _h.PostAsync(hr, $"{Persons}/{a}/merge", new { intoPersonId = b, reason = "duplicate" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, both.Status);
        Assert.Equal("errors.hrm.merge.both_employed", both.Json.GetProperty("title").GetString());
        Assert.Equal("errors.hrm.merge.same_person", (await _h.PostAsync(hr, $"{Persons}/{a}/merge", new { intoPersonId = a, reason = "x" }, Ct)).Json.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.PostAsync(hr, $"{Persons}/{a}/merge", new { intoPersonId = c }, Ct)).Status); // reason required

        await SetMergeApprovalAsync(admin, secondAdmin, on: false);
        try
        {
            var merged = await _h.PostAsync(hr, $"{Persons}/{c}/merge", new { intoPersonId = a, reason = "duplicate of the active one" }, Ct);
            Assert.True(merged.Status == HttpStatusCode.OK, merged.Body);
            Assert.Equal(a, await _h.ScalarAsync<Guid>("select merged_into_person_id from hrm.persons where id = $1", Ct, c));
            Assert.Equal(2L, await _h.ScalarAsync<long>("select count(*) from hrm.employments where person_id = $1", Ct, a));
            Assert.Equal("duplicate of the active one", await MergeReasonAsync(c)); // recorded without approval too (review finding 5)
        }
        finally
        {
            await SetMergeApprovalAsync(admin, secondAdmin, on: true);
        }
    }

    // ------------------------------------------------------------------ data scope and field security

    [Fact]
    public async Task FR_HRM_ROL_02_a_company_scoped_role_changes_only_persons_employed_in_its_company()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var mine = await _h.CompanyAsync(Ct);
        var theirs = await _h.CompanyAsync(Ct);
        var (inside, insideVersion, _) = await PlainAsync(hr);
        var (outside, outsideVersion, _) = await PlainAsync(hr);
        await EmployAsync(inside, mine);
        await EmployAsync(outside, theirs);

        var clerk = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["hrm.persons.read", "hrm.persons.update", "hrm.persons.create", "hrm.person_contacts.create", "hrm.persons.merge"], [("*", "all")]);
        await _h.AssignAsync(clerk, role, mine, _h.Today.AddDays(-1), null, Ct);

        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(clerk, $"{Persons}/{inside}", new { lastName = "Mine", rowVersion = insideVersion }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PutAsync(clerk, $"{Persons}/{outside}", new { lastName = "Theirs", rowVersion = outsideVersion }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(clerk, $"{Persons}/{outside}/contacts", new { kind = "phone", value = "1" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(clerk, $"{Persons}/{inside}/merge", new { intoPersonId = outside, reason = "x" }, Ct)).Status);
        // A free-standing person belongs to no company yet, so a company-limited role cannot create one (S1.20b hires: R-111).
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(clerk, Persons, new { firstName = Unique("Free"), gender = "male" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(await _h.UserAsync(Ct, null, "employee"), $"{Persons}/{inside}", new { lastName = "x", rowVersion = insideVersion + 1 }, Ct)).Status);
    }

    [Fact]
    public async Task FR_KRN_IAM_13_a_field_the_role_may_not_edit_is_refused_by_name()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (id, version, _) = await PlainAsync(hr, Birth());
        var clerk = await _h.UserAsync(Ct);
        await _h.AssignAsync(clerk, await _h.RoleAsync(Ct, ["hrm.persons.read", "hrm.persons.update"], [("*", "all")], [("hrm.persons", "date_of_birth", "read_only")]), null, _h.Today.AddDays(-1), null, Ct);

        var refused = await _h.PutAsync(clerk, $"{Persons}/{id}", new { dateOfBirth = "1990-01-01", rowVersion = version }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("errors.access.field_not_editable", refused.Json.GetProperty("errors").GetProperty("date_of_birth")[0].GetString());
        Assert.Equal(HttpStatusCode.OK, (await _h.PutAsync(clerk, $"{Persons}/{id}", new { lastName = "Allowed", rowVersion = version }, Ct)).Status);
    }

    // ------------------------------------------------------------------ addresses and contacts

    [Fact]
    public async Task GHR_03_addresses_are_effective_dated_by_kind_and_a_new_one_fits_into_the_history()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (id, _, _) = await PlainAsync(hr);
        async Task<Answer> Add(string kind, string from) => await _h.PostAsync(hr, $"{Persons}/{id}/addresses", new { kind, address = new { line1 = from }, validFrom = from }, Ct);
        Assert.Equal(HttpStatusCode.Created, (await Add("permanent", "2020-01-01")).Status);
        Assert.Equal(HttpStatusCode.Created, (await Add("permanent", "2024-06-01")).Status);
        Assert.Equal(HttpStatusCode.Created, (await Add("permanent", "2022-03-01")).Status); // between the two
        Assert.Equal(HttpStatusCode.Created, (await Add("current", "2021-01-01")).Status); // another kind: its own history
        Assert.Equal("errors.hrm.address.same_start", (await Add("permanent", "2022-03-01")).Json.GetProperty("title").GetString());

        var history = (await RowsAsync(hr, "/api/hrm/person-addresses", id)).EnumerateArray().Where(a => a.GetProperty("kind").GetString() == "permanent")
            .OrderBy(a => a.GetProperty("validFrom").GetString(), StringComparer.Ordinal)
            .Select(a => (a.GetProperty("validFrom").GetString(), a.GetProperty("validTo").ValueKind == JsonValueKind.Null ? null : a.GetProperty("validTo").GetString())).ToList();
        Assert.Equal([("2020-01-01", "2022-03-01"), ("2022-03-01", "2024-06-01"), ("2024-06-01", (string?)null)], history);

        var row = (await RowsAsync(hr, "/api/hrm/person-addresses", id)).EnumerateArray().First(a => a.GetProperty("kind").GetString() == "current");
        var moved = await _h.PutAsync(hr, $"{Persons}/{id}/addresses/{row.GetProperty("id").GetGuid()}", new { validFrom = "2021-02-01", address = new { line1 = "x" }, rowVersion = row.GetProperty("rowVersion").GetInt64() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, moved.Status); // a move is a new address from its date, never an edit of the dates
    }

    [Fact]
    public async Task FR_HRM_EMP_01_one_primary_contact_per_kind_and_a_removed_section_row_stays_in_the_change_log()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (id, _, _) = await CreateAsync(hr, new { firstName = Unique("C"), gender = "female", contacts = new[] { new { kind = "mobile", value = "0300-1", isPrimary = true } } });
        var second = await _h.PostAsync(hr, $"{Persons}/{id}/contacts", new { kind = "mobile", value = "0300-2", isPrimary = true }, Ct);
        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from hrm.person_contacts where person_id = $1 and is_primary", Ct, id));
        Assert.Equal(second.Json.GetProperty("id").GetGuid(), await _h.ScalarAsync<Guid>("select id from hrm.person_contacts where person_id = $1 and is_primary", Ct, id));

        var removed = await _h.DeleteAsync(hr, $"{Persons}/{id}/contacts/{second.Json.GetProperty("id").GetGuid()}?rowVersion=1", Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.Status);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_id = $1 and action = 'delete'", Ct, second.Json.GetProperty("id").GetGuid()));
    }

    // ------------------------------------------------------------------ review S1.20a findings 2, 4, 7, 10

    [Fact]
    public async Task FR_HRM_EMP_18_FR_HRM_ROL_02_review_2_a_held_merge_is_neither_shown_nor_decidable_outside_the_deciders_data_scope()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var companyA = await _h.CompanyAsync(Ct);
        var companyB = await _h.CompanyAsync(Ct);
        var (dup, _, dupNo) = await PlainAsync(hr);
        var (keep, _, _) = await PlainAsync(hr);
        await EmployAsync(dup, companyA, "separated");
        await EmployAsync(keep, companyA, "separated");
        var held = await _h.PostAsync(hr, $"{Persons}/{dup}/merge", new { intoPersonId = keep, reason = "duplicate" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, held.Status);
        var request = held.Json.GetProperty("requestId").GetGuid();

        // An HR officer of company B with the merge permission: the request is not in their inbox and they cannot decide it.
        var officerB = await _h.UserAsync(Ct);
        await _h.AssignAsync(officerB, await _h.RoleAsync(Ct, ["hrm.persons.read", "hrm.persons.update", "hrm.persons.merge"], [("*", "all")]), companyB, _h.Today.AddDays(-1), null, Ct);
        var inbox = await _h.GetAsync(officerB, "/api/iam/maker-checker-requests/pending", Ct);
        Assert.True(inbox.Status == HttpStatusCode.OK, inbox.Body);
        Assert.DoesNotContain(request.ToString(), inbox.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(dupNo, inbox.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(officerB, $"/api/iam/maker-checker-requests/{request}/reject", new { reason = "no" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.PostAsync(officerB, $"/api/iam/maker-checker-requests/{request}/approve", new { reason = "ok" }, Ct)).Status);
        Assert.Equal("pending", await _h.ScalarAsync<string>("select status from iam.maker_checker_requests where id = $1", Ct, request));

        // An officer of company A sees and decides it.
        var officerA = await _h.UserAsync(Ct);
        await _h.AssignAsync(officerA, await _h.RoleAsync(Ct, ["hrm.persons.read", "hrm.persons.update", "hrm.persons.merge", "hrm.employments.read"], [("*", "all")]), companyA, _h.Today.AddDays(-1), null, Ct);
        Assert.Contains(request.ToString(), (await _h.GetAsync(officerA, "/api/iam/maker-checker-requests/pending", Ct)).Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await _h.PostAsync(officerA, $"/api/iam/maker-checker-requests/{request}/reject", new { reason = "not the same person" }, Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_EMP_18_FR_HRM_ROL_02_review_4_a_merge_never_moves_an_employment_outside_the_callers_scope()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var companyA = await _h.CompanyAsync(Ct);
        var companyB = await _h.CompanyAsync(Ct);
        var (dup, _, _) = await PlainAsync(hr);
        var (keep, _, _) = await PlainAsync(hr);
        await EmployAsync(dup, companyA, "separated");
        var foreign = await EmployAsync(dup, companyB, "separated");
        await EmployAsync(keep, companyA, "separated");

        var officerA = await _h.UserAsync(Ct);
        await _h.AssignAsync(officerA, await _h.RoleAsync(Ct, ["hrm.persons.read", "hrm.persons.update", "hrm.persons.merge", "hrm.employments.read"], [("*", "all")]), companyA, _h.Today.AddDays(-1), null, Ct);
        var refused = await _h.PostAsync(officerA, $"{Persons}/{dup}/merge", new { intoPersonId = keep, reason = "duplicate" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("errors.hrm.merge.employment_out_of_scope", refused.Json.GetProperty("title").GetString());
        Assert.Equal(dup, await _h.ScalarAsync<Guid>("select person_id from hrm.employments where id = $1", Ct, foreign));
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from iam.maker_checker_requests where subject_id = $1", Ct, dup));

        // The HR administrator for all companies may ask for it.
        Assert.Equal(HttpStatusCode.Accepted, (await _h.PostAsync(hr, $"{Persons}/{dup}/merge", new { intoPersonId = keep, reason = "duplicate" }, Ct)).Status);
    }

    [Fact]
    public async Task FR_HRM_EMP_18_review_7_confirming_a_different_person_at_create_needs_the_review_permission()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var first = Unique("Rev");
        var birth = Birth();
        await PlainAsync(hr, birth, first);
        var clerk = await _h.UserAsync(Ct);
        await _h.AssignAsync(clerk, await _h.RoleAsync(Ct, ["hrm.persons.read", "hrm.persons.create"], [("*", "all")]), null, _h.Today.AddDays(-1), null, Ct);
        var body = new { firstName = first, lastName = "Person", gender = "male", dateOfBirth = birth, duplicates = "distinct" };
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(clerk, Persons, body, Ct)).Status);
        Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(clerk, Persons, body with { duplicates = "review" }, Ct)).Status);
    }

    [Fact]
    public async Task GHR_03_review_10_removing_an_address_leaves_no_gap_in_the_history()
    {
        var hr = await _h.UserAsync(Ct, null, "hr_admin");
        var (id, _, _) = await PlainAsync(hr);
        foreach (var from in new[] { "2020-01-01", "2022-01-01", "2024-01-01" })
        {
            Assert.Equal(HttpStatusCode.Created, (await _h.PostAsync(hr, $"{Persons}/{id}/addresses", new { kind = "permanent", address = new { line1 = from }, validFrom = from }, Ct)).Status);
        }

        async Task<List<(string?, string?)>> HistoryAsync() => (await RowsAsync(hr, "/api/hrm/person-addresses", id)).EnumerateArray()
            .OrderBy(a => a.GetProperty("validFrom").GetString(), StringComparer.Ordinal)
            .Select(a => (a.GetProperty("validFrom").GetString(), a.GetProperty("validTo").ValueKind == JsonValueKind.Null ? null : a.GetProperty("validTo").GetString())).ToList();
        var middle = (await RowsAsync(hr, "/api/hrm/person-addresses", id)).EnumerateArray().Single(a => a.GetProperty("validFrom").GetString() == "2022-01-01");
        Assert.Equal(HttpStatusCode.NoContent, (await _h.DeleteAsync(hr, $"{Persons}/{id}/addresses/{middle.GetProperty("id").GetGuid()}?rowVersion={middle.GetProperty("rowVersion").GetInt64()}", Ct)).Status);
        Assert.Equal([("2020-01-01", "2024-01-01"), ("2024-01-01", (string?)null)], await HistoryAsync());
        var last = (await RowsAsync(hr, "/api/hrm/person-addresses", id)).EnumerateArray().Single(a => a.GetProperty("validFrom").GetString() == "2024-01-01");
        Assert.Equal(HttpStatusCode.NoContent, (await _h.DeleteAsync(hr, $"{Persons}/{id}/addresses/{last.GetProperty("id").GetGuid()}?rowVersion={last.GetProperty("rowVersion").GetInt64()}", Ct)).Status);
        Assert.Equal([("2020-01-01", (string?)null)], await HistoryAsync());
    }
}
