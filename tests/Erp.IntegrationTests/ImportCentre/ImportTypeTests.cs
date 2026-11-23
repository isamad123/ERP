using System.Net;
using System.Text;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.ImportCentre;

/// <summary>The import types and the template designer of slice S1.16a (FR-ADM-ORG-03, FR-ADM-INT-04, FR-KRN-INT-03) in the real API and worker pipeline.</summary>
[Collection(TenancyGroup.Name)]
public sealed class ImportTypeTests(TenancyFixture fx) : IAsyncLifetime
{
    private ImportRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await ImportRig.StartAsync(fx, Ct);

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    [Fact]
    public async Task S116a_FR_ADM_ORG_03_FR_KRN_IAM_13_units_are_imported_with_their_place_in_the_hierarchy_and_a_company_administrator_cannot_import_into_another_company()
    {
        var (_, codeB, companyA, codeA) = await _rig.UnitSetupAsync(Ct);
        var person = await _rig.UnitImporterAsync(companyA, Ct);
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var csv = string.Join("\r\n",
            "company,code,name_en,name_ur,unit_type,hierarchy,parent,valid_from",
            $"{codeA},HQ{tag},Head office,,DEPT,REPORTING,,2026-01-01",
            $"{codeA},FIN{tag},Finance,,DEPT,REPORTING,HQ{tag},2026-01-01",
            $"{codeB},OTH{tag},Other company,,DEPT,REPORTING,,2026-01-01",
            $"{codeA},BAD{tag},Bad parent,,DEPT,REPORTING,NOPE,2026-01-01") + "\r\n";
        var (id, batch) = await _rig.UploadAndValidateAsync("org_units", "units.csv", Encoding.UTF8.GetBytes(csv), Ct, person);
        Assert.True(batch.GetProperty("status").GetString() == "validated", batch.ToString() + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.True(2 == batch.GetProperty("okRows").GetInt32(), (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors", Ct, person)).Text);
        Assert.Equal(2, batch.GetProperty("errorRows").GetInt32());
        var errors = (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors", Ct, person)).Text;
        Assert.Contains("errors.import.company_forbidden", errors, StringComparison.Ordinal);
        Assert.Contains("errors.import.parent_unknown", errors, StringComparison.Ordinal);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from org.org_units where code like $1", Ct, "%" + tag));

        var refused = await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/commit", new { skipErrors = false }, Ct, person);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        var committed = await _rig.CommitAsync(id, Ct, skipErrors: true, user: person);
        Assert.True(committed.GetProperty("status").GetString() == "committed", committed.ToString() + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.Equal(2, committed.GetProperty("createdRows").GetInt32());
        Assert.Equal(2, await _rig.ScalarAsync<long>("select count(*) from org.org_units where code like $1 and not is_deleted", Ct, "%" + tag));
        Assert.Equal(2, await _rig.ScalarAsync<long>("select count(*) from org.org_unit_links l join org.org_units u on u.id = l.unit_id where u.code like $1", Ct, "%" + tag));
        Assert.Equal("HQ" + tag, await _rig.ScalarAsync<string>("select p.code from org.org_unit_links l join org.org_units u on u.id = l.unit_id join org.org_units p on p.id = l.parent_unit_id where u.code = $1", Ct, "FIN" + tag));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from org.org_units where code = $1", Ct, "OTH" + tag));

        // The same file again is refused: the keys exist and the template only creates.
        var (_, again) = await _rig.UploadAndValidateAsync("org_units", "units.csv", Encoding.UTF8.GetBytes(csv), Ct, person);
        Assert.Equal(0, again.GetProperty("okRows").GetInt32());
        Assert.Contains("errors.import.key_exists", (await _rig.GetAsync($"/api/itg/imports/batches/{again.GetProperty("id").GetGuid()}/errors", Ct, person)).Text, StringComparison.Ordinal);

        // A batch that created rows rolls back: the units are deleted and the links go with them.
        var rolled = await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/rollback", new { reason = "undo" }, Ct, person);
        Assert.True(rolled.Status == HttpStatusCode.OK, rolled.Text);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from org.org_units where code like $1 and not is_deleted", Ct, "%" + tag));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from org.org_unit_links l join org.org_units u on u.id = l.unit_id where u.code like $1", Ct, "%" + tag));
    }

    [Fact]
    public async Task S116a_FR_ADM_INT_04_a_designed_template_maps_other_headers_and_updates_existing_rows_and_an_update_batch_cannot_be_rolled_back()
    {
        var tag = Guid.NewGuid().ToString("N")[..5];
        await _rig.ExecAsync("insert into ref.calendars (id, code, name_i18n, source, is_active, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"Cal\"}', 'tenant', true, now(), $3, now(), $3)", Ct, Guid.CreateVersion7(), "CAL" + tag, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        var mapping = new
        {
            delimiter = ";",
            columns = new object[]
            {
                new { field = "calendar", source = "Kalender", transform = new[] { "trim", "upper" } },
                new { field = "holiday_date", source = "Datum", transform = new[] { "date:dd/MM/yyyy" } },
                new { field = "name_en", source = "Name", transform = new[] { "trim" } },
                new { field = "kind", source = "Art", transform = new[] { "lower" }, @default = "public" },
            },
        };
        var create = await _rig.PostJsonAsync("/api/itg/imports/templates", new { code = "hol_" + tag, name = new { en = "German holidays" }, entityType = "holidays", format = "csv", mode = "create_or_update", mapping }, Ct);
        Assert.True(create.Status == HttpStatusCode.Created, create.Text);
        var template = create.Json.GetProperty("id").GetGuid();

        var csv = $"Kalender;Datum;Name;Art\r\ncal{tag};24/12/2027;Christmas Eve;PUBLIC\r\ncal{tag};25/12/2027;Christmas;\r\n";
        var test = await _rig.TestAsync(template, "h.csv", Encoding.UTF8.GetBytes(csv), Ct);
        Assert.True(test.Status == HttpStatusCode.OK, test.Text);
        Assert.Equal(2, test.Json.GetProperty("totalRows").GetInt32());
        Assert.Equal(0, test.Json.GetProperty("errorRows").GetInt32());
        Assert.Equal("2027-12-24", test.Json.GetProperty("sample")[0].GetProperty("values").GetProperty("holidayDate").GetString());
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from ref.holidays h join ref.calendars c on c.id = h.calendar_id where c.code = $1", Ct, "CAL" + tag.ToUpperInvariant()));

        var up = await _rig.UploadAsync(template, "h.csv", Encoding.UTF8.GetBytes(csv), Ct);
        var batchId = up.Json.GetProperty("id").GetGuid();
        await _rig.PostJsonAsync($"/api/itg/imports/batches/{batchId}/validate", null, Ct);
        await _rig.RunJobsAsync(Ct);
        var first = await _rig.CommitAsync(batchId, Ct);
        Assert.Equal(2, first.GetProperty("createdRows").GetInt32());
        Assert.Equal("public", await _rig.ScalarAsync<string>("select h.kind from ref.holidays h join ref.calendars c on c.id = h.calendar_id where c.code = $1 and h.holiday_date = date '2027-12-25'", Ct, "CAL" + tag.ToUpperInvariant()));

        // The same keys again with other names: update, and such a batch can never be rolled back.
        var changed = $"Kalender;Datum;Name;Art\r\ncal{tag};24/12/2027;Christmas Eve (half day);optional\r\n";
        var second = await _rig.UploadAsync(template, "h2.csv", Encoding.UTF8.GetBytes(changed), Ct);
        var secondId = second.Json.GetProperty("id").GetGuid();
        await _rig.PostJsonAsync($"/api/itg/imports/batches/{secondId}/validate", null, Ct);
        await _rig.RunJobsAsync(Ct);
        var done = await _rig.CommitAsync(secondId, Ct);
        Assert.Equal(1, done.GetProperty("updatedRows").GetInt32());
        Assert.Equal(0, done.GetProperty("createdRows").GetInt32());
        Assert.Equal("optional", await _rig.ScalarAsync<string>("select h.kind from ref.holidays h join ref.calendars c on c.id = h.calendar_id where c.code = $1 and h.holiday_date = date '2027-12-24'", Ct, "CAL" + tag.ToUpperInvariant()));
        Assert.False(done.GetProperty("canRollback").GetBoolean());
        Assert.Equal("errors.import.rollback_has_updates", done.GetProperty("rollbackBlockedKey").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _rig.PostJsonAsync($"/api/itg/imports/batches/{secondId}/rollback", new { reason = "x" }, Ct)).Status);
    }

    [Fact]
    public async Task S116a_FR_ADM_INT_04_the_template_file_is_a_valid_import_file_in_csv_and_in_excel()
    {
        var template = await _rig.TemplateAsync("holidays", Ct);
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        await _rig.ExecAsync("insert into ref.calendars (id, code, name_i18n, source, is_active, created_at, created_by, updated_at, updated_by) values ($1, 'PK', '{\"en\":\"Pakistan\"}', 'tenant', true, now(), $2, now(), $2) on conflict do nothing", Ct, Guid.CreateVersion7(), Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        foreach (var format in new[] { "csv", "xlsx" })
        {
            var file = await _rig.GetAsync($"/api/itg/imports/templates/{template}/file?format={format}", Ct);
            Assert.Equal(HttpStatusCode.OK, file.Status);
            Assert.NotEmpty(file.Bytes);
            var (id, batch) = await _rig.UploadAndValidateAsync("holidays", "template." + format, file.Bytes, Ct);
            Assert.True(batch.GetProperty("status").GetString() == "validated" && batch.GetProperty("okRows").GetInt32() == 1, tag + batch + (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors", Ct)).Text);
        }
    }

    [Fact]
    public async Task S116a_FR_ADM_DAT_01_only_people_who_may_import_a_type_can_start_it_and_nobody_sees_another_persons_batches_without_the_read_right()
    {
        var plain = await _rig.Docs.UserAsync(Ct, "business_user");
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.GetAsync("/api/itg/imports/types", Ct, plain)).Status);
        var template = await _rig.TemplateAsync("users", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.UploadAsync(template, "u.csv", ImportRig.UsersCsv("pp", 1), Ct, plain)).Status);

        // Holds the import right but not the right to create users: the type is listed as one the person cannot run.
        var role = await _rig.UnitImporterAsync(_rig.Docs.Company, Ct);
        var types = (await _rig.GetAsync("/api/itg/imports/types", Ct, role)).Json;
        var users = types.EnumerateArray().First(t => t.GetProperty("code").GetString() == "users");
        Assert.False(users.GetProperty("canRun").GetBoolean());
        var denied = await _rig.UploadAsync(template, "u.csv", ImportRig.UsersCsv("pq", 1), Ct, role);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("errors.import.type_forbidden", denied.Json.GetProperty("title").GetString());

        var (id, _) = await _rig.UploadAndValidateAsync("users", "own.csv", ImportRig.UsersCsv("pr", 2), Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync($"/api/itg/imports/batches/{id}", Ct, role)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/commit", null, Ct, role)).Status);
    }
}
