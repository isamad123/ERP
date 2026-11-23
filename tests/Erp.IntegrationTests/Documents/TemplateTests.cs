using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Documents.Templates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.IntegrationTests.Documents;

/// <summary>The data a test record offers to templates (stands in for the owning module's provider).</summary>
public sealed class TestTemplateBook
{
    public ConcurrentDictionary<Guid, (Guid Company, Dictionary<string, object?> Values)> Records { get; } = new();
}

/// <summary>A module's provider for the test record kind: fields with their class, values of one record the caller may read.</summary>
public sealed class TestTemplateProvider(TestTemplateBook data, TestRecordBook records, ICurrentActor actor) : ITemplateDataProvider
{
    public string EntityType => TestRecordBook.OwnerType;

    public IReadOnlyList<TemplateFieldDefinition> Fields { get; } =
    [
        new("company.name", "text", TemplateFieldClasses.General),
        new("employee.name", "text", TemplateFieldClasses.General),
        new("employee.id_card", "text", TemplateFieldClasses.Personal),
        new("employee.salary", "number", TemplateFieldClasses.Salary),
        new("employee.joined", "date", TemplateFieldClasses.General),
        new("items[].label", "text", TemplateFieldClasses.General),
    ];

    public Task<TemplateData?> LoadAsync(Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(data.Records.TryGetValue(ownerId, out var r) && records.Find(ownerId, actor.UserId) is { CanRead: true } ? new TemplateData(r.Company, r.Values) : null);
}

/// <summary>
/// Document templates and PDF output of slice S1.11c2 (KRN-DOC-05, HRM-DOC-08; rulings Q17, Q19, Q26, Q27) through the real API
/// pipeline, with a real PostgreSQL and the server's headless Chromium. Every refusal is tested.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class TemplateTests(TenancyFixture fx) : IAsyncLifetime
{
    private readonly TestTemplateBook _book = new();
    private DocumentRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await DocumentRig.StartAsync(fx, Ct, services =>
    {
        services.AddSingleton(_book);
        services.AddScoped<ITemplateDataProvider, TestTemplateProvider>();
        services.RemoveAll<PdfRendererOptions>();
        services.AddSingleton(new PdfRendererOptions(null, NoSandbox: true, TimeoutSeconds: 90, Parallel: 2));
    });

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private Task<FileAnswer> PostAsync(Guid user, string path, object body) => _rig.SendAsync(HttpMethod.Post, user, path, Json(body), Ct);

    private async Task<Guid> AdminAsync() => await _rig.UserAsync(Ct, "tenant_admin");

    private async Task<Guid> RecordAsync(Guid reader, string name = "Ayesha Khan", string salary = "85000")
    {
        var record = _rig.Records.Add(_rig.Company, [reader], [reader], [reader]);
        _book.Records[record] = (_rig.Company, new Dictionary<string, object?>
        {
            ["company"] = new Dictionary<string, object?> { ["name"] = "Acme Traders" },
            ["employee"] = new Dictionary<string, object?> { ["name"] = name, ["id_card"] = "35202-1234567-1", ["salary"] = decimal.Parse(salary, System.Globalization.CultureInfo.InvariantCulture), ["joined"] = new DateOnly(2024, 3, 1) },
            ["items"] = new List<object?> { new Dictionary<string, object?> { ["label"] = "Basic" } },
        });
        return record;
    }

    private static object NewTemplate(string code, string body, string language = "en", string kind = "letter", string format = "pdf", string entity = TestRecordBook.OwnerType, Guid? company = null) =>
        new { code, name = new Dictionary<string, string> { ["en"] = "Test " + code }, kind, entityType = entity, companyId = company, language, outputFormat = format, body };

    private async Task<(Guid Template, Guid Version, long VersionRow)> ActiveTemplateAsync(Guid admin, string code, string body, string language = "en", string format = "pdf", string kind = "letter", string entity = TestRecordBook.OwnerType)
    {
        var created = await PostAsync(admin, "/api/doc/templates", NewTemplate(code, body, language, kind, format, entity));
        Assert.True(created.Status == HttpStatusCode.Created, created.Text);
        var template = created.Json.GetProperty("id").GetGuid();
        var versions = await _rig.GetAsync(admin, $"/api/doc/templates/{template}/versions", Ct);
        var draft = versions.Json[0];
        var activated = await PostAsync(admin, $"/api/doc/templates/versions/{draft.GetProperty("id").GetGuid()}/activate", new { rowVersion = draft.GetProperty("rowVersion").GetInt64() });
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Text);
        return (template, draft.GetProperty("id").GetGuid(), activated.Json.GetProperty("rowVersion").GetInt64());
    }

