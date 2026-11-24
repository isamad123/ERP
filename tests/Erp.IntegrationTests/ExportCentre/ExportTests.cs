using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Erp.IntegrationTests.ImportCentre;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.IntegrationTests.ExportCentre;

/// <summary>
/// The export engine of slice S1.16b (FR-ADM-DAT-04, FR-ADM-NAV-02, FR-KRN-IAM-12 to -14, FR-KRN-AUD-02, FR-KRN-ENT-04) in the real API and worker pipeline:
/// the list's own filter, search and sort, field security per person, the reason, the access log, the row limit, approval by a second person,
/// formula neutralising, the keeping period, and the locked and read-only states.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class ExportTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Exports = "/api/itg/exports";
    private ImportRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid Company => _rig.Docs.Company;

    public async ValueTask InitializeAsync() => _rig = await ImportRig.StartAsync(fx, Ct);

    public async ValueTask DisposeAsync()
    {
        await fx.SetDaysAfterEndAsync(_rig.Code, null, CancellationToken.None);
        await _rig.DisposeAsync();
    }

    private async Task<Guid> BranchAsync(string code, string name, bool active = true)
    {
        var id = Guid.CreateVersion7();
        await _rig.ExecAsync("""
            insert into org.branches (id, company_id, code, name_i18n, is_active, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4::jsonb, $5, now(), $6, now(), $6)
            """, Ct, id, Company, code, JsonSerializer.Serialize(new { en = name, ur = name }), active, BaselineSeed.SystemUserId);
        return id;
    }

    private async Task<Guid> PayGroupAsync(string code, decimal threshold)
    {
        var id = Guid.CreateVersion7();
        await _rig.ExecAsync("""
            insert into hrm.pay_groups (id, company_id, code, name_i18n, frequency, currency_code, tax_year_start_month, approval_threshold, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, '{"en":"Group"}'::jsonb, 'monthly', 'PKR', 7, $4, now(), $5, now(), $5)
            """, Ct, id, Company, code, threshold, BaselineSeed.SystemUserId);
        return id;
    }

    private Task SetAsync(string key, string json) =>
        _rig.ExecAsync("insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by) values (gen_random_uuid(), $1, 'tenant', null, $2::jsonb, now(), $3, now(), $3) on conflict (setting_key, layer, scope_id) do update set value = excluded.value",
            Ct, key, json, BaselineSeed.SystemUserId);

    private Task UnsetAsync(string key) => _rig.ExecAsync("delete from cfg.setting_values where setting_key = $1", Ct, key);

    /// <summary>A person with the permissions and field rules given, assigned for all companies.</summary>
    private async Task<Guid> PersonAsync(string[] permissions, (string Resource, string Field, string Access)[]? fields = null)
    {
        var user = await _rig.Docs.UserAsync(Ct);
        var role = Guid.CreateVersion7();
        await _rig.ExecAsync("insert into iam.roles (id, code, name_i18n, source, created_at, created_by, updated_at, updated_by) values ($1, $2, '{\"en\":\"Exporter\"}', 'tenant', now(), $3, now(), $3)", Ct, role, "ex" + Guid.NewGuid().ToString("N")[..10], BaselineSeed.SystemUserId);
        await _rig.ExecAsync("insert into iam.role_permissions (role_id, permission_id) select $1, id from iam.permissions where code = any($2)", Ct, role, permissions);
        await _rig.ExecAsync("insert into iam.role_data_scopes (role_id, resource_type, data_scope_id) select $1, '*', id from iam.data_scopes where code = 'all'", Ct, role);
        foreach (var (resource, field, access) in fields ?? [])
        {
            await _rig.ExecAsync("insert into iam.field_policies (id, role_id, resource_type, field_key, access, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, $4, $5, now(), $6, now(), $6)",
                Ct, Guid.CreateVersion7(), role, resource, field, access, BaselineSeed.SystemUserId);
        }

        await _rig.ExecAsync("insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, null, 'test', current_date - 1, now(), $4, now(), $4)", Ct, Guid.CreateVersion7(), user, role, BaselineSeed.SystemUserId);
        return user;
    }

    private object BranchBody(string format = "csv", string? search = null, string? sort = null, object? query = null, string? reason = null) => new
    {
        source = "org.branches",
        query = query ?? new { },
        search,
        sort,
        format,
        reason,
        companyId = Company,
        language = "en",
        columns = new[] { new { field = "code", label = "Code" }, new { field = "nameI18n", label = "Name" }, new { field = "isActive", label = "Active" } },
    };

    private async Task<(Guid Id, JsonElement View)> ExportAsync(object body, Guid? user = null, HttpStatusCode expect = HttpStatusCode.Accepted)
    {
        var answer = await _rig.PostJsonAsync(Exports, body, Ct, user);
        Assert.True(answer.Status == expect, answer.Text);
        if (expect != HttpStatusCode.Accepted)
        {
            return (Guid.Empty, answer.Json.Clone());
        }

        var id = answer.Json.GetProperty("id").GetGuid();
        await _rig.RunJobsAsync(Ct);
        return (id, (await _rig.GetAsync($"{Exports}/{id}", Ct, user)).Json.Clone());
    }

    private async Task<string> DownloadTextAsync(Guid id, Guid? user = null)
    {
        var file = await _rig.GetAsync($"{Exports}/{id}/download", Ct, user);
        Assert.True(file.Status == HttpStatusCode.OK, file.Text);
        return file.Text;
    }

    [Fact]
    public async Task S116b_FR_ADM_DAT_04_FR_ADM_NAV_02_a_list_is_exported_with_its_own_filter_search_and_sort_a_watermark_a_keeping_date_and_a_log()
    {
        await BranchAsync("EXA-1", "Karachi head office");
        await BranchAsync("EXA-2", "Lahore");
        await BranchAsync("EXA-3", "Multan");
        await BranchAsync("EXA-4", "Closed branch", active: false);
        await BranchAsync("OTHER", "Not searched");

        var (id, view) = await ExportAsync(BranchBody(search: "EXA-", sort: "-code", query: new { isActive = true }));
        Assert.True("done" == view.GetProperty("status").GetString(), view.ToString() + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.Equal(3, view.GetProperty("rowCount").GetInt32());
        Assert.True(view.GetProperty("canDownload").GetBoolean());
        var lines = (await DownloadTextAsync(id)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Exported by ", lines[0].TrimStart('﻿'), StringComparison.Ordinal);
        Assert.Contains("confidential", lines[0], StringComparison.Ordinal);
        Assert.Contains(_rig.Code, lines[0], StringComparison.Ordinal);
        Assert.Equal("Code,Name,Active", lines[1]);
        Assert.Equal(["EXA-3,Multan,true", "EXA-2,Lahore,true", "EXA-1,Karachi head office,true"], lines[2..]);

        // The row in the export log, the kept file with its date, and the change log (FR-ADM-DAT-04: every export is logged).
        var now = _rig.Docs.Clock.UtcNow;
        Assert.Equal("org.branches", await _rig.ScalarAsync<string>("select entity_type from itg.export_jobs where id = $1", Ct, id));
        var expires = await _rig.ScalarAsync<DateTime>("select expires_at from itg.export_jobs where id = $1", Ct, id);
        Assert.Equal(now.AddDays(7).UtcDateTime.Date, expires.Date);
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from doc.files f join itg.export_jobs e on e.file_id = f.id where e.id = $1 and f.owner_type = 'itg.export_jobs' and f.is_sensitive and f.document_type = 'export'", Ct, id));
        Assert.Equal(3, await _rig.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'itg.export_jobs' and entity_id = $1", Ct, id)); // asked, done, downloaded
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'itg.export_jobs' and entity_id = $1 and action = 'update'", Ct, id));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from aud.access_log where action = 'export' and entity_type = 'org.branches'", Ct));

        // The person sees their own exports in the centre.
        var list = await _rig.GetAsync(Exports, Ct);
        Assert.Contains(list.Json.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task S116b_FR_ADM_DAT_04_text_that_starts_like_a_formula_is_written_as_text_in_both_formats_and_the_workbook_carries_the_watermark()
    {
        await BranchAsync("EXF-1", "=1+1");
        await BranchAsync("EXF-2", "@SUM(A1)");
        await BranchAsync("EXF-3", "-5 days");

        var (csv, _) = await ExportAsync(BranchBody(search: "EXF-", sort: "code"));
        var text = await DownloadTextAsync(csv);
        Assert.Contains("EXF-1,'=1+1,true", text, StringComparison.Ordinal);
        Assert.Contains("EXF-2,'@SUM(A1),true", text, StringComparison.Ordinal);
        Assert.Contains("EXF-3,'-5 days,true", text, StringComparison.Ordinal);

        var (xlsx, view) = await ExportAsync(BranchBody("xlsx", search: "EXF-", sort: "code"));
        Assert.Equal("done", view.GetProperty("status").GetString());
        var file = await _rig.GetAsync($"{Exports}/{xlsx}/download", Ct);
        Assert.Equal(HttpStatusCode.OK, file.Status);
        using var zip = new ZipArchive(new MemoryStream(file.Bytes));
        var sheet = await new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEndAsync(Ct);
        Assert.Contains("'=1+1", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheet, StringComparison.Ordinal);
        Assert.Contains("confidential", sheet, StringComparison.Ordinal);
        Assert.Contains("<oddFooter>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S116b_FR_KRN_IAM_13_FR_KRN_AUD_02_GHR_06_hidden_fields_are_left_out_masked_fields_are_exported_masked_and_a_reason_is_asked_and_logged()
    {
        await PayGroupAsync("PGX-1", 1234567.5m);
        string[] permissions = ["hrm.pay_groups.read", "itg.exports.run"];
        var shown = await PersonAsync(permissions, [("hrm.pay_groups", "approval_threshold", "read_only")]);
        var masked = await PersonAsync(permissions, [("hrm.pay_groups", "approval_threshold", "masked")]);
        var hidden = await PersonAsync(permissions, [("hrm.pay_groups", "approval_threshold", "hidden")]);
        object Body(string[] fields, string? reason = null) => new
        {
            source = "hrm.pay_groups", query = new { }, search = "PGX-", format = "csv", reason, companyId = Company, language = "en",
            columns = fields.Select(f => new { field = f, label = f }).ToArray(),
        };

        // A person who sees the amount: the check says so, a reason is required, the file has the amount, the access log names the field.
        var check = await _rig.PostJsonAsync($"{Exports}/check", Body(["code", "approvalThreshold"]), Ct, shown);
        Assert.Equal(HttpStatusCode.OK, check.Status);
        Assert.True(check.Json.GetProperty("reasonRequired").GetBoolean());
        Assert.Equal("salary", check.Json.GetProperty("sensitiveClasses")[0].GetString());
        var noReason = await _rig.PostJsonAsync(Exports, Body(["code", "approvalThreshold"]), Ct, shown);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.Status);
        Assert.Equal("errors.export.reason_required", noReason.Json.GetProperty("title").GetString());
        var (id, view) = await ExportAsync(Body(["code", "approvalThreshold"], "Quarterly review of approval limits"), shown);
        Assert.True("done" == view.GetProperty("status").GetString(), view.ToString() + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.Contains("PGX-1,1234567.5", await DownloadTextAsync(id, shown), StringComparison.Ordinal);
        Assert.Equal("Quarterly review of approval limits", await _rig.ScalarAsync<string>("select reason from itg.export_jobs where id = $1", Ct, id));
        Assert.Equal("salary", await _rig.ScalarAsync<string>("select sensitive_classes[1] from itg.export_jobs where id = $1", Ct, id));
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from aud.access_log where action = 'export' and entity_type = 'hrm.pay_groups' and actor_user_id = $1 and 'approval_threshold' = any(fields)", Ct, shown));

        // A person with the amount masked: a reason is still asked (the column is in the file), the file has it masked, nothing unmasked is logged.
        Assert.Equal("errors.export.reason_required", (await _rig.PostJsonAsync(Exports, Body(["code", "approvalThreshold"]), Ct, masked)).Json.GetProperty("title").GetString());
        var (maskedId, maskedView) = await ExportAsync(Body(["code", "approvalThreshold"], "Check"), masked);
        Assert.Equal("done", maskedView.GetProperty("status").GetString());
        var maskedText = await DownloadTextAsync(maskedId, masked);
        Assert.Contains("PGX-1,****", maskedText, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567", maskedText, StringComparison.Ordinal);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from aud.access_log where action = 'export' and actor_user_id = $1", Ct, masked));

        // A person who may not see it: the column is left out, no reason is needed, and a list of only that column is refused.
        var (hiddenId, hiddenView) = await ExportAsync(Body(["code", "approvalThreshold"]), hidden);
        Assert.Equal("done", hiddenView.GetProperty("status").GetString());
        var hiddenLines = (await DownloadTextAsync(hiddenId, hidden)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("code", hiddenLines[1]);
        Assert.Equal("PGX-1", hiddenLines[2]);
        Assert.Equal("errors.export.no_columns", (await _rig.PostJsonAsync(Exports, Body(["approvalThreshold"]), Ct, hidden)).Json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task S116b_FR_ADM_DAT_04_a_list_over_the_row_limit_fails_with_its_reason_and_a_person_without_the_list_permission_cannot_export_it()
    {
        for (var i = 1; i <= 3; i++)
        {
            await BranchAsync("EXL-" + i, "Limit " + i);
        }

        await SetAsync("itg.export.max_rows", "2");
        try
        {
            var (id, view) = await ExportAsync(BranchBody(search: "EXL-"));
            Assert.Equal("failed", view.GetProperty("status").GetString());
            Assert.Equal("errors.export.too_many_rows", view.GetProperty("errorKey").GetString());
            Assert.False(view.GetProperty("canDownload").GetBoolean());
            Assert.Equal(HttpStatusCode.Conflict, (await _rig.GetAsync($"{Exports}/{id}/download", Ct)).Status);
            Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from doc.files where owner_type = 'itg.export_jobs' and owner_id = $1", Ct, id));
        }
        finally
        {
            await UnsetAsync("itg.export_jobs.max_rows");
            await UnsetAsync("itg.export.max_rows");
        }

        var onlyExport = await PersonAsync(["itg.exports.run"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.PostJsonAsync(Exports, BranchBody(), Ct, onlyExport)).Status);
        var noExportRight = await PersonAsync(["org.branches.read"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.PostJsonAsync(Exports, BranchBody(), Ct, noExportRight)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.GetAsync(Exports, Ct, noExportRight)).Status);
    }

    [Fact]
    public async Task S116b_FR_KRN_IAM_14_FR_XM_32_a_large_or_sensitive_export_waits_for_a_second_person_who_is_never_the_requester()
    {
        for (var i = 1; i <= 3; i++)
        {
            await BranchAsync("EXQ-" + i, "Approval " + i);
        }

        await PayGroupAsync("PGQ-1", 10m);
        await SetAsync("itg.export.requires_approval", "true");
        await SetAsync("itg.export.approval_rows", "2");
        try
        {
            var requester = await PersonAsync(["org.branches.read", "hrm.pay_groups.read", "itg.exports.run"]);
            var approver = await _rig.Docs.UserAsync(Ct, "tenant_admin");

            // Above the row limit for approval: held, no job queued, nothing in the file store.
            var (id, view) = await ExportAsync(BranchBody(search: "EXQ-"), requester);
            Assert.True("awaiting_approval" == view.GetProperty("status").GetString(), view + " " + (await _rig.PostJsonAsync($"{Exports}/check", BranchBody(search: "EXQ-"), Ct, requester)).Text);
            Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from doc.files where owner_type = 'itg.export_jobs' and owner_id = $1", Ct, id));
            var request = view.GetProperty("heldRequestId").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await _rig.GetAsync($"{Exports}/{id}/download", Ct, requester)).Status);

            // Nobody approves their own request (FR-XM-32); a second person with the approval right does.
            var self = await _rig.PostJsonAsync($"/api/iam/maker-checker-requests/{request}/approve", new { reason = "mine" }, Ct, requester);
            Assert.NotEqual(HttpStatusCode.OK, self.Status);
            var approved = await _rig.PostJsonAsync($"/api/iam/maker-checker-requests/{request}/approve", new { reason = "agreed" }, Ct, approver);
            Assert.True(approved.Status == HttpStatusCode.OK, approved.Text);
            await _rig.RunJobsAsync(Ct);
            var done = (await _rig.GetAsync($"{Exports}/{id}", Ct, requester)).Json;
            Assert.True("done" == done.GetProperty("status").GetString(), done.ToString() + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
            Assert.Contains("EXQ-1", await DownloadTextAsync(id, requester), StringComparison.Ordinal);
            // The approver gets no copy of the file (Q9).
            Assert.Equal(HttpStatusCode.Forbidden, (await _rig.GetAsync($"{Exports}/{id}/download", Ct, approver)).Status);

            // A sensitive export is held whatever its size; a refused one ends as rejected.
            var (heldId, held) = await ExportAsync(new
            {
                source = "hrm.pay_groups", query = new { }, search = "PGQ-", format = "csv", reason = "Needed", companyId = Company, language = "en",
                columns = new[] { new { field = "code", label = "Code" }, new { field = "approvalThreshold", label = "Limit" } },
            }, await PersonAsync(["hrm.pay_groups.read", "itg.exports.run"], [("hrm.pay_groups", "approval_threshold", "read_only")]));
            Assert.Equal("awaiting_approval", held.GetProperty("status").GetString());
            var rejected = await _rig.PostJsonAsync($"/api/iam/maker-checker-requests/{held.GetProperty("heldRequestId").GetGuid()}/reject", new { reason = "no" }, Ct, approver);
            Assert.True(rejected.Status == HttpStatusCode.OK, rejected.Text);
            Assert.Equal("rejected", (await _rig.GetAsync($"{Exports}/{heldId}", Ct, approver)).Json.GetProperty("status").GetString());
        }
        finally
        {
            await UnsetAsync("itg.export.requires_approval");
            await UnsetAsync("itg.export.approval_rows");
        }
    }

    [Fact]
    public async Task S116b_FR_ADM_DAT_04_only_the_person_who_made_an_export_downloads_its_file_holders_of_the_log_right_see_the_row_and_the_file_expires()
    {
        await BranchAsync("EXD-1", "Download");
        var maker = await PersonAsync(["org.branches.read", "itg.exports.run"]);
        var auditor = await PersonAsync(["itg.exports.run", "itg.export_jobs.read"]);
        var (id, view) = await ExportAsync(BranchBody(search: "EXD-"), maker);
        Assert.Equal("done", view.GetProperty("status").GetString());

        // Another person with the log right sees the row in the centre (all=true) but not the file; one without it sees nothing.
        var seen = await _rig.GetAsync($"{Exports}?all=true", Ct, auditor);
        Assert.Contains(seen.Json.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id && !i.GetProperty("canDownload").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.GetAsync($"{Exports}/{id}/download", Ct, auditor)).Status);
        var stranger = await PersonAsync(["itg.exports.run"]);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync($"{Exports}/{id}", Ct, stranger)).Status);
        Assert.DoesNotContain((await _rig.GetAsync($"{Exports}?all=true", Ct, stranger)).Json.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);

        // A person who lost the right to read the list can no longer fetch an old file.
        await _rig.ExecAsync("delete from iam.user_roles where user_id = $1", Ct, maker);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.GetAsync($"{Exports}/{id}/download", Ct, maker)).Status);

        // After the keeping period the file is deleted by the daily job and the row shows expired.
        _rig.Docs.Clock.Advance(TimeSpan.FromDays(8));
        await _rig.Jobs.QueueAsync(Erp.Kernel.Contracts.Itg.ExportCodes.ExpireJob, ct: Ct);
        await _rig.RunJobsAsync(Ct);
        var fileKey = await _rig.ScalarAsync<string>("select f.storage_key from doc.files f join itg.export_jobs e on e.file_id = f.id where e.id = $1", Ct, id);
        Assert.Equal("purged", fileKey);
        Assert.Equal("expired", await _rig.ScalarAsync<string>("select status from itg.export_jobs where id = $1", Ct, id));
    }

    [Fact]
    public async Task S116b_FR_KRN_ENT_04_exports_are_allowed_in_read_only_and_locked_states()
    {
        await BranchAsync("EXR-1", "Read only");
        foreach (var days in new[] { 20, 60 })
        {
            await fx.SetDaysAfterEndAsync(_rig.Code, days, Ct);
            var (id, view) = await ExportAsync(BranchBody(search: "EXR-"));
            Assert.True("done" == view.GetProperty("status").GetString(), $"{days}: {view} {await _rig.Jobs.DumpAsync(Ct, _rig.Code)}");
            Assert.Contains("EXR-1", await DownloadTextAsync(id), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task S116b_FR_ADM_DAT_04_a_request_for_an_unknown_list_column_format_or_company_is_refused_with_its_key()
    {
        async Task<string> TitleAsync(object body) => (await _rig.PostJsonAsync(Exports, body, Ct)).Json.GetProperty("title").GetString()!;
        Assert.Equal("errors.export.source_unknown", await TitleAsync(new { source = "nope.nothing", format = "csv", companyId = Company, columns = new[] { new { field = "code", label = "c" } } }));
        Assert.Equal("errors.export.column_unknown", await TitleAsync(new { source = "org.branches", format = "csv", companyId = Company, columns = new[] { new { field = "nothing", label = "c" } } }));
        Assert.Equal("errors.export.format_unsupported", await TitleAsync(new { source = "org.branches", format = "pdf", companyId = Company, columns = new[] { new { field = "code", label = "c" } } }));
        Assert.Equal("errors.export.company_required", await TitleAsync(new { source = "org.branches", format = "csv", companyId = Guid.NewGuid(), columns = new[] { new { field = "code", label = "c" } } }));
        Assert.Equal("errors.export.no_columns", await TitleAsync(new { source = "org.branches", format = "csv", companyId = Company, columns = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync($"{Exports}/{Guid.NewGuid()}", Ct)).Status);
    }
}
