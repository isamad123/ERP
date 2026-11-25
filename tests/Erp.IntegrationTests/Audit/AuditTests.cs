using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Identity;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Audit;

/// <summary>
/// Slice S1.07 acceptance, end to end through the real API pipeline, the real access engine and real customer databases:
/// change log in the same transaction, tamper-proof storage and the hash chain, per-person keys, the access log, security
/// events, search, history and export. Tests that damage a chain run on a customer provisioned for that test alone.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AuditTests(TenancyFixture fx) : IAsyncLifetime
{
    private AccessHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ change log (build item 1)

    [Fact]
    public async Task FR_KRN_AUD_01_GHR_09_create_update_delete_write_entries_with_actor_source_correlation_and_both_values()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var code = "aud" + Guid.NewGuid().ToString("N")[..8];
        var company = await _h.DefaultCompanyAsync(Ct);
        var branch = await _h.ScalarAsync<Guid>("select id from org.branches where company_id = $1 and not is_deleted order by created_at limit 1", Ct, company);
        object Body(string name, long? rowVersion) => new { companyId = company, branchId = branch, code, nameI18n = new { en = name }, address = new { line1 = "1 Mall Road" }, regionCode = "PB", timeZone = "Asia/Karachi", rowVersion };
        var created = await SendAsync(HttpMethod.Post, admin, "/api/org/locations", Body("Audit site", null));
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var id = created.Json.GetProperty("id").GetGuid();
        var updated = await SendAsync(HttpMethod.Put, admin, $"/api/org/locations/{id}", Body("Audit site 2", 1));
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        var deleted = await SendAsync(HttpMethod.Delete, admin, $"/api/org/locations/{id}?rowVersion=2", null);
        Assert.True(deleted.Status is HttpStatusCode.NoContent or HttpStatusCode.OK, $"{deleted.Status} {deleted.Body}");

        var rows = await EntriesAsync("org.locations", id);
        Assert.Equal(["create", "update", "delete"], rows.Select(r => r.Action));
        Assert.All(rows, r =>
        {
            Assert.Equal(admin, r.Actor);
            Assert.Equal("user", r.ActorKind);
            Assert.Equal("ui", r.Source);
            Assert.NotNull(r.Correlation);
        });
        Assert.Equal(created.Correlation, rows[0].Correlation);
        Assert.Equal(updated.Correlation, rows[1].Correlation);
        var name = rows[1].Changes.EnumerateArray().Single(c => c.GetProperty("field").GetString() == "name_i18n");
        Assert.Equal("Audit site", name.GetProperty("before").GetProperty("en").GetString());
        Assert.Equal("Audit site 2", name.GetProperty("after").GetProperty("en").GetString());
        Assert.Equal(company, rows[0].Company);
        Assert.Contains(rows[2].Changes.EnumerateArray(), c => c.GetProperty("field").GetString() == "is_deleted" && c.GetProperty("after").GetBoolean());
    }

    [Fact]
    public async Task FR_KRN_AUD_01_a_business_change_that_rolls_back_leaves_no_entry()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var code = "aud" + Guid.NewGuid().ToString("N")[..8];
        var created = await SendAsync(HttpMethod.Post, admin, "/api/org/org-unit-types", new { code, nameI18n = new { en = "Audit type" } });
        var id = created.Json.GetProperty("id").GetGuid();

        // A lost update (stale row version) and a duplicate code are refused and rolled back: no entry for either.
        var stale = await SendAsync(HttpMethod.Put, admin, $"/api/org/org-unit-types/{id}", new { code, nameI18n = new { en = "Changed" }, rowVersion = 7 });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        var duplicate = await SendAsync(HttpMethod.Post, admin, "/api/org/org-unit-types", new { code, nameI18n = new { en = "Duplicate" } });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);

        Assert.Equal(["create"], (await EntriesAsync("org.org_unit_types", id)).Select(r => r.Action));
        Assert.Equal(1L, await _h.ScalarAsync<long>("""select count(*) from aud.change_log where entity_type = 'org.org_unit_types' and changes @> jsonb_build_array(jsonb_build_object('field', 'code', 'after', $1::text))""", Ct, code));
    }

    [Fact]
    public async Task FR_KRN_AUD_01_if_the_audit_write_fails_the_business_change_rolls_back()
    {
        var code = await _h.ProvisionAsync("af", Ct);
        var admin = await _h.UserAsync(Ct, code, "tenant_admin");
        await using (var owner = await _h.OwnerAsync(code, Ct))
        {
            await AccessHarness.ExecAsync(owner, "revoke execute on function aud.append_changes(jsonb) from app_rw", Ct);
        }

        var typeCode = "aud" + Guid.NewGuid().ToString("N")[..8];
        HttpStatusCode? status = null;
        try
        {
            status = (await SendAsync(HttpMethod.Post, admin, "/api/org/org-unit-types", new { code = typeCode, nameI18n = new { en = "x" } }, code)).Status;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // The test server passes the unhandled error through; production answers 500.
        }

        Assert.True(status is null or HttpStatusCode.InternalServerError, $"got {status}");
        await using var check = await _h.OwnerAsync(code, Ct);
        Assert.Equal(0L, await AccessHarness.ScalarAsync<long>(check, "select count(*) from org.org_unit_types where code = $1", Ct, typeCode));
        await AccessHarness.ExecAsync(check, "grant execute on function aud.append_changes(jsonb) to app_rw", Ct);
    }

    // ------------------------------------------------------------------ tamper-proof storage and hash chain (build item 2)

    [Theory]
    [InlineData(TenantLogins.ReadWrite)]
    [InlineData(TenantLogins.Audit)]
    public async Task FR_KRN_AUD_03_Spec03_10_inv3_audit_rows_cannot_be_updated_deleted_or_truncated_by_the_application_logins(string login)
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        await SendAsync(HttpMethod.Post, admin, "/api/org/org-unit-types", new { code = "aud" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "x" } });
        await using var c = await fx.OpenTenantAsync(_h.Code, login, Ct);
        foreach (var table in new[] { "aud.change_log", "aud.security_events", "aud.access_log" })
        {
            foreach (var sql in new[] { $"update {table} set occurred_at = occurred_at", $"delete from {table}", $"truncate {table}" })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(c, sql, Ct));
                Assert.True(error.SqlState is PostgresErrorCodes.InsufficientPrivilege or "ER001", $"{login}: {sql}: {error.SqlState} {error.MessageText}");
            }
        }

        // Neither login can write the chain directly: only through aud.append_changes (Q1; app_audit since CR-031, R-011).
        var forged = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(c, """
            insert into aud.change_log (id, occurred_at, actor_kind, entity_type, entity_id, action, changes, prev_hash, row_hash, created_at, created_by)
            values (gen_random_uuid(), now(), 'system', 'x', gen_random_uuid(), 'create', '[]', '\x00', '\x00', now(), gen_random_uuid())
            """, Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, forged.SqlState);
        foreach (var table in new[] { "aud.security_events", "aud.access_log", "aud.chain_anchors", "aud.subject_keys" })
        {
            var direct = await Assert.ThrowsAsync<PostgresException>(() => AccessHarness.ExecAsync(c, $"insert into {table} select * from {table} limit 0", Ct));
            Assert.True(direct.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"{login}: insert into {table}: {direct.SqlState}");
        }
    }

    [Fact]
    public async Task FR_KRN_AUD_03_CR031_the_audit_login_appends_only_through_the_functions_and_the_chain_holds()
    {
        var code = await _h.ProvisionAsync("al", Ct);
        await using (var audit = await fx.OpenTenantAsync(code, TenantLogins.Audit, Ct))
        {
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(1, await AuditDb.AppendChangesAsync(audit, null, [new ChangeEntry(Guid.CreateVersion7(now), now, null, "system", "org.companies", Guid.CreateVersion7(now), null, "update",
                [new System.Text.Json.Nodes.JsonObject { ["field"] = "code", ["after"] = "x" }], null, null, null, "system", BaselineSeed.SystemUserId)], Ct));
        }

        await using var rw = await fx.OpenTenantAsync(code, TenantLogins.ReadWrite, Ct);
        var today = AccessHarness.Day(DateTimeOffset.UtcNow);
        var result = await ChainVerifier.VerifyAsync(rw, null, today.AddDays(-1), today.AddDays(1), markVerified: false, _h.Clock, Ct);
        Assert.True(result.Intact, string.Join(Environment.NewLine, result.Findings));
        Assert.Equal(1, result.RowsChecked);
    }

    [Fact]
    public async Task FR_KRN_AUD_03_CR032_an_entry_stamped_ahead_of_the_database_clock_takes_the_database_time_and_raises_a_warning()
    {
        var code = await _h.ProvisionAsync("ak", Ct);
        await using var rw = await fx.OpenTenantAsync(code, TenantLogins.ReadWrite, Ct);
        var ahead = DateTimeOffset.UtcNow.AddDays(2);
        var entity = Guid.CreateVersion7();
        await AuditDb.AppendChangesAsync(rw, null, [new ChangeEntry(Guid.CreateVersion7(ahead), ahead, null, "system", "org.companies", entity, null, "update",
            [new System.Text.Json.Nodes.JsonObject { ["field"] = "code", ["after"] = "x" }], null, null, null, "system", BaselineSeed.SystemUserId)], Ct);

        var stored = await AccessHarness.ScalarAsync<DateTime>(rw, "select occurred_at from aud.change_log where entity_id = $1", Ct, entity);
        var databaseNow = await AccessHarness.ScalarAsync<DateTime>(rw, "select now()", Ct);
        Assert.True(stored <= databaseNow.AddMinutes(1), $"stored {stored:O}, database {databaseNow:O}");
        Assert.Equal(1L, await AccessHarness.ScalarAsync<long>(rw, "select count(*) from aud.security_events where event_type = 'aud.clock_ahead' and severity = 'warning'", Ct));

        // A small difference (under 5 minutes) is kept as it is, and the chain still verifies.
        var slightly = DateTimeOffset.UtcNow.AddMinutes(2);
        var second = Guid.CreateVersion7();
        await AuditDb.AppendChangesAsync(rw, null, [new ChangeEntry(Guid.CreateVersion7(slightly), slightly, null, "system", "org.companies", second, null, "update",
            [new System.Text.Json.Nodes.JsonObject { ["field"] = "code", ["after"] = "y" }], null, null, null, "system", BaselineSeed.SystemUserId)], Ct);
        var kept = await AccessHarness.ScalarAsync<DateTime>(rw, "select occurred_at from aud.change_log where entity_id = $1", Ct, second);
        Assert.True(Math.Abs((kept - slightly.UtcDateTime).TotalMilliseconds) < 1, $"kept {kept:O}, offered {slightly:O}");
        var today = AccessHarness.Day(DateTimeOffset.UtcNow);
        Assert.True((await ChainVerifier.VerifyAsync(rw, null, today.AddDays(-1), today.AddDays(1), markVerified: false, _h.Clock, Ct)).Intact);
    }

    [Fact]
    public async Task FR_KRN_AUD_03_R012_a_refused_audit_write_in_hand_written_sql_is_a_critical_security_event()
    {
        var code = await _h.ProvisionAsync("at", Ct);
        await using var rw = await fx.OpenTenantAsync(code, TenantLogins.ReadWrite, Ct);
        const string Sql = "update aud.change_log set action = 'update'";
        var error = await Assert.ThrowsAsync<PostgresException>(() => TamperAttempt.GuardAsync(rw, Sql, async () =>
        {
            await AccessHarness.ExecAsync(rw, Sql, Ct);
            return 0;
        }));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        Assert.Equal(1L, await AccessHarness.ScalarAsync<long>(rw, "select count(*) from aud.security_events where event_type = 'aud.tamper_attempt' and severity = 'critical'", Ct));
    }

    [Fact]
    public async Task FR_KRN_AUD_03_S107_Q9_an_attempt_through_the_application_is_a_critical_security_event()
    {
        const string Route = "/api/test/audit-tamper";
        await using var app = await fx.StartAppAsync(a => a.MapPost(Route, async (KernelDbContext db, HttpContext http) =>
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync("update aud.change_log set action = 'update'", http.RequestAborted);
                    return Results.Ok();
                }
                catch (PostgresException)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }).RequireAuthorization(), Ct, realAccess: true);
        var before = await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'aud.tamper_attempt' and severity = 'critical'", Ct);
        using var client = TenancyFixture.Client(app, TenancyFixture.HostOf(_h.Code), _h.Token(BaselineSeed.SystemUserId));
        using var response = await client.PostAsync(Route, null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before + 1, await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'aud.tamper_attempt' and severity = 'critical'", Ct));
    }

    [Fact]
    public async Task FR_KRN_AUD_03_changing_one_byte_of_a_row_or_removing_a_row_is_found_by_the_verifier()
    {
        var code = await _h.ProvisionAsync("av", Ct);
        var admin = await _h.UserAsync(Ct, code, "tenant_admin");
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var created = await SendAsync(HttpMethod.Post, admin, "/api/org/org-unit-types", new { code = "aud" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "x" + i } }, code);
            ids.Add(created.Json.GetProperty("id").GetGuid());
        }

        var today = AccessHarness.Day(_h.Clock.UtcNow);
        var intact = await SendAsync(HttpMethod.Post, admin, "/api/aud/verify", new { from = today.AddDays(-1), to = today }, code);
        Assert.Equal(HttpStatusCode.OK, intact.Status);
        Assert.True(intact.Json.GetProperty("intact").GetBoolean(), intact.Body);
        Assert.True(intact.Json.GetProperty("rowsChecked").GetInt64() >= 4);

        // The database owner, with the append-only triggers switched off, changes one byte of one row's values.
        var target = await RowIdAsync(code, ids[2]);
        await OwnerExecAsync(code, "alter table aud.change_log disable trigger user",
            "update aud.change_log set changes = jsonb_set(changes, '{0,after}', '\"y0\"') where id = '" + target + "'",
            "alter table aud.change_log enable trigger user");
        var broken = await SendAsync(HttpMethod.Post, admin, "/api/aud/verify", new { from = today.AddDays(-1), to = today }, code);
        Assert.False(broken.Json.GetProperty("intact").GetBoolean());
        Assert.Equal(target, broken.Json.GetProperty("brokenRowId").GetGuid());
        Assert.Equal(ChainVerifier.RowChanged, broken.Json.GetProperty("problem").GetString());

        // Removing a row breaks the link of the row after it; and the failed checks are critical security events.
        await using var owner = await _h.OwnerAsync(code, Ct);
        Assert.True(await AccessHarness.ScalarAsync<long>(owner, "select count(*) from aud.security_events where event_type = 'aud.chain_broken' and severity = 'critical'", Ct) >= 1);
        TestContext.Current.SendDiagnosticMessage("Verifier report: " + broken.Body);
    }

    [Fact]
    public async Task FR_KRN_AUD_03_removing_a_row_breaks_the_link_and_the_verifier_names_the_next_row()
    {
        var code = await _h.ProvisionAsync("ar", Ct);
        var admin = await _h.UserAsync(Ct, code, "tenant_admin");
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var created = await SendAsync(HttpMethod.Post, admin, "/api/org/org-unit-types", new { code = "aud" + Guid.NewGuid().ToString("N")[..8], nameI18n = new { en = "x" + i } }, code);
            ids.Add(created.Json.GetProperty("id").GetGuid());
        }

        var removed = await RowIdAsync(code, ids[1]);
        var next = await RowIdAsync(code, ids[2]);
        await OwnerExecAsync(code, "alter table aud.change_log disable trigger user", "delete from aud.change_log where id = '" + removed + "'", "alter table aud.change_log enable trigger user");
        var today = AccessHarness.Day(_h.Clock.UtcNow);
        var broken = await SendAsync(HttpMethod.Post, admin, "/api/aud/verify", new { from = today.AddDays(-1), to = today }, code);
        Assert.False(broken.Json.GetProperty("intact").GetBoolean());
        Assert.Equal(next, broken.Json.GetProperty("brokenRowId").GetGuid());
        Assert.Equal(ChainVerifier.LinkBroken, broken.Json.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task FR_KRN_AUD_03_S107_P5_a_rewritten_chain_is_caught_by_the_control_plane_copy_of_the_anchor()
    {
        var code = await _h.ProvisionAsync("ap", Ct);
        var admin = await _h.UserAsync(Ct, code, "tenant_admin");
        var day = AccessHarness.Day(_h.Clock.UtcNow).AddDays(-3);
        var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        await using (var rw = await fx.OpenTenantAsync(code, TenantLogins.ReadWrite, Ct))
        {
            // Entries of a finished day (written as an earlier day's work would have been).
            var entries = Enumerable.Range(0, 3).Select(i => new ChangeEntry(Guid.CreateVersion7(at), at.AddMinutes(i), null, "system", "org.companies", Guid.CreateVersion7(at), null, "update",
                [new System.Text.Json.Nodes.JsonObject { ["field"] = "code", ["after"] = "v" + i }], null, null, null, "system", BaselineSeed.SystemUserId)).ToList();
            await AuditDb.AppendChangesAsync(rw, null, entries, Ct);
        }

        var actor = fx.Admin; // the fixture's platform admin (other tests count the platform users)
        var publisher = new AnchorPublisher(fx.Options, fx.Secrets, _h.Clock, fx.Migrations);
        var first = await publisher.PublishAsync(actor, code, Ct);
        Assert.True(first.Single().Copied >= 1);
        Assert.Empty(first.Single().Mismatches);

        // The customer's database owner rewrites the day consistently: new values, new hashes, new anchor.
        await OwnerExecAsync(code, "alter table aud.change_log disable trigger user", $$"""
            do $$
            declare r record; prev bytea := decode(repeat('00', 32), 'hex'); h bytea;
            begin
                for r in select * from aud.change_log where occurred_at >= '{{day:yyyy-MM-dd}}' and occurred_at < '{{day.AddDays(1):yyyy-MM-dd}}' order by occurred_at loop
                    h := sha256(prev || convert_to(aud.change_canonical(r.id, r.occurred_at, r.actor_user_id, r.actor_kind, r.entity_type, r.entity_id, r.company_id, r.action,
                         '[{"field":"code","after":"forged"}]'::jsonb, r.subject_person_id, r.correlation_id, r.ip_address, r.source, r.created_at, r.created_by), 'UTF8'));
                    update aud.change_log set changes = '[{"field":"code","after":"forged"}]'::jsonb, prev_hash = prev, row_hash = h where id = r.id and occurred_at = r.occurred_at;
                    prev := h;
                end loop;
                update aud.chain_anchors set last_hash = prev where day = '{{day:yyyy-MM-dd}}';
            end $$
            """, "alter table aud.change_log enable trigger user");

        var report = await SendAsync(HttpMethod.Post, admin, "/api/aud/verify", new { from = day, to = day }, code);
        Assert.False(report.Json.GetProperty("intact").GetBoolean(), report.Body);
        Assert.Equal(ChainVerifier.ControlPlaneMismatch, report.Json.GetProperty("problem").GetString());
        Assert.True(report.Json.GetProperty("controlPlaneCompared").GetBoolean());

        var again = await publisher.PublishAsync(actor, code, Ct);
        Assert.Equal([day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)], again.Single().Mismatches);
        await using var owner = await _h.OwnerAsync(code, Ct);
        Assert.Equal(1L, await AccessHarness.ScalarAsync<long>(owner, "select count(*) from aud.security_events where event_type = $1 and severity = 'critical'", Ct, AnchorPublisher.MismatchEvent));
    }

    [Fact]
    public async Task FR_KRN_AUD_03_NFR_07_twenty_concurrent_writers_produce_one_unbroken_chain_and_the_cost_per_write_is_measured()
    {
        var code = await _h.ProvisionAsync("ac", Ct);
        const int Writers = 20;
        const int PerWriter = 25;
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Writers).Select(async w =>
        {
            await using var rw = await fx.OpenTenantAsync(code, TenantLogins.ReadWrite, Ct);
            for (var i = 0; i < PerWriter; i++)
            {
                await using var tx = await rw.BeginTransactionAsync(Ct);
                var now = DateTimeOffset.UtcNow;
                await AuditDb.AppendChangesAsync(rw, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, null, "system", "org.companies", Guid.CreateVersion7(now), null, "update",
                    [new System.Text.Json.Nodes.JsonObject { ["field"] = "writer", ["after"] = w }], null, null, null, "system", BaselineSeed.SystemUserId)], Ct);
                await tx.CommitAsync(Ct);
            }
        }));
        stopwatch.Stop();

        await using var verify = await fx.OpenTenantAsync(code, TenantLogins.ReadWrite, Ct);
        var today = AccessHarness.Day(DateTimeOffset.UtcNow);
        var result = await ChainVerifier.VerifyAsync(verify, null, today.AddDays(-1), today.AddDays(1), markVerified: false, _h.Clock, Ct);
        Assert.True(result.Intact, string.Join(Environment.NewLine, result.Findings));
        Assert.Equal(Writers * PerWriter, result.RowsChecked);

        // Cost of one audited write on its own (one entry per transaction, no contention), for the slice report (NFR-07).
        var single = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            var now = DateTimeOffset.UtcNow;
            await AuditDb.AppendChangesAsync(verify, null, [new ChangeEntry(Guid.CreateVersion7(now), now, null, "system", "org.companies", Guid.CreateVersion7(now), null, "update",
                [new System.Text.Json.Nodes.JsonObject { ["field"] = "n", ["after"] = i }], null, null, null, "system", BaselineSeed.SystemUserId)], Ct);
        }

        single.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"NFR-07 audit overhead: {Writers * PerWriter} concurrent writes in {stopwatch.ElapsedMilliseconds} ms ({stopwatch.Elapsed.TotalMilliseconds / (Writers * PerWriter):0.00} ms each with 20 writers); single writer {single.Elapsed.TotalMilliseconds / 200:0.00} ms per audited write.");
    }

    // ------------------------------------------------------------------ per-person keys (build item 3)

    [Fact]
    public async Task Spec03_R21_HRM_PRV_04_personal_values_are_unreadable_raw_readable_in_history_and_erased_after_the_key_is_destroyed()
    {
        // Slice S1.20a: persons are written by the HR person service (generated writes closed, ruling Q1) and religion is refused
        // until a pack asks for it, so the second personal value checked is a relation's phone, written in the same transaction.
        var role = await _h.RoleAsync(Ct, ["hrm.persons.read", "hrm.persons.create", "hrm.persons.update", "hrm.person_relations.create", "aud.history.view"],
            [("hrm.persons", "all"), ("hrm.person_relations", "all")]);
        var user = await _h.UserAsync(Ct);
        await _h.AssignAsync(user, role, null, _h.Today.AddDays(-1), null, Ct);
        var created = await SendAsync(HttpMethod.Post, user, "/api/hrm/persons", new
        {
            firstName = "Ayesha", gender = "female", dateOfBirth = "1991-07-23",
            relations = new[] { new { relation = "parent", fullName = "Rashid", phone = "0321-7654321" } },
        });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var person = created.Json.GetProperty("id").GetGuid();

        var raw = await _h.ScalarAsync<string>("select changes::text from aud.change_log where entity_type = 'hrm.persons' and entity_id = $1", Ct, person);
        Assert.DoesNotContain("1991-07-23", raw, StringComparison.Ordinal);
        Assert.Contains("Ayesha", raw, StringComparison.Ordinal); // names stay readable (Q5)
        var relationRaw = await _h.ScalarAsync<string>("select changes::text from aud.change_log where entity_type = 'hrm.person_relations' and entity_id = (select id from hrm.person_relations where person_id = $1)", Ct, person);
        Assert.DoesNotContain("0321-7654321", relationRaw, StringComparison.Ordinal);
        Assert.Contains("Rashid", relationRaw, StringComparison.Ordinal);
        Assert.Equal(person, await _h.ScalarAsync<Guid>("select subject_person_id from aud.change_log where entity_type = 'hrm.person_relations' and entity_id = (select id from hrm.person_relations where person_id = $1)", Ct, person));
        Assert.Equal(person, await _h.ScalarAsync<Guid>("select subject_person_id from aud.change_log where entity_type = 'hrm.persons' and entity_id = $1", Ct, person));

        var accessBefore = await AccessRowsAsync(user, "hrm.persons");
        var history = await SendAsync(HttpMethod.Get, user, $"/api/aud/history/hrm.persons/{person}", null);
        Assert.Equal(HttpStatusCode.OK, history.Status);
        var dob = Field(history.Json[0], "date_of_birth");
        Assert.Equal("1991-07-23", dob.GetProperty("after").GetString());
        Assert.Equal(accessBefore + 1, await AccessRowsAsync(user, "hrm.persons")); // viewing decrypted personal values is logged (Q6)

        // Erasure destroys the person's key: values read as erased and the chain still verifies.
        var services = _h.App.Services;
        var tenant = await services.GetRequiredService<ITenantDirectory>().FindByHostAsync(TenancyFixture.HostOf(_h.Code), Ct);
        var source = await services.GetRequiredService<ITenantDataSourceFactory>().GetAsync(tenant!, Ct);
        Assert.True(await new SubjectKeyService(new FixedTenant(source), _h.Clock, new SystemAuditContext()).DestroyAsync(person, "test erasure", Ct));
        var erased = await SendAsync(HttpMethod.Get, user, $"/api/aud/history/hrm.persons/{person}", null);
        var erasedDob = Field(erased.Json[0], "date_of_birth");
        Assert.True(erasedDob.GetProperty("erased").GetBoolean());
        Assert.False(erasedDob.TryGetProperty("after", out _));
        Assert.Equal("Ayesha", Field(erased.Json[0], "first_name").GetProperty("after").GetString());

        var tenantAdmin = await _h.UserAsync(Ct, null, "tenant_admin");
        var verify = await SendAsync(HttpMethod.Post, tenantAdmin, "/api/aud/verify", new { from = _h.Today.AddDays(-1), to = _h.Today });
        Assert.True(verify.Json.GetProperty("intact").GetBoolean(), verify.Body);
    }

    // ------------------------------------------------------------------ access log (build item 4)

    [Fact]
    public async Task FR_KRN_AUD_02_GHR_02_HRM_PRV_05_a_salary_read_by_payroll_writes_one_access_row_and_a_masked_read_writes_none()
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        var result = await _h.RowAsync("hrm.pay_results", company, null, Ct);
        var id = (Guid)result["id"]!;
        var payroll = await _h.UserAsync(Ct, null, "payroll_officer");
        var masked = await _h.RoleAsync(Ct, ["hrm.pay_results.read"], [("hrm.pay_results", "all")], [("hrm.pay_results", "class:salary", "masked")]);
        var maskedUser = await _h.UserAsync(Ct);
        await _h.AssignAsync(maskedUser, masked, null, _h.Today.AddDays(-1), null, Ct);

        var read = await SendAsync(HttpMethod.Get, payroll, $"/api/hrm/pay-results/{id}", null);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        var rows = await _h.ScalarAsync<long>("select count(*) from aud.access_log where actor_user_id = $1 and entity_type = 'hrm.pay_results' and entity_id = $2 and action = 'view' and 'gross' = any(fields) and correlation_id = $3", Ct, payroll, id, read.Correlation!.Value);
        Assert.Equal(1L, rows);

        var maskedRead = await SendAsync(HttpMethod.Get, maskedUser, $"/api/hrm/pay-results/{id}", null);
        Assert.Equal(HttpStatusCode.OK, maskedRead.Status);
        Assert.StartsWith("****", maskedRead.Json.GetProperty("gross").GetString(), StringComparison.Ordinal);
        Assert.Equal(0L, await _h.ScalarAsync<long>("select count(*) from aud.access_log where actor_user_id = $1", Ct, maskedUser));
    }

    // ------------------------------------------------------------------ security events (build item 5)

    [Fact]
    public async Task FR_KRN_AUD_02_ADM_USR_04_role_and_setting_changes_and_sign_ins_are_recorded_with_the_right_actor()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var roleCode = "audrole" + Guid.NewGuid().ToString("N")[..8];
        var role = await SendAsync(HttpMethod.Post, admin, "/api/iam/roles", new { code = roleCode, name = new { en = "Audit role" } });
        Assert.Equal(HttpStatusCode.Created, role.Status);
        var roleId = role.Json.GetProperty("id").GetGuid();
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'iam.roles' and entity_id = $1 and action = 'create' and actor_user_id = $2", Ct, roleId, admin));
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'iam.role_created' and actor = $1 and subject = $2", Ct, admin.ToString(), "iam.role:" + roleId));

        // An iam.* setting (KRN-SET-04): the change log keeps the value before and after (own customer: the shared one's
        // settings are counted by the access tests).
        var settingsCode = await _h.ProvisionAsync("as", Ct);
        var settingsAdmin = await _h.UserAsync(Ct, settingsCode, "tenant_admin");
        var setting = await SendAsync(HttpMethod.Put, settingsAdmin, "/api/iam/settings/iam.session.idle_minutes", new { value = 25, reason = "audit test" }, settingsCode);
        Assert.True(setting.Status is HttpStatusCode.OK or HttpStatusCode.Accepted, setting.Body);
        await using (var settingsDb = await _h.OwnerAsync(settingsCode, Ct))
        {
            Assert.Equal(1L, await AccessHarness.ScalarAsync<long>(settingsDb, """select count(*) from aud.change_log where entity_type = 'cfg.setting_values' and actor_user_id = $1 and changes @> '[{"field":"value","after":25}]'""", Ct, settingsAdmin));
        }

        // Sign-in success and failure are security events (Q7), next to iam.login_attempts.
        var (userId, username) = await ActiveUserAsync();
        var bad = await AnonymousPostAsync("/api/iam/auth/sign-in", new { login = username, password = "wrong-password-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.Status);
        var good = await AnonymousPostAsync("/api/iam/auth/sign-in", new { login = username, password = IdentityHarness.Password });
        Assert.Equal(HttpStatusCode.OK, good.Status);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'iam.sign_in_failed' and subject = $1", Ct, username));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'iam.sign_in_succeeded' and actor = $1", Ct, userId.ToString()) >= 1);
        await _h.AssignAsync(userId, await _h.RoleIdAsync("business_user", Ct), null, _h.Today.AddDays(-1), null, Ct); // iam.self.manage
        var mine = await SendAsync(HttpMethod.Get, userId, "/api/aud/my-sign-ins", null);
        Assert.Equal(HttpStatusCode.OK, mine.Status);
        Assert.Contains(mine.Json.GetProperty("items").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "iam.sign_in_failed");
    }

    [Fact]
    public async Task FR_KRN_AUD_02_S107_Q7_a_lockout_is_a_warning_event()
    {
        var (userId, username) = await ActiveUserAsync();
        for (var i = 0; i < 5; i++)
        {
            await AnonymousPostAsync("/api/iam/auth/sign-in", new { login = username, password = "wrong-password-" + i });
        }

        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'iam.user_locked' and severity = 'warning' and subject = $1", Ct, "iam.user:" + userId));
        Assert.True(await _h.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'iam.users' and entity_id = $1 and action = 'status'", Ct, userId) >= 1);
    }

    // ------------------------------------------------------------------ search, history, export (build item 6)

    [Fact]
    public async Task FR_KRN_AUD_04_search_by_user_entity_record_date_and_action_returns_exactly_the_expected_rows()
    {
        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var other = await _h.UserAsync(Ct, null, "tenant_admin");
        var code = "aud" + Guid.NewGuid().ToString("N")[..8];
        var id = (await SendAsync(HttpMethod.Post, admin, "/api/org/org-unit-types", new { code, nameI18n = new { en = "a" } })).Json.GetProperty("id").GetGuid();
        await SendAsync(HttpMethod.Put, other, $"/api/org/org-unit-types/{id}", new { code, nameI18n = new { en = "b" }, rowVersion = 1 });
        await SendAsync(HttpMethod.Put, other, $"/api/org/org-unit-types/{id}", new { code, nameI18n = new { en = "c" }, rowVersion = 2 });

        async Task<List<JsonElement>> Search(string query) =>
            (await SendAsync(HttpMethod.Get, admin, "/api/aud/changes?" + query, null)).Json.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(3, (await Search($"entityType=org.org_unit_types&entityId={id}")).Count);
        Assert.Equal(2, (await Search($"entityId={id}&action=update")).Count);
        Assert.Single(await Search($"entityId={id}&userId={admin}"));
        Assert.Equal(2, (await Search($"entityId={id}&userId={other}")).Count);
        // Date range around the entries' own times (P2 may move an entry forward past a head written by another test).
        var times = (await Search($"entityId={id}")).Select(e => e.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        var from = Uri.EscapeDataString(times.Min().AddSeconds(-1).ToString("O"));
        var to = Uri.EscapeDataString(times.Max().AddSeconds(1).ToString("O"));
        Assert.Equal(3, (await Search($"entityId={id}&from={from}&to={to}")).Count);
        var newest = Uri.EscapeDataString(times.Max().ToString("O"));
        Assert.Equal(2, (await Search($"entityId={id}&to={newest}")).Count); // "to" is exclusive
        var past = Uri.EscapeDataString(times.Min().AddDays(-3).ToString("O"));
        Assert.Empty(await Search($"entityId={id}&to={past}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, admin, "/api/aud/changes?action=drop", null)).Status);

        // Paging: one entry per page, newest first, three pages.
        var page1 = (await SendAsync(HttpMethod.Get, admin, $"/api/aud/changes?entityId={id}&limit=1", null)).Json;
        Assert.Equal("update", page1.GetProperty("items")[0].GetProperty("action").GetString());
        var page2 = (await SendAsync(HttpMethod.Get, admin, $"/api/aud/changes?entityId={id}&limit=1&cursor={page1.GetProperty("next").GetString()}", null)).Json;
        var page3 = (await SendAsync(HttpMethod.Get, admin, $"/api/aud/changes?entityId={id}&limit=1&cursor={page2.GetProperty("next").GetString()}", null)).Json;
        Assert.Equal("create", page3.GetProperty("items")[0].GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, page3.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task FR_KRN_AUD_04_export_needs_aud_export_and_is_written_to_the_access_log()
    {
        var viewer = await _h.UserAsync(Ct);
        await _h.AssignAsync(viewer, await _h.RoleAsync(Ct, ["aud.changes.view"]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, viewer, "/api/aud/export", null)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, viewer, "/api/aud/changes?limit=1", null)).Status);

        var admin = await _h.UserAsync(Ct, null, "tenant_admin");
        var code = "aud" + Guid.NewGuid().ToString("N")[..8];
        var id = (await SendAsync(HttpMethod.Post, admin, "/api/org/org-unit-types", new { code, nameI18n = new { en = "a" } })).Json.GetProperty("id").GetGuid();
        var export = await SendAsync(HttpMethod.Get, admin, $"/api/aud/export?entityId={id}", null);
        Assert.Equal(HttpStatusCode.OK, export.Status);
        Assert.StartsWith("occurred_at,actor_user_id", export.Body, StringComparison.Ordinal);
        Assert.Contains(code, export.Body, StringComparison.Ordinal);
        Assert.Equal(1L, await _h.ScalarAsync<long>("select count(*) from aud.access_log where actor_user_id = $1 and action = 'export' and entity_type = 'aud.change_log' and row_count = 1", Ct, admin));
    }

    [Fact]
    public async Task FR_KRN_AUD_07_history_shows_values_through_the_viewers_field_security()
    {
        var company = await _h.DefaultCompanyAsync(Ct);
        // S1.35a closed the generated writes of pay results (only the payroll engine writes them); another salary-class record with a generated update serves.
        var result = await _h.RowAsync("hrm.previous_employer_income", company, null, Ct);
        var id = (Guid)result["id"]!;
        var editorRole = await _h.RoleAsync(Ct, ["hrm.previous_employer_income.read", "hrm.previous_employer_income.update"], [("hrm.previous_employer_income", "all")], [("hrm.previous_employer_income", "class:salary", "editable")]);
        var editor = await _h.UserAsync(Ct);
        await _h.AssignAsync(editor, editorRole, null, _h.Today.AddDays(-1), null, Ct);
        var current = await SendAsync(HttpMethod.Get, editor, $"/api/hrm/previous-employer-income/{id}", null);
        var body = System.Text.Json.Nodes.JsonNode.Parse(current.Body)!.AsObject();
        var gross = body["taxableIncome"]!.GetValue<decimal>();
        body["taxableIncome"] = gross + 1000m;
        var put = await SendAsync(HttpMethod.Put, editor, $"/api/hrm/previous-employer-income/{id}", body.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, put.Status);

        // The salary value is stored encrypted with the customer key (not readable in the raw table).
        var raw = await _h.ScalarAsync<string>("select changes::text from aud.change_log where entity_type = 'hrm.previous_employer_income' and entity_id = $1 and action = 'update'", Ct, id);
        Assert.Contains("\"customer\"", raw, StringComparison.Ordinal);

        var payroll = await _h.UserAsync(Ct, null, "payroll_officer");
        var seen = await SendAsync(HttpMethod.Get, payroll, $"/api/aud/history/hrm.previous_employer_income/{id}", null);
        Assert.Equal(HttpStatusCode.OK, seen.Status);
        Assert.Equal(gross + 1000m, Field(seen.Json[0], "taxable_income").GetProperty("after").GetDecimal());

        var noSalaryRole = await _h.RoleAsync(Ct, ["hrm.previous_employer_income.read", "aud.history.view"], [("hrm.previous_employer_income", "all")]);
        var manager = await _h.UserAsync(Ct);
        await _h.AssignAsync(manager, noSalaryRole, null, _h.Today.AddDays(-1), null, Ct);
        var hidden = await SendAsync(HttpMethod.Get, manager, $"/api/aud/history/hrm.previous_employer_income/{id}", null);
        Assert.Equal(HttpStatusCode.OK, hidden.Status);
        var grossChange = Field(hidden.Json[0], "taxable_income");
        Assert.True(grossChange.GetProperty("hidden").GetBoolean());
        Assert.False(grossChange.TryGetProperty("after", out _));
        Assert.DoesNotContain((gross + 1000m).ToString(System.Globalization.CultureInfo.InvariantCulture), hidden.Body, StringComparison.Ordinal);

        // Without read access to the record there is no history at all.
        var outsider = await _h.UserAsync(Ct);
        await _h.AssignAsync(outsider, await _h.RoleAsync(Ct, ["aud.history.view"]), null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, outsider, $"/api/aud/history/hrm.previous_employer_income/{id}", null)).Status);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Sent(HttpStatusCode Status, string Body, Guid? Correlation)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    private sealed record Entry(string Action, Guid? Actor, string ActorKind, string? Source, Guid? Correlation, JsonElement Changes, Guid? Company);

    private async Task<Sent> SendAsync(HttpMethod method, Guid user, string path, object? body, string? code = null)
    {
        using var client = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(code ?? _h.Code), _h.Token(user, code));
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = body is string json ? new StringContent(json, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body, options: JsonSerializerOptions.Web);
        }

        using var response = await client.SendAsync(request, Ct);
        Guid? correlation = response.Headers.TryGetValues("X-Correlation-Id", out var values) && Guid.TryParse(values.First(), out var g) ? g : null;
        return new Sent(response.StatusCode, await response.Content.ReadAsStringAsync(Ct), correlation);
    }

    private async Task<Answer> AnonymousPostAsync(string path, object body)
    {
        using var client = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(_h.Code));
        using var response = await client.PostAsJsonAsync(path, body, Ct);
        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>An active user with a password (invited, then activated through the API).</summary>
    private async Task<(Guid UserId, string Username)> ActiveUserAsync()
    {
        var username = "aud" + Guid.NewGuid().ToString("N")[..10];
        var id = Guid.CreateVersion7();
        string token;
        await using (var owner = await _h.OwnerAsync(null, Ct))
        {
            await AccessHarness.ExecAsync(owner, """
                insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $2 || '@audit.test', $2, 'en', 'Asia/Karachi', false, 'invited', now(), $3, now(), $3)
                """, Ct, id, username, BaselineSeed.SystemUserId);
            token = (await Erp.Kernel.Infrastructure.Identity.ActivationTokens.IssueAsync(owner, id, BaselineSeed.SystemUserId, _h.Clock.UtcNow, Ct))!.Token;
        }

        var activated = await AnonymousPostAsync("/api/iam/auth/activate", new { token, newPassword = IdentityHarness.Password });
        Assert.Equal(HttpStatusCode.OK, activated.Status);
        return (id, username);
    }

    private async Task<List<Entry>> EntriesAsync(string table, Guid id)
    {
        await using var c = await _h.OwnerAsync(null, Ct);
        await using var command = new NpgsqlCommand("select action, actor_user_id, actor_kind, source, correlation_id, changes::text, company_id from aud.change_log where entity_type = $1 and entity_id = $2 order by occurred_at", c);
        command.Parameters.Add(new NpgsqlParameter { Value = table });
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        var list = new List<Entry>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            list.Add(new Entry(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), JsonDocument.Parse(reader.GetString(5)).RootElement.Clone(), reader.IsDBNull(6) ? null : reader.GetGuid(6)));
        }

        return list;
    }

    private async Task<Guid> RowIdAsync(string code, Guid entityId)
    {
        await using var c = await _h.OwnerAsync(code, Ct);
        return await AccessHarness.ScalarAsync<Guid>(c, "select id from aud.change_log where entity_id = $1 order by occurred_at limit 1", Ct, entityId);
    }

    private async Task OwnerExecAsync(string code, params string[] statements)
    {
        await using var c = await _h.OwnerAsync(code, Ct);
        foreach (var sql in statements)
        {
            await AccessHarness.ExecAsync(c, sql, Ct);
        }
    }

    private async Task<long> AccessRowsAsync(Guid user, string entityType) =>
        await _h.ScalarAsync<long>("select count(*) from aud.access_log where actor_user_id = $1 and entity_type = $2", Ct, user, entityType);

    private static JsonElement Field(JsonElement item, string field) =>
        item.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("field").GetString() == field);

    private sealed class FixedTenant(System.Data.Common.DbDataSource source) : Erp.BuildingBlocks.Application.Tenancy.ITenantConnectionProvider
    {
        public System.Data.Common.DbDataSource DataSource => source;
    }
}