    private async Task<FileAnswer> DownloadAsync(Guid user, FileAnswer generated)
    {
        var file = generated.Json.GetProperty("fileId").GetGuid();
        var down = await _rig.GetAsync(user, $"/api/doc/attachments/{file}/content", Ct);
        Assert.Equal(HttpStatusCode.OK, down.Status);
        return down;
    }

    [Fact]
    public async Task S115d_KRN_DOC_05_the_editor_lists_the_fields_each_printable_record_offers_with_their_class_and_refuses_a_person_without_the_right()
    {
        var admin = await AdminAsync();
        var answer = await _rig.GetAsync(admin, "/api/doc/templates/entity-fields", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        var entry = answer.Json.EnumerateArray().Single(e => e.GetProperty("entityType").GetString() == TestRecordBook.OwnerType);
        var salary = entry.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("path").GetString() == "employee.salary");
        Assert.Equal("salary", salary.GetProperty("fieldClass").GetString());
        Assert.DoesNotContain("85000", answer.Text, StringComparison.Ordinal);
        var worker = await _rig.UserAsync(Ct, "business_user");
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.GetAsync(worker, "/api/doc/templates/entity-fields", Ct)).Status);
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_an_english_letter_is_produced_as_a_pdf_stored_encrypted_against_the_record()
    {
        var admin = await AdminAsync();
        var (template, _, _) = await ActiveTemplateAsync(admin, "offer-en", "<h1>Dear {{ employee.name }}</h1><p>Welcome to {{ company.name }}. Joined {{ employee.joined | date.to_string '%d %B %Y' }}.</p>{{ qr 'https://verify.example/1' }}{{ barcode 'EMP-0001' }}");
        var record = await RecordAsync(admin);

        var made = await PostAsync(admin, "/api/doc/templates/generate", new { templateId = template, ownerId = record });
        Assert.True(made.Status == HttpStatusCode.Created, made.Text);
        var pdf = (await DownloadAsync(admin, made)).Bytes;
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5), StringComparison.Ordinal);
        Assert.Equal(1, ChromiumPdfRenderer.PageCount(pdf));
        Assert.Contains("NotoSans", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        var fileId = made.Json.GetProperty("fileId").GetGuid();
        var key = await _rig.ScalarAsync<string>("select storage_key from doc.files where id = $1", Ct, fileId);
        Assert.NotEqual(pdf, await File.ReadAllBytesAsync(Path.Combine(_rig.Root, key), Ct)); // encrypted at rest
        Assert.Equal(1L, await _rig.ScalarAsync<long>("select count(*) from doc.generated_documents where id = $1 and file_id = $2 and owner_id = $3 and not is_password_protected", Ct, made.Json.GetProperty("id").GetGuid(), fileId, record));
        var listed = await _rig.GetAsync(admin, $"/api/doc/templates/generated?ownerType={TestRecordBook.OwnerType}&ownerId={record}", Ct);
        Assert.Single(listed.Json.EnumerateArray());
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_an_urdu_letter_is_right_to_left_with_the_nastaliq_font_embedded()
    {
        var admin = await AdminAsync();
        var (template, _, _) = await ActiveTemplateAsync(admin, "offer-ur", "<h1>محترم {{ employee.name }}</h1><p>{{ company.name }} میں خوش آمدید</p>", language: "ur");
        var record = await RecordAsync(admin, name: "عائشہ خان");
        var made = await PostAsync(admin, "/api/doc/templates/generate", new { templateId = template, ownerId = record });
        Assert.True(made.Status == HttpStatusCode.Created, made.Text);
        var pdf = (await DownloadAsync(admin, made)).Bytes;
        Assert.Contains("NotoNastaliqUrdu", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        var preview = await PostAsync(admin, "/api/doc/templates/preview", new { body = "<p>{{ employee.name }}</p>", outputFormat = "html", language = "ur", entityType = TestRecordBook.OwnerType });
        Assert.Contains("dir=\"rtl\"", preview.Json.GetProperty("html").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_the_letterhead_of_the_company_and_language_wraps_the_letter()
    {
        var admin = await AdminAsync();
        var head = await PostAsync(admin, "/api/doc/templates", NewTemplate("letterhead", "<header>ACME LETTERHEAD</header>{{ content }}<footer>Page footer {{ company.name }}</footer>", kind: "part", format: "html"));
        Assert.True(head.Status == HttpStatusCode.Created, head.Text);
        var headId = head.Json.GetProperty("id").GetGuid();
        var headVersion = (await _rig.GetAsync(admin, $"/api/doc/templates/{headId}/versions", Ct)).Json[0];
        await PostAsync(admin, $"/api/doc/templates/versions/{headVersion.GetProperty("id").GetGuid()}/activate", new { rowVersion = headVersion.GetProperty("rowVersion").GetInt64() });
        try
        {

            var (html, _, _) = await ActiveTemplateAsync(admin, "note-en", "<p>Hello {{ employee.name }}</p>", format: "html");
            var record = await RecordAsync(admin);
            var english = await PostAsync(admin, "/api/doc/templates/generate", new { templateId = html, ownerId = record });
            Assert.Equal(HttpStatusCode.Created, english.Status);
            var text = english.Json.GetProperty("content").GetString()!;
            Assert.Contains("ACME LETTERHEAD", text, StringComparison.Ordinal);
            Assert.Contains("<p>Hello Ayesha Khan</p>", text, StringComparison.Ordinal);
            Assert.Contains("Page footer Acme Traders", text, StringComparison.Ordinal);

            var (urdu, _, _) = await ActiveTemplateAsync(admin, "note-ur", "<p>{{ employee.name }}</p>", language: "ur", format: "html");
            var other = await PostAsync(admin, "/api/doc/templates/generate", new { templateId = urdu, ownerId = record });
            Assert.DoesNotContain("ACME LETTERHEAD", other.Json.GetProperty("content").GetString(), StringComparison.Ordinal); // no Urdu letterhead exists
        }
        finally
        {
            await PostAsync(admin, $"/api/doc/templates/{headId}/retire", new { rowVersion = await _rig.ScalarAsync<long>("select row_version from doc.templates where id = $1", Ct, headId) }); // other tests share this customer
        }
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_versions_go_draft_active_retired_and_the_active_one_is_never_changed()
    {
        var admin = await AdminAsync();
        var (template, v1, row) = await ActiveTemplateAsync(admin, "ver-en", "<p>one</p>");
        var edit = await _rig.PutJsonAsync(admin, $"/api/doc/templates/versions/{v1}", JsonSerializer.Serialize(new { body = "<p>changed</p>", rowVersion = row }), Ct);
        Assert.Equal(HttpStatusCode.Conflict, edit.Status); // only a draft can change
        Assert.Equal(TemplateErrors.NotDraft, edit.Json.GetProperty("title").GetString());

        var second = await PostAsync(admin, $"/api/doc/templates/{template}/versions", new { body = "<p>two</p>" });
        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal(2, second.Json.GetProperty("version").GetInt32());
        Assert.Equal("draft", second.Json.GetProperty("status").GetString());
        var again = await PostAsync(admin, $"/api/doc/templates/{template}/versions", new { body = "<p>three</p>" });
        Assert.Equal(HttpStatusCode.Conflict, again.Status); // one draft at a time
        var draft = second.Json.GetProperty("id").GetGuid();
        var changed = await _rig.PutJsonAsync(admin, $"/api/doc/templates/versions/{draft}", JsonSerializer.Serialize(new { body = "<p>two b</p>", rowVersion = second.Json.GetProperty("rowVersion").GetInt64() }), Ct);
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        var stale = await _rig.PutJsonAsync(admin, $"/api/doc/templates/versions/{draft}", JsonSerializer.Serialize(new { body = "<p>x</p>", rowVersion = second.Json.GetProperty("rowVersion").GetInt64() }), Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status); // stale row version

        var activated = await PostAsync(admin, $"/api/doc/templates/versions/{draft}/activate", new { rowVersion = changed.Json.GetProperty("rowVersion").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, activated.Status);
        var statuses = (await _rig.GetAsync(admin, $"/api/doc/templates/{template}/versions", Ct)).Json.EnumerateArray().ToDictionary(v => v.GetProperty("version").GetInt32(), v => v.GetProperty("status").GetString());
        Assert.Equal("retired", statuses[1]);
        Assert.Equal("active", statuses[2]);
        Assert.Equal(draft, await _rig.ScalarAsync<Guid>("select active_version_id from doc.templates where id = $1", Ct, template));
        Assert.True(await _rig.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'doc.template_versions' and action = 'status'", Ct) >= 2); // every change is audited
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_a_template_with_a_mistake_an_unknown_field_or_too_much_text_is_refused_and_nothing_is_stored()
    {
        var admin = await AdminAsync();
        var before = await _rig.ScalarAsync<long>("select count(*) from doc.templates", Ct);
        var syntax = await PostAsync(admin, "/api/doc/templates", NewTemplate("bad-1", "{{ if }}"));
        Assert.Equal(HttpStatusCode.BadRequest, syntax.Status);
        Assert.Equal(TemplateErrors.Syntax, syntax.Json.GetProperty("title").GetString());
        var unknown = await PostAsync(admin, "/api/doc/templates", NewTemplate("bad-2", "{{ employee.shoe_size }}"));
        Assert.Equal(TemplateErrors.UnknownField, unknown.Json.GetProperty("title").GetString());
        var large = await PostAsync(admin, "/api/doc/templates", NewTemplate("bad-3", new string('x', 300 * 1024)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.Status);
        var word = await PostAsync(admin, "/api/doc/templates", NewTemplate("bad-4", "x", format: "xlsx"));
        Assert.Equal(TemplateErrors.FormatNotSupported, word.Json.GetProperty("title").GetString());
        var entity = await PostAsync(admin, "/api/doc/templates", NewTemplate("bad-5", "x", entity: "no.such_record"));
        Assert.Equal(TemplateErrors.UnknownEntity, entity.Json.GetProperty("title").GetString());
        var badCode = await PostAsync(admin, "/api/doc/templates", NewTemplate("Bad Code!", "x"));
        Assert.Equal(HttpStatusCode.BadRequest, badCode.Status);
        Assert.Equal(before, await _rig.ScalarAsync<long>("select count(*) from doc.templates", Ct));

        var first = await PostAsync(admin, "/api/doc/templates", NewTemplate("dup-1", "x"));
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(admin, "/api/doc/templates", NewTemplate("dup-1", "x"))).Status);
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_a_preview_shows_the_document_and_stores_nothing()
    {
        var admin = await AdminAsync();
        var files = await _rig.ScalarAsync<long>("select count(*) from doc.files", Ct);
        var generated = await _rig.ScalarAsync<long>("select count(*) from doc.generated_documents", Ct);
        var preview = await PostAsync(admin, "/api/doc/templates/preview", new { body = "<p>{{ employee.name }} earns {{ employee.salary }}</p>", outputFormat = "pdf", language = "en", entityType = TestRecordBook.OwnerType });
        Assert.True(preview.Status == HttpStatusCode.OK, preview.Text);
        Assert.Contains("Sample name earns 1234.5", preview.Json.GetProperty("html").GetString(), StringComparison.Ordinal); // invented values, never a real person's
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(Convert.FromBase64String(preview.Json.GetProperty("pdf").GetString()!), 0, 5), StringComparison.Ordinal);
        Assert.Equal(["employee.name", "employee.salary"], preview.Json.GetProperty("usedFields").EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal));
        var withData = await PostAsync(admin, "/api/doc/templates/preview", new { body = "<p>{{ who }}</p>", outputFormat = "html", language = "en", entityType = TestRecordBook.OwnerType, sampleData = new { who = "Given Name" } });
        Assert.True(withData.Status == HttpStatusCode.OK, withData.Text);
        Assert.Contains("<p>Given Name</p>", withData.Json.GetProperty("html").GetString(), StringComparison.Ordinal);
        Assert.Equal(files, await _rig.ScalarAsync<long>("select count(*) from doc.files", Ct));
        Assert.Equal(generated, await _rig.ScalarAsync<long>("select count(*) from doc.generated_documents", Ct));
    }

    [Fact]
    public async Task S111c2_HRM_DOC_08_a_template_with_salary_fields_needs_the_salary_right_is_logged_and_the_file_is_sensitive()
    {
        var admin = await AdminAsync();
        var (salaryTemplate, _, _) = await ActiveTemplateAsync(admin, "cert-salary", "<p>{{ employee.name }} earns {{ employee.salary }}</p>");
        var (plainTemplate, _, _) = await ActiveTemplateAsync(admin, "cert-plain", "<p>{{ employee.name }}</p>");

        // A clerk who may produce documents but does not hold the salary right.
        var clerk = await _rig.UserAsync(Ct);
        await GrantAsync(clerk, "doc.generated_documents.create", "doc.generated_documents.read");
        var record = await RecordAsync(clerk);
        var refused = await PostAsync(clerk, "/api/doc/templates/generate", new { templateId = salaryTemplate, ownerId = record });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal(0L, await _rig.ScalarAsync<long>("select count(*) from doc.generated_documents where owner_id = $1", Ct, record));
        var allowedPlain = await PostAsync(clerk, "/api/doc/templates/generate", new { templateId = plainTemplate, ownerId = record });
        Assert.Equal(HttpStatusCode.Created, allowedPlain.Status);
        Assert.False(await _rig.ScalarAsync<bool>("select is_sensitive from doc.files where id = $1", Ct, allowedPlain.Json.GetProperty("fileId").GetGuid()));

        var adminRecord = await RecordAsync(admin);
        var logged = await _rig.ScalarAsync<long>("select count(*) from aud.access_log where entity_type = 'doc.generated_documents' and actor_user_id = $1", Ct, admin);
        var made = await PostAsync(admin, "/api/doc/templates/generate", new { templateId = salaryTemplate, ownerId = adminRecord });
        Assert.Equal(HttpStatusCode.Created, made.Status);
        Assert.True(await _rig.ScalarAsync<bool>("select is_sensitive from doc.files where id = $1", Ct, made.Json.GetProperty("fileId").GetGuid()));
        Assert.Equal(logged + 1, await _rig.ScalarAsync<long>("select count(*) from aud.access_log where entity_type = 'doc.generated_documents' and actor_user_id = $1", Ct, admin));
        Assert.Equal("employee.salary", await _rig.ScalarAsync<string>("select fields[1] from aud.access_log where entity_type = 'doc.generated_documents' and actor_user_id = $1 order by occurred_at desc limit 1", Ct, admin));
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_a_record_the_caller_may_not_read_a_retired_template_and_a_missing_permission_are_refused()
    {
        var admin = await AdminAsync();
        var (template, _, _) = await ActiveTemplateAsync(admin, "refuse-en", "<p>{{ employee.name }}</p>");
        var someoneElses = await RecordAsync(await _rig.UserAsync(Ct, "business_user"));
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(admin, "/api/doc/templates/generate", new { templateId = template, ownerId = someoneElses })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(admin, "/api/doc/templates/generate", new { templateId = template, ownerId = Guid.NewGuid() })).Status);

        var worker = await _rig.UserAsync(Ct, "business_user");
        var own = await RecordAsync(worker);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(worker, "/api/doc/templates/generate", new { templateId = template, ownerId = own })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.GetAsync(worker, "/api/doc/templates", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(worker, "/api/doc/templates", NewTemplate("nope", "x"))).Status);

        var adminRecord = await RecordAsync(admin);
        var row = await _rig.ScalarAsync<long>("select row_version from doc.templates where id = $1", Ct, template);
        var retired = await PostAsync(admin, $"/api/doc/templates/{template}/retire", new { rowVersion = row });
        Assert.Equal(HttpStatusCode.OK, retired.Status);
        Assert.False(retired.Json.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(admin, "/api/doc/templates/generate", new { templateId = template, ownerId = adminRecord })).Status);
        Assert.Equal(0L, await _rig.ScalarAsync<long>("select count(*) from doc.template_versions where template_id = $1 and status <> 'retired'", Ct, template));
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_a_document_over_the_default_page_limit_of_50_is_refused()
    {
        var admin = await AdminAsync();
        var preview = await PostAsync(admin, "/api/doc/templates/preview", new { body = string.Concat(Enumerable.Repeat("<div style='page-break-after:always'>x</div>", 60)), outputFormat = "pdf", language = "en", entityType = TestRecordBook.OwnerType });
        Assert.Equal(HttpStatusCode.BadRequest, preview.Status);
        Assert.Equal(TemplateErrors.TooManyPages, preview.Json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_a_template_cannot_reach_the_network_or_run_script()
    {
        var admin = await AdminAsync();
        var preview = await PostAsync(admin, "/api/doc/templates/preview", new { body = "<img src='http://169.254.169.254/latest/meta-data/'><script>document.body.innerHTML='pwned'</script><p>text</p>", outputFormat = "pdf", language = "en", entityType = TestRecordBook.OwnerType });
        Assert.True(preview.Status == HttpStatusCode.OK, preview.Text); // the page prints; the request is refused inside the browser
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(Convert.FromBase64String(preview.Json.GetProperty("pdf").GetString()!), 0, 5), StringComparison.Ordinal);
    }

    private async Task GrantAsync(Guid user, params string[] permissions)
    {
        var role = Guid.CreateVersion7();
        await _rig.ExecAsync("insert into iam.roles (id, code, name_i18n, source, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"Clerk\"}', 'tenant', now(), $3, now(), $3)", Ct, role, "clerk" + Guid.NewGuid().ToString("N")[..10], BaselineSeed.SystemUserId);
        await _rig.ExecAsync("insert into iam.role_permissions (role_id, permission_id) select $1, id from iam.permissions where code = any($2)", Ct, role, permissions);
        await _rig.ExecAsync("insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, null, 'test', current_date - 1, now(), $4, now(), $4)", Ct, Guid.CreateVersion7(), user, role, BaselineSeed.SystemUserId);
    }
}
