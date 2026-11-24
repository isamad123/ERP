using System.Globalization;
using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.IntegrationTests.Tenancy;
using Sylvan.Data.Excel;

namespace Erp.IntegrationTests.ImportCentre;

/// <summary>The fixes of the fresh review of slice S1.16a (docs/reviews/S1.16a-review.md), finding by finding, in the real API and worker pipeline.</summary>
[Collection(TenancyGroup.Name)]
public sealed class ImportReviewTests(TenancyFixture fx) : IAsyncLifetime
{
    private ImportRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await ImportRig.StartAsync(fx, Ct);

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    // ------------------------------------------------------------------ helpers

    /// <summary>A person holding exactly these permissions for every company (scope "all").</summary>
    private async Task<Guid> PersonAsync(params string[] permissions)
    {
        var user = await _rig.Docs.UserAsync(Ct);
        var role = Guid.CreateVersion7();
        await _rig.ExecAsync("insert into iam.roles (id, code, name_i18n, source, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"Review test\"}', 'tenant', now(), $3, now(), $3)", Ct, role, "rv" + Guid.NewGuid().ToString("N")[..10], BaselineSeed.SystemUserId);
        await _rig.ExecAsync("insert into iam.role_permissions (role_id, permission_id) select $1, id from iam.permissions where code = any($2)", Ct, role, permissions);
        await _rig.ExecAsync("insert into iam.role_data_scopes (role_id, resource_type, data_scope_id) select $1, '*', id from iam.data_scopes where code = 'all'", Ct, role);
        await _rig.ExecAsync("insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, null, 'test', current_date - 1, now(), $4, now(), $4)", Ct, Guid.CreateVersion7(), user, role, BaselineSeed.SystemUserId);
        return user;
    }

    private Task SetAsync(string key, string json) => _rig.ExecAsync("insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by) values (gen_random_uuid(), $1, 'tenant', null, $2::jsonb, now(), $3, now(), $3) on conflict (setting_key, layer, scope_id) do update set value = excluded.value", Ct, key, json, BaselineSeed.SystemUserId);

    private Task UnsetAsync(string key) => _rig.ExecAsync("delete from cfg.setting_values where setting_key = $1", Ct, key);

    private static string UnitsCsv(string company, string tag, params (string Code, string Parent)[] units) =>
        "company,code,name_en,name_ur,unit_type,hierarchy,parent,valid_from\r\n" + string.Concat(units.Select(u => $"{company},{u.Code}{tag},{u.Code} unit,,DEPT,REPORTING,{(u.Parent.Length > 0 ? u.Parent + tag : string.Empty)},2026-01-01\r\n"));

    // ------------------------------------------------------------------ finding 1: the import tables are written only by the engine

