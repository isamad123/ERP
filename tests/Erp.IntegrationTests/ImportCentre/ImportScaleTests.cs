using System.Globalization;
using System.Text;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.ImportCentre;

/// <summary>The size and restart promises of the import engine (slice S1.16a, FR-KRN-INT-04): 50,000 rows, a worker that stops half way.</summary>
[Collection(TenancyGroup.Name)]
public sealed class ImportScaleTests(TenancyFixture fx) : IAsyncLifetime
{
    private const int Rows = 50_000;
    private ImportRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await ImportRig.StartAsync(fx, Ct);

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    [Fact]
    public async Task S116a_FR_KRN_INT_04_a_50000_row_holiday_file_is_checked_and_committed_in_the_background_and_a_stopped_worker_finishes_it_without_duplicates()
    {
        // 50 calendars of 1,000 days each: the key of a holiday is its calendar and its date.
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await _rig.ExecAsync("""
            insert into ref.calendars (id, code, name_i18n, source, is_active, created_at, created_by, updated_at, updated_by)
            select gen_random_uuid(), 'S' || $1 || lpad(n::text, 2, '0'), '{"en":"Scale"}', 'tenant', true, now(), $2, now(), $2 from generate_series(0, 49) n
            """, Ct, tag, Erp.Kernel.Infrastructure.ControlPlane.Provisioning.BaselineSeed.SystemUserId);
        var text = new StringBuilder("calendar,holiday_date,name_en,name_ur,kind,is_tentative\r\n");
        var start = new DateOnly(2030, 1, 1);
        for (var c = 0; c < 50; c++)
        {
            for (var d = 0; d < 1000; d++)
            {
                text.Append(CultureInfo.InvariantCulture, $"S{tag}{c:00},{start.AddDays(d):yyyy-MM-dd},Day {c}-{d},,public,no\r\n");
            }
        }

        var template = await _rig.TemplateAsync("holidays", Ct);
        var up = await _rig.UploadAsync(template, "holidays.csv", Encoding.UTF8.GetBytes(text.ToString()), Ct);
        Assert.True(up.Status == System.Net.HttpStatusCode.Created, up.Text);
        var id = up.Json.GetProperty("id").GetGuid();
        Assert.Equal(Rows, up.Json.GetProperty("totalRows").GetInt32());

        await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/validate", null, Ct);
        await _rig.RunJobsAsync(Ct);
        var batch = (await _rig.GetAsync($"/api/itg/imports/batches/{id}", Ct)).Json;
        Assert.True(batch.GetProperty("status").GetString() == "validated" && batch.GetProperty("okRows").GetInt32() == Rows, batch + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from ref.holidays h join ref.calendars c on c.id = h.calendar_id where c.code like $1", Ct, "S" + tag + "%"));

        // The commit starts on one worker, which stops (as a crashed process would) after some chunks were saved.
        var a = _rig.NewWorker();
        await _rig.PostJsonAsync($"/api/itg/imports/batches/{id}/commit", null, Ct);
        Assert.Equal(1, (await _rig.Jobs.PollAsync(a, Ct)).Started);
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (await _rig.ScalarAsync<long>("select count(*) from itg.import_batch_rows where batch_id = $1", Ct, id) < 5_000 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, Ct);
        }

        await a.KillAsync();
        var saved = await _rig.ScalarAsync<long>("select count(*) from itg.import_batch_rows where batch_id = $1", Ct, id);
        Assert.InRange(saved, 1, Rows - 1);
        Assert.Equal("running", await _rig.Jobs.StatusAsync(batch.GetProperty("jobRunId").GetGuid(), Ct));

        // Another worker takes the run over once the lease has run out and finishes the batch.
        _rig.Docs.Clock.Advance(TimeSpan.FromSeconds(61));
        _rig.NewWorker();
        await _rig.RunJobsAsync(Ct);
        var done = (await _rig.GetAsync($"/api/itg/imports/batches/{id}", Ct)).Json;
        Assert.True(done.GetProperty("status").GetString() == "committed", done + await _rig.Jobs.DumpAsync(Ct, _rig.Code));
        Assert.Equal(Rows, done.GetProperty("createdRows").GetInt32());
        Assert.Equal(Rows, await _rig.ScalarAsync<long>("select count(*) from ref.holidays h join ref.calendars c on c.id = h.calendar_id where c.code like $1", Ct, "S" + tag + "%"));
        Assert.Equal(Rows, await _rig.ScalarAsync<long>("select count(distinct (table_name, record_id)) from itg.import_batch_rows where batch_id = $1", Ct, id));
        Assert.Equal(Rows, await _rig.ScalarAsync<long>("select count(*) from itg.import_batch_rows where batch_id = $1", Ct, id));
    }
}
