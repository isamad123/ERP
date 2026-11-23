using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Jobs;

namespace Erp.IntegrationTests.Jobs;

/// <summary>
/// The job-centre API of slice S1.11a (Build item 7; FR-KRN-JOB-03, FR-ADM-OPS-01, the job numbers of FR-ADM-OPS-05) in the
/// real API pipeline with the real access engine: jobs with their next run, run now, the run list with filters, a run with
/// its log, rerun, cancel, schedule change for editable jobs only (audited), and health. Each refusal is tested.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class JobCentreApiTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;
    private string _code = null!;

    /// <summary>One customer per test: two random characters could repeat in a run, and a second test would then share the first one's customer.</summary>
    private static int _next;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _code = await _h.ProvisionAsync("jc" + (Interlocked.Increment(ref _next) & 0xff).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<T> ScalarAsync<T>(string sql, params object?[] values)
    {
        await using var c = await _h.OwnerAsync(_code, Ct);
        return await AccessHarness.ScalarAsync<T>(c, sql, Ct, values);
    }

    [Fact]
    public async Task S111a_FR_ADM_OPS_01_an_administrator_lists_jobs_runs_one_now_sees_it_with_its_log_cancels_it_and_runs_it_again()
    {
        var admin = await _h.UserAsync(Ct, _code, "tenant_admin");
        var jobs = await _h.GetAsync(admin, "/api/jobs/definitions", Ct, _code);
        Assert.True(jobs.Status == HttpStatusCode.OK, jobs.Body);
        var purge = jobs.Json.EnumerateArray().Single(j => j.GetProperty("code").GetString() == KernelJobs.Purge);
        Assert.Equal("Daily clean-up", purge.GetProperty("name").GetProperty("en").GetString());
        Assert.True(purge.GetProperty("scheduleEditable").GetBoolean());
        Assert.True(purge.GetProperty("licensed").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.String, JsonValueKindOf(purge, "nextRunAt"));
        var id = purge.GetProperty("id").GetGuid();

        var queued = await _h.PostAsync(admin, $"/api/jobs/definitions/{id}/run", new { }, Ct, _code);
        Assert.True(queued.Status == HttpStatusCode.Created, queued.Body);
        var run = queued.Json.GetProperty("id").GetGuid();
        Assert.Equal("queued", queued.Json.GetProperty("status").GetString());
        Assert.Equal(admin, queued.Json.GetProperty("requestedBy").GetGuid());

        var list = await _h.GetAsync(admin, $"/api/jobs/runs?code={KernelJobs.Purge}&status=queued", Ct, _code);
        Assert.Contains(list.Json.EnumerateArray(), r => r.GetProperty("id").GetGuid() == run);
        Assert.Empty((await _h.GetAsync(admin, $"/api/jobs/runs?code={KernelJobs.Purge}&status=failed", Ct, _code)).Json.EnumerateArray());
        var detail = await _h.GetAsync(admin, $"/api/jobs/runs/{run}", Ct, _code);
        Assert.Contains(detail.Json.GetProperty("logs").EnumerateArray(), l => l.GetProperty("message").GetString()!.StartsWith("queued by", StringComparison.Ordinal));

        var cancelled = await _h.PostAsync(admin, $"/api/jobs/runs/{run}/cancel", null, Ct, _code);
        Assert.Equal("cancelled", cancelled.Json.GetProperty("status").GetString());
        var again = await _h.PostAsync(admin, $"/api/jobs/runs/{run}/cancel", null, Ct, _code);
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal("errors.job.run_finished", again.Text("title"));

        var rerun = await _h.PostAsync(admin, $"/api/jobs/runs/{run}/rerun", null, Ct, _code);
        Assert.True(rerun.Status == HttpStatusCode.Created, rerun.Body);
        Assert.NotEqual(run, rerun.Json.GetProperty("id").GetGuid());
        var rerunTwice = await _h.PostAsync(admin, $"/api/jobs/runs/{rerun.Json.GetProperty("id").GetGuid()}/rerun", null, Ct, _code);
        Assert.Equal("errors.job.run_not_finished", rerunTwice.Text("title"));

        // Undeclared parameters are refused; an unknown run is 404.
        var bad = await _h.PostAsync(admin, $"/api/jobs/definitions/{id}/run", new { parameters = new { rows = 5 } }, Ct, _code);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal("errors.job.parameters_invalid", bad.Text("title"));
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(admin, $"/api/jobs/runs/{Guid.CreateVersion7()}", Ct, _code)).Status);

        var health = await _h.GetAsync(admin, "/api/jobs/health", Ct, _code);
        Assert.True(health.Status == HttpStatusCode.OK, health.Body);
        Assert.True(health.Json.GetProperty("queued").GetInt32() >= 1);
        Assert.Contains(health.Json.GetProperty("events").EnumerateArray(), e => e.GetProperty("schema").GetString() == "job");
    }

    [Fact]
    public async Task S111a_FR_ADM_OPS_01_only_editable_jobs_change_schedule_the_change_is_audited_and_stale_or_invalid_changes_are_refused()
    {
        var admin = await _h.UserAsync(Ct, _code, "tenant_admin");
        var purge = await ScalarAsync<Guid>("select id from job.definitions where code = $1", KernelJobs.Purge);
        var verify = await ScalarAsync<Guid>("select id from job.definitions where code = $1", KernelJobs.AuditVerify);
        var version = await ScalarAsync<long>("select row_version from job.definitions where id = $1", purge);

        var changed = await _h.PutAsync(admin, $"/api/jobs/definitions/{purge}/schedule", new { schedule = new { daily = new { at = "04:15" } }, isEnabled = false, rowVersion = version }, Ct, _code);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        Assert.False(changed.Json.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(admin, await ScalarAsync<Guid>("select updated_by from job.definitions where id = $1", purge)); // kept on upgrade from now on
        Assert.Equal(1, await ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'job.definitions' and entity_id = $1 and actor_user_id = $2", purge, admin));

        // An upgrade keeps the customer's schedule (JobSetup, Build item 1).
        await using (var c = await _h.OwnerAsync(_code, Ct))
        {
            await JobSetup.EnsureAsync(c, null, JobManifest.Parse(JobManifest.KernelJson), _h.Clock.UtcNow, Ct);
        }

        Assert.Equal("04:15", await ScalarAsync<string>("select schedule->'daily'->>'at' from job.definitions where id = $1", purge));
        Assert.False(await ScalarAsync<bool>("select is_enabled from job.definitions where id = $1", purge));

        var stale = await _h.PutAsync(admin, $"/api/jobs/definitions/{purge}/schedule", new { isEnabled = true, rowVersion = version }, Ct, _code);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        var invalid = await _h.PutAsync(admin, $"/api/jobs/definitions/{purge}/schedule", new { schedule = new { cron = "* * * * *" }, isEnabled = true, rowVersion = version + 1 }, Ct, _code);
        Assert.Equal("errors.job.schedule_invalid", invalid.Text("title"));
        var locked = await _h.PutAsync(admin, $"/api/jobs/definitions/{verify}/schedule", new { isEnabled = false, rowVersion = 1 }, Ct, _code);
        Assert.Equal(HttpStatusCode.Forbidden, locked.Status);
        Assert.Equal("errors.job.schedule_not_editable", locked.Text("title"));
    }

    [Fact]
    public async Task S111a_FR_ADM_OPS_01_without_the_permissions_the_job_centre_refuses_and_the_generated_job_writes_are_gone()
    {
        var auditor = await _h.UserAsync(Ct, _code, "auditor"); // job.*.read only
        var user = await _h.UserAsync(Ct, _code, "business_user");
        var purge = await ScalarAsync<Guid>("select id from job.definitions where code = $1", KernelJobs.Purge);

        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(auditor, "/api/jobs/runs", Ct, _code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PostAsync(auditor, $"/api/jobs/definitions/{purge}/run", new { }, Ct, _code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(auditor, $"/api/jobs/definitions/{purge}/schedule", new { isEnabled = false, rowVersion = 1 }, Ct, _code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(user, "/api/jobs/definitions", Ct, _code)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(user, "/api/jobs/health", Ct, _code)).Status);

        // CR-046 item 2 (P2): the generated endpoints only read.
        var admin = await _h.UserAsync(Ct, _code, "tenant_admin");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _h.PostAsync(admin, "/api/job/runs", new { definitionId = purge, status = "running" }, Ct, _code)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(admin, "/api/job/runs", Ct, _code)).Status);
    }

    private static System.Text.Json.JsonValueKind JsonValueKindOf(System.Text.Json.JsonElement e, string name) => e.GetProperty(name).ValueKind;
}
