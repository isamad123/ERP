using System.Net;
using System.Text;
using Erp.IntegrationTests.Documents;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.ImportCentre;

/// <summary>The import engine of slice S1.16a (FR-KRN-INT-03, -04, FR-ADM-DAT-01, FR-ADM-INT-04) in the real API and worker pipeline.</summary>
[Collection(TenancyGroup.Name)]
public sealed class ImportTests(TenancyFixture fx) : IAsyncLifetime
{
    private ImportRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await ImportRig.StartAsync(fx, Ct);

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    [Fact]
    public async Task S116a_FR_KRN_INT_03_FR_ADM_DAT_01_FR_ADM_USR_02_a_users_file_is_checked_without_saving_then_committed_with_invitations_and_rolled_back_until_a_user_activates()
    {
        // 500 rows, 12 of them bad: the check finds exactly those and saves nothing.
        var before = await _rig.ScalarAsync<long>("select count(*) from iam.users", Ct);
        var bad = new[] { 3, 40, 77, 120, 150, 201, 250, 299, 333, 400, 450, 499 };
        var (id, batch) = await _rig.UploadAndValidateAsync("users", "users.csv", ImportRig.UsersCsv("imp", 500, bad), Ct);
        Assert.True(batch.GetProperty("status").GetString() == "validated", batch.ToString() + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.True(488 == batch.GetProperty("okRows").GetInt32(), (await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors?limit=5", Ct)).Text + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.Equal(12, batch.GetProperty("errorRows").GetInt32());
        Assert.Equal(before, await _rig.ScalarAsync<long>("select count(*) from iam.users", Ct));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'iam.users' and source = 'import'", Ct));
        var errors = await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors?limit=100", Ct);
        Assert.Equal(12, errors.Json.GetProperty("items").GetArrayLength());
        var file = await _rig.GetAsync($"/api/itg/imports/batches/{id}/errors.csv", Ct);
        Assert.Equal(HttpStatusCode.OK, file.Status);
        Assert.Equal(13, file.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("message", file.Text, StringComparison.Ordinal);

        // A second check or a commit of the first is refused when it has problems and no choice was made.
        var refused = await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/validate", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        var withErrors = await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/commit", new { skipErrors = false }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, withErrors.Status);

        // The corrected file commits all 500 as invited users, each with an invitation, audited as an import.
        var (fixedId, fixedBatch) = await _rig.UploadAndValidateAsync("users", "users-fixed.csv", ImportRig.UsersCsv("imq", 500), Ct);
        Assert.Equal(500, fixedBatch.GetProperty("okRows").GetInt32());
        var committed = await _rig.CommitAsync(fixedId, Ct);
        Assert.True(committed.GetProperty("status").GetString() == "committed", committed.ToString() + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.Equal(500, committed.GetProperty("createdRows").GetInt32());
        Assert.Equal(before + 500, await _rig.ScalarAsync<long>("select count(*) from iam.users", Ct));
        Assert.Equal(500, await _rig.ScalarAsync<long>("select count(*) from iam.users where username like 'imq%' and status = 'invited'", Ct));
        await _rig.DispatchEventsAsync(Ct);
        var queued = await _rig.ScalarAsync<long>("select count(*) from ntf.outbound_messages m join iam.users u on u.id = m.user_id where u.username like 'imq%' and m.event_code = 'identity.invitation'", Ct);
        Assert.True(queued == 500, queued + " " + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.Equal(500, await _rig.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'iam.users' and source = 'import'", Ct));
        Assert.True(committed.GetProperty("canRollback").GetBoolean());

        // One of them activates: the batch can no longer be rolled back.
        await _rig.ExecAsync("update iam.users set status = 'active', row_version = row_version + 1 where username = 'imq0007'", Ct);
        var after = (await _rig.GetAsync($"/api/itg/imports/batches/{fixedId}", Ct)).Json;
        Assert.False(after.GetProperty("canRollback").GetBoolean());
        Assert.Equal("errors.import.rollback_changed", after.GetProperty("rollbackBlockedKey").GetString());
        var no = await _rig.PostJsonAsync($"/api/itg/imports/batches/{fixedId}/rollback", new { reason = "test" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, no.Status);

        // An untouched batch rolls back.
        var (smallId, _) = await _rig.UploadAndValidateAsync("users", "small.csv", ImportRig.UsersCsv("imr", 5), Ct);
        await _rig.CommitAsync(smallId, Ct);
        var yes = await _rig.PostJsonAsync($"/api/itg/imports/batches/{smallId}/rollback", new { reason = "test" }, Ct);
        Assert.True(yes.Status == HttpStatusCode.OK, yes.Text);
        Assert.Equal("rolled_back", yes.Json.GetProperty("status").GetString());
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from iam.users where username like 'imr%' and not is_deleted", Ct));
    }

    [Fact]
    public async Task S116a_FR_KRN_INT_03_an_infected_unsupported_empty_or_oversized_file_is_refused_before_it_is_read()
    {
        var template = await _rig.TemplateAsync("users", Ct);
        var infected = await _rig.UploadAsync(template, "virus.csv", Encoding.UTF8.GetBytes("username,email,display_name\r\nEICAR,a@b.test,X\r\n"), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, infected.Status);
        Assert.Equal("errors.import.infected", infected.Json.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _rig.UploadAsync(template, "pic.png", [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0], Ct)).Status);
        Assert.Equal("errors.import.empty_file", (await _rig.UploadAsync(template, "empty.csv", [], Ct)).Json.GetProperty("title").GetString());
        var missing = await _rig.UploadAsync(template, "headers.csv", Encoding.UTF8.GetBytes("a,b\r\n1,2\r\n"), Ct);
        Assert.Equal("errors.import.header_missing", missing.Json.GetProperty("title").GetString());
        await _rig.ExecAsync("insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by) values (gen_random_uuid(), 'itg.import.max_rows', 'tenant', null, '3'::jsonb, now(), $1, now(), $1) on conflict (setting_key, layer, scope_id) do update set value = excluded.value",
            Ct, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        FileAnswer many;
        try
        {
            many = await _rig.UploadAsync(template, "many.csv", ImportRig.UsersCsv("big", 4), Ct);
        }
        finally
        {
            await _rig.ExecAsync("delete from cfg.setting_values where setting_key = 'itg.import.max_rows'", Ct);
        }

        // Over the row limit: the upload does not count rows (review finding 2); the check run does and says so as a problem of the file.
        Assert.Equal(HttpStatusCode.Created, many.Status);
        var batchId = many.Json.GetProperty("id").GetGuid();
        await _rig.ExecAsync("insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by) values (gen_random_uuid(), 'itg.import.max_rows', 'tenant', null, '3'::jsonb, now(), $1, now(), $1) on conflict (setting_key, layer, scope_id) do update set value = excluded.value",
            Ct, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        try
        {
            await _rig.PostJsonAsync($"/api/itg/imports/batches/{batchId}/validate", null, Ct);
            await _rig.RunJobsAsync(Ct);
        }
        finally
        {
            await _rig.ExecAsync("delete from cfg.setting_values where setting_key = 'itg.import.max_rows'", Ct);
        }

        var checkedBatch = (await _rig.GetAsync($"/api/itg/imports/batches/{batchId}", Ct)).Json;
        Assert.Equal(0, checkedBatch.GetProperty("okRows").GetInt32());
        Assert.Contains("errors.import.too_many_rows", (await _rig.GetAsync($"/api/itg/imports/batches/{batchId}/errors", Ct)).Text, StringComparison.Ordinal);
    }
}