    [Fact]
    public async Task S116a_review_1_FR_KRN_INT_03_the_generated_write_routes_of_the_import_tables_are_closed_and_a_forged_row_never_deletes_a_table_the_type_does_not_write()
    {
        foreach (var route in new[] { "/api/itg/import-batch-rows", "/api/itg/import-batches", "/api/itg/import-templates", "/api/itg/import-batch-errors" })
        {
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
            {
                var path = method == HttpMethod.Put ? route + "/" + Guid.NewGuid() : route;
                var answer = await _rig.Docs.SendAsync(method, _rig.Admin, path, new StringContent("{}", Encoding.UTF8, "application/json"), Ct);
                Assert.True(answer.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {path}: {answer.Status} {answer.Text}");
            }
        }

        // A row in the rollback list for a table the type does not write: the rollback is refused and the record stays.
        var (id, _) = await _rig.UploadAndValidateAsync("users", "u.csv", ImportRig.UsersCsv("rva", 2), Ct);
        var committed = await _rig.CommitAsync(id, Ct);
        Assert.Equal("committed", committed.GetProperty("status").GetString());
        await _rig.ExecAsync("insert into itg.import_batch_rows (id, batch_id, row_no, table_name, record_id, action, record_version, created_at, created_by) values (gen_random_uuid(), $1, 999, 'org.companies', $2, 'created', null, now(), $3)", Ct, id, _rig.Docs.Company, BaselineSeed.SystemUserId);
        var view = (await _rig.GetAsync($"/api/itg/imports/batches/{id}", Ct)).Json;
        Assert.False(view.GetProperty("canRollback").GetBoolean());
        Assert.Equal("errors.import.rollback_not_allowed", view.GetProperty("rollbackBlockedKey").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/rollback", new { reason = "forged" }, Ct)).Status);
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from org.companies where id = $1 and not is_deleted", Ct, _rig.Docs.Company));
    }

    [Fact]
    public async Task S116a_review_1_FR_KRN_INT_03_a_batch_pointing_at_another_records_file_never_reads_it()
    {
        var template = await _rig.TemplateAsync("users", Ct);
        var first = await _rig.UploadAsync(template, "one.csv", ImportRig.UsersCsv("rvb", 2), Ct);
        var second = await _rig.UploadAsync(template, "two.csv", ImportRig.UsersCsv("rvc", 2), Ct);
        var firstId = first.Json.GetProperty("id").GetGuid();
        var secondId = second.Json.GetProperty("id").GetGuid();
        await _rig.ExecAsync("update itg.import_batches set file_id = (select file_id from itg.import_batches where id = $1) where id = $2", Ct, firstId, secondId);
        await _rig.PostJsonAsync($"/api/itg/imports/batches/{secondId}/validate", null, Ct);
        await _rig.RunJobsAsync(Ct);
        Assert.Equal("failed", (await _rig.GetAsync($"/api/itg/imports/batches/{secondId}", Ct)).Json.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync($"/api/itg/imports/batches/{secondId}/errors.csv", Ct)).Status);
    }

    // ------------------------------------------------------------------ finding 3: the template test is gated like an upload

    [Fact]
    public async Task S116a_review_3_FR_KRN_INT_03_a_sample_file_for_the_template_test_is_scanned_and_size_limited_like_an_upload()
    {
        var template = await _rig.TemplateAsync("users", Ct);
        var infected = await _rig.TestAsync(template, "virus.csv", Encoding.UTF8.GetBytes("username,email,display_name\r\nEICAR,a@b.test,X\r\n"), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, infected.Status);
        Assert.Equal("errors.import.infected", infected.Json.GetProperty("title").GetString());

        await SetAsync("doc.upload.max_mb", "1");
        try
        {
            var big = await _rig.TestAsync(template, "big.csv", ImportRig.UsersCsv("rvd", 25_000), Ct);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.Status);
            Assert.Equal("errors.import.too_large", big.Json.GetProperty("title").GetString());
            var upload = await _rig.UploadAsync(template, "big.csv", ImportRig.UsersCsv("rve", 25_000), Ct);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, upload.Status);
            Assert.Equal("errors.import.too_large", upload.Json.GetProperty("title").GetString());
        }
        finally
        {
            await UnsetAsync("doc.upload.max_mb");
        }
    }

    // ------------------------------------------------------------------ finding 2: package and row limits of Excel files

    [Fact]
    public async Task S116a_review_2_FR_KRN_INT_03_an_excel_package_that_unpacks_to_too_much_and_an_excel_file_over_the_row_limit_are_refused()
    {
        var template = await _rig.TemplateAsync("users", Ct);
        using var bomb = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bomb, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            await using var entry = zip.CreateEntry("xl/sharedStrings.xml", System.IO.Compression.CompressionLevel.SmallestSize).Open();
            await entry.WriteAsync(new byte[20 * 1024 * 1024], Ct);
        }

        Assert.True(bomb.Length < 1024 * 1024, "the test bomb must itself be small");
        var refused = await _rig.UploadAsync(template, "bomb.xlsx", bomb.ToArray(), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.Status);
        Assert.Equal("errors.import.too_large", refused.Json.GetProperty("title").GetString());

        // A small Excel file with four rows against a limit of three: the upload takes it, the check says "too many rows".
        using var table = new DataTable();
        foreach (var header in new[] { "username", "email", "display_name", "company", "language", "time_zone", "access_valid_to" })
        {
            table.Columns.Add(header, typeof(string));
        }

        for (var i = 1; i <= 4; i++)
        {
            table.Rows.Add($"rvx{i}", $"rvx{i}@import.test", $"Person {i}", null, "en", "Asia/Karachi", null);
        }

        using var xlsx = new MemoryStream();
        using (var writer = ExcelDataWriter.Create(xlsx, ExcelWorkbookType.ExcelXml))
        using (var reader = table.CreateDataReader())
        {
            writer.Write(reader, "Users");
        }

        await SetAsync("itg.import.max_rows", "3");
        try
        {
            var (id, batch) = await _rig.UploadAndValidateAsync("users", "four.xlsx", xlsx.ToArray(), Ct);
            Assert.Equal(0, batch.GetProperty("okRows").GetInt32());
            Assert.Contains("errors.import.too_many_rows", (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors", Ct)).Text, StringComparison.Ordinal);
        }
        finally
        {
            await UnsetAsync("itg.import.max_rows");
        }

        // A cell over 4,000 characters is a problem of its row, not of the file.
        var csv = "username,email,display_name,company,language,time_zone,access_valid_to\r\n" + $"rvy1,rvy1@import.test,{new string('x', 4100)},,en,Asia/Karachi,\r\n" + "rvy2,rvy2@import.test,Fine,,en,Asia/Karachi,\r\n";
        var (longId, longBatch) = await _rig.UploadAndValidateAsync("users", "long.csv", Encoding.UTF8.GetBytes(csv), Ct);
        Assert.Equal(1, longBatch.GetProperty("okRows").GetInt32());
        Assert.Contains("errors.import.cell_too_long", (await _rig.GetAsync($"/api/itg/imports/batches/{longId}/errors", Ct)).Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ finding 4: the batch keeps the mapping it was checked with

    [Fact]
    public async Task S116a_review_4_FR_ADM_INT_04_a_template_changed_after_the_check_does_not_change_what_the_commit_writes()
    {
        var templates = (await _rig.GetAsync("/api/itg/imports/templates?type=users", Ct)).Json;
        var shipped = templates.EnumerateArray().First(t => t.GetProperty("isDefault").GetBoolean());
        var columns = shipped.GetProperty("mapping").GetProperty("columns").EnumerateArray().Select(c => c.Clone()).ToList();
        var code = "rvmap" + Guid.NewGuid().ToString("N")[..6];
        var create = await _rig.PostJsonAsync("/api/itg/imports/templates", new { code, name = new { en = "Mapping copy" }, entityType = "users", format = "csv", mode = "create", mapping = new { columns } }, Ct);
        Assert.True(create.Status == HttpStatusCode.Created, create.Text);
        var template = create.Json.GetProperty("id").GetGuid();
        var rowVersion = create.Json.GetProperty("rowVersion").GetInt64();

        var up = await _rig.UploadAsync(template, "m.csv", ImportRig.UsersCsv("rvm", 3), Ct);
        var id = up.Json.GetProperty("id").GetGuid();
        await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/validate", null, Ct);
        await _rig.RunJobsAsync(Ct);

        // A designer now points the display name at the e-mail column.
        var changed = columns.Select(c => c.GetProperty("field").GetString() == "display_name" ? JsonSerializer.SerializeToElement(new { field = "display_name", source = "email" }) : c).ToList();
        var put = await _rig.PutJsonAsync($"/api/itg/imports/templates/{template}", new { name = new { en = "Mapping copy" }, mode = "create", mapping = new { columns = changed }, rowVersion }, Ct);
        Assert.True(put.Status == HttpStatusCode.OK, put.Text);

        var done = await _rig.CommitAsync(id, Ct);
        Assert.True(done.GetProperty("status").GetString() == "committed", done.ToString());
        Assert.Equal("Person 1", await _rig.ScalarAsync<string>("select display_name from iam.users where username = 'rvm0001'", Ct));
    }

    // ------------------------------------------------------------------ finding 5: rollback respects what depends on a record and the right to delete it

    [Fact]
    public async Task S116a_review_5_FR_ADM_ORG_03_a_unit_that_a_later_unit_sits_under_cannot_be_rolled_back_a_changed_unit_cannot_and_a_person_without_the_delete_right_cannot()
    {
        var (_, _, _, codeA) = await _rig.UnitSetupAsync(Ct);
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var (parentBatch, _) = await _rig.UploadAndValidateAsync("org_units", "p.csv", Encoding.UTF8.GetBytes(UnitsCsv(codeA, tag, ("HQ", string.Empty))), Ct);
        await _rig.CommitAsync(parentBatch, Ct);
        var (childBatch, _) = await _rig.UploadAndValidateAsync("org_units", "c.csv", Encoding.UTF8.GetBytes(UnitsCsv(codeA, tag, ("FIN", "HQ"))), Ct);
        await _rig.CommitAsync(childBatch, Ct);

        var view = (await _rig.GetAsync($"/api/itg/imports/batches/{parentBatch}", Ct)).Json;
        Assert.False(view.GetProperty("canRollback").GetBoolean());
        Assert.Equal("errors.import.rollback_in_use", view.GetProperty("rollbackBlockedKey").GetString());
        var refused = await _rig.PostJsonAsync($"/api/itg/imports/batches/{parentBatch}/rollback", new { reason = "x" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("errors.import.rollback_in_use", refused.Json.GetProperty("title").GetString());

        // The child first, then the parent.
        Assert.Equal(HttpStatusCode.OK, (await _rig.PostJsonAsync($"/api/itg/imports/batches/{childBatch}/rollback", new { reason = "x" }, Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _rig.PostJsonAsync($"/api/itg/imports/batches/{parentBatch}/rollback", new { reason = "x" }, Ct)).Status);

        // A unit that changed since is refused.
        var tag2 = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var (changedBatch, _) = await _rig.UploadAndValidateAsync("org_units", "ch.csv", Encoding.UTF8.GetBytes(UnitsCsv(codeA, tag2, ("HQ", string.Empty))), Ct);
        await _rig.CommitAsync(changedBatch, Ct);
        await _rig.ExecAsync("update org.org_units set row_version = row_version + 1 where code = $1", Ct, "HQ" + tag2);
        var changed = await _rig.PostJsonAsync($"/api/itg/imports/batches/{changedBatch}/rollback", new { reason = "x" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, changed.Status);
        Assert.Equal("errors.import.rollback_changed", changed.Json.GetProperty("title").GetString());

        // A person who may import units but not delete them cannot roll their own batch back.
        var importer = await _rig.UnitImporterAsync(_rig.Docs.Company, Ct);
        var tag3 = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var (ownBatch, _) = await _rig.UploadAndValidateAsync("org_units", "o.csv", Encoding.UTF8.GetBytes(UnitsCsv(codeA, tag3, ("HQ", string.Empty))), Ct, importer);
        await _rig.CommitAsync(ownBatch, Ct, user: importer);
        var denied = await _rig.PostJsonAsync($"/api/itg/imports/batches/{ownBatch}/rollback", new { reason = "x" }, Ct, importer);
        Assert.Equal(HttpStatusCode.Conflict, denied.Status);
        Assert.Equal("errors.import.rollback_forbidden", denied.Json.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _rig.PostJsonAsync($"/api/itg/imports/batches/{ownBatch}/rollback", new { reason = "admin" }, Ct)).Status);
    }

    // ------------------------------------------------------------------ finding 6: who may read another person's batch

    [Fact]
    public async Task S116a_review_6_FR_ADM_DAT_01_another_persons_users_batch_and_its_problem_file_need_the_right_to_read_users()
    {
        var (id, _) = await _rig.UploadAndValidateAsync("users", "r.csv", ImportRig.UsersCsv("rvr", 5, 2), Ct);
        var reader = await PersonAsync("itg.imports.manage", "itg.import_batches.read");
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync($"/api/itg/imports/batches/{id}", Ct, reader)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors", Ct, reader)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors.csv", Ct, reader)).Status);
        var list = (await _rig.GetAsync("/api/itg/imports/batches", Ct, reader)).Json.GetProperty("items");
        Assert.DoesNotContain(list.EnumerateArray(), b => b.GetProperty("id").GetGuid() == id);

        var both = await PersonAsync("itg.imports.manage", "itg.import_batches.read", "iam.users.read");
        Assert.Equal(HttpStatusCode.OK, (await _rig.GetAsync($"/api/itg/imports/batches/{id}", Ct, both)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors.csv", Ct, both)).Status);
    }

    // ------------------------------------------------------------------ finding 7: unique values across the whole file

    [Fact]
    public async Task S116a_review_7_FR_KRN_INT_03_an_e_mail_address_repeated_more_than_500_rows_later_is_found_by_the_check()
    {
        var lines = Encoding.UTF8.GetString(ImportRig.UsersCsv("rvu", 600)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[^1] = "rvu0600,rvu0002@import.test,Person 600,,en,Asia/Karachi,";
        var (id, batch) = await _rig.UploadAndValidateAsync("users", "dup.csv", Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n"), Ct);
        Assert.True(batch.GetProperty("okRows").GetInt32() == 599 && batch.GetProperty("errorRows").GetInt32() == 1, batch + (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors", Ct)).Text);
        Assert.Contains("errors.import.duplicate_in_file", (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors", Ct)).Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ finding 8: an unexpected error in the commit does not strand the batch

    [Fact]
    public async Task S116a_review_8_FR_KRN_INT_04_a_commit_that_fails_once_continues_from_its_checkpoint_and_one_that_always_fails_can_be_rolled_back()
    {
        var (id, _) = await _rig.UploadAndValidateAsync("users", "f.csv", ImportRig.UsersCsv("rvf", 450), Ct);
        _rig.NewWorker(extra: services => FailingCommitJob.Install(services, 1, 1));
        await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/commit", null, Ct);
        await _rig.RunJobsAsync(Ct);
        var between = (await _rig.GetAsync($"/api/itg/imports/batches/{id}", Ct)).Json;
        Assert.Equal("committing", between.GetProperty("status").GetString()); // not failed: the retry is still to come
        Assert.Equal(200, await _rig.ScalarAsync<long>("select count(*) from iam.users where username like 'rvf%'", Ct));
        _rig.Docs.Clock.Advance(TimeSpan.FromSeconds(31));
        await _rig.RunJobsAsync(Ct);
        var done = (await _rig.GetAsync($"/api/itg/imports/batches/{id}", Ct)).Json;
        Assert.True(done.GetProperty("status").GetString() == "committed", done + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.Equal(450, await _rig.ScalarAsync<long>("select count(*) from iam.users where username like 'rvf%'", Ct));
        Assert.Equal(450, await _rig.ScalarAsync<long>("select count(*) from itg.import_batch_rows where batch_id = $1", Ct, id));

        // Fails in every attempt: after the last one the batch is failed, and what it saved can be rolled back.
        var (failId, _) = await _rig.UploadAndValidateAsync("users", "g.csv", ImportRig.UsersCsv("rvg", 450), Ct);
        _rig.NewWorker(extra: services => FailingCommitJob.Install(services, 1, 5));
        await _rig.PostJsonAsync($"/api/itg/imports/batches/{failId}/commit", null, Ct);
        await _rig.RunJobsAsync(Ct);
        _rig.Docs.Clock.Advance(TimeSpan.FromSeconds(31));
        await _rig.RunJobsAsync(Ct);
        _rig.Docs.Clock.Advance(TimeSpan.FromSeconds(121));
        await _rig.RunJobsAsync(Ct);
        var failed = (await _rig.GetAsync($"/api/itg/imports/batches/{failId}", Ct)).Json;
        Assert.True(failed.GetProperty("status").GetString() == "failed", failed + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.True(failed.GetProperty("canRollback").GetBoolean(), failed.ToString());
        var rolled = await _rig.PostJsonAsync($"/api/itg/imports/batches/{failId}/rollback", new { reason = "failed commit" }, Ct);
        Assert.True(rolled.Status == HttpStatusCode.OK, rolled.Text);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from iam.users where username like 'rvg%' and not is_deleted", Ct));
    }

    // ------------------------------------------------------------------ finding 10: the missing parts of the acceptance list

    [Fact]
    public async Task S116a_review_10_FR_ADM_ORG_02_locations_are_imported_with_their_branch_and_address()
    {
        var company = await _rig.ScalarAsync<string>("select code from org.companies where id = $1", Ct, _rig.Docs.Company);
        var branch = await _rig.ScalarAsync<string>("select code from org.branches where company_id = $1 and not is_deleted order by created_at limit 1", Ct, _rig.Docs.Company);
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var csv = "company,code,name_en,name_ur,branch,address_line1,address_line2,city,postal_code,country,region_code,time_zone,latitude,longitude,holiday_calendar\r\n"
            + $"{company},LOC{tag},Lahore office,,{branch},12 Mall Road,,Lahore,54000,PK,PB,Asia/Karachi,31.5497,74.3436,\r\n"
            + $"{company},BAD{tag},No address,,{branch},,,Lahore,,PK,PB,Asia/Karachi,,,\r\n";
        var (id, batch) = await _rig.UploadAndValidateAsync("locations", "l.csv", Encoding.UTF8.GetBytes(csv), Ct);
        Assert.True(batch.GetProperty("okRows").GetInt32() == 1 && batch.GetProperty("errorRows").GetInt32() == 1, batch + (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors", Ct)).Text);
        var done = await _rig.CommitAsync(id, Ct, skipErrors: true);
        Assert.Equal(1, done.GetProperty("createdRows").GetInt32());
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from org.locations where code = $1 and not is_deleted", Ct, "LOC" + tag));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from org.locations where code = $1", Ct, "BAD" + tag));
    }

    [Fact]
    public async Task S116a_review_10_FR_ADM_USR_02_FR_KRN_ENT_05_imported_users_take_seats_when_they_activate_and_the_design_and_rollback_rights_are_checked_by_the_server()
    {
        var (id, _) = await _rig.UploadAndValidateAsync("users", "s.csv", ImportRig.UsersCsv("rvs", 3), Ct);
        await _rig.CommitAsync(id, Ct);
        var seats = $"select count(*) from iam.users where status in ('active', 'locked') and not is_deleted and not is_system";
        var before = await _rig.ScalarAsync<long>(seats, Ct);
        await _rig.ExecAsync("update iam.users set status = 'active', row_version = row_version + 1 where username = 'rvs0001'", Ct);
        Assert.Equal(before + 1, await _rig.ScalarAsync<long>(seats, Ct));
        var licence = (await _rig.GetAsync("/api/cfg/licence", Ct)).Json.GetProperty("limits").EnumerateArray().FirstOrDefault(l => l.GetProperty("key").GetString() == "users.named");
        if (licence.ValueKind == JsonValueKind.Object && licence.GetProperty("used").ValueKind == JsonValueKind.Number)
        {
            Assert.True(licence.GetProperty("used").GetInt64() >= before + 1, licence.ToString());
        }

        // The design right: a person who only imports cannot create or change a template.
        var importer = await PersonAsync("itg.imports.manage", "iam.users.create", "iam.users.read");
        var code = "rvd" + Guid.NewGuid().ToString("N")[..6];
        var body = new { code, name = new { en = "No" }, entityType = "users", format = "csv", mode = "create", mapping = new { columns = Array.Empty<object>() } };
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.PostJsonAsync("/api/itg/imports/templates", body, Ct, importer)).Status);
        var template = await _rig.TemplateAsync("users", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.PutJsonAsync($"/api/itg/imports/templates/{template}", new { name = new { en = "No" }, mode = "create", mapping = new { columns = Array.Empty<object>() }, rowVersion = 1 }, Ct, importer)).Status);

        // The `default.` prefix belongs to the shipped templates.
        var reserved = await _rig.PostJsonAsync("/api/itg/imports/templates", new { code = "default.employees", name = new { en = "Mine" }, entityType = "users", format = "csv", mode = "create", mapping = new { columns = Array.Empty<object>() } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, reserved.Status);

        // The right to roll back: a person who may import users but not end their accounts is refused.
        var (ownId, _) = await _rig.UploadAndValidateAsync("users", "o.csv", ImportRig.UsersCsv("rvt", 2), Ct, importer);
        await _rig.CommitAsync(ownId, Ct, user: importer);
        var denied = await _rig.PostJsonAsync($"/api/itg/imports/batches/{ownId}/rollback", new { reason = "x" }, Ct, importer);
        Assert.Equal(HttpStatusCode.Conflict, denied.Status);
        Assert.Equal("errors.import.rollback_forbidden", denied.Json.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors", Ct, importer)).Status); // another person's batch: the right to read import batches is missing
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors.csv", Ct, importer)).Status);
    }

    [Fact]
    public async Task S116a_review_12_13_the_error_list_pages_without_losing_problems_and_the_error_file_speaks_the_persons_language()
    {
        // One row with three problems, paged one problem at a time.
        var csv = "username,email,display_name,company,language,time_zone,access_valid_to\r\n" + ",,,,xx,Nowhere/Land,notadate\r\n";
        var (id, _) = await _rig.UploadAndValidateAsync("users", "many.csv", Encoding.UTF8.GetBytes(csv), Ct);
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors?limit=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), Ct)).Json;
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("field").GetString() + ":" + e.GetProperty("message").GetString()));
            cursor = page.GetProperty("next").ValueKind == JsonValueKind.String ? page.GetProperty("next").GetString() : null;
        }
        while (cursor is not null);
        var all = (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors?limit=100", Ct)).Json.GetProperty("items").GetArrayLength();
        Assert.True(all >= 3, all.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(all, seen.Count);

        var file = await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors.csv", Ct);
        Assert.DoesNotContain("errors.import.required", file.Text, StringComparison.Ordinal); // keys are turned into the person's words
    }
}
