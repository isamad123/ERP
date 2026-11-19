using Erp.IntegrationTests.Infrastructure;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy;

/// <summary>
/// R-028: provisioning creates the server-wide group roles (CR-004). On a new server two provisionings can create the
/// same role at the same moment; neither may fail. The tests use roles of their own that no server has yet, so they run
/// the real race on every build, and drop them afterwards.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GroupRoleRaceTests(PostgresFixture fx)
{
    private const int Sessions = 16;

    [Fact]
    public async Task FR_KRN_TEN_02_R028_concurrent_role_creation_on_a_new_server_never_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var roles = NewRoleNames();
        try
        {
            using var start = new SemaphoreSlim(0, Sessions);
            var connections = new List<NpgsqlConnection>();
            try
            {
                for (var i = 0; i < Sessions; i++)
                {
                    connections.Add(await fx.OpenConnectionAsync(ct));
                }

                var tasks = connections.Select(async c =>
                {
                    await start.WaitAsync(ct);
                    await ClusterAdministrator.EnsureNoLoginRolesAsync(c, roles, ct);
                }).ToList();
                start.Release(Sessions);
                await Task.WhenAll(tasks);
            }
            finally
            {
                foreach (var c in connections)
                {
                    await c.DisposeAsync();
                }
            }

            Assert.Equal(roles.Length, await CountRolesAsync(roles, ct));
        }
        finally
        {
            await DropRolesAsync(roles, ct);
        }
    }

    /// <summary>
    /// The exact collision, without relying on timing (R-028 review finding 2): another session has created the role in
    /// a transaction that is still open, so ours waits on it. When the other session commits, ours must carry on
    /// (the duplicate is ignored); when it rolls back, ours must create the role itself.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FR_KRN_TEN_02_R028_a_role_created_by_another_open_transaction_is_waited_for_and_never_fails(bool otherCommits)
    {
        var ct = TestContext.Current.CancellationToken;
        var role = NewRoleNames()[0];
        try
        {
            await using var other = await fx.OpenConnectionAsync(ct);
            await using var ours = await fx.OpenConnectionAsync(ct);
            var ourPid = ours.ProcessID;
            await using var tx = await other.BeginTransactionAsync(ct);
#pragma warning disable CA2100 // the name comes from NewRoleNames (letters, digits and underscores only)
            await using (var create = new NpgsqlCommand($"create role \"{role}\" nologin", other, tx))
#pragma warning restore CA2100
            {
                await create.ExecuteNonQueryAsync(ct);
            }

            var ensure = ClusterAdministrator.EnsureNoLoginRolesAsync(ours, [role], ct);
            await WaitUntilBlockedAsync(ourPid, ct);
            Assert.False(ensure.IsCompleted);

            if (otherCommits)
            {
                await tx.CommitAsync(ct);
            }
            else
            {
                await tx.RollbackAsync(ct);
            }

            await ensure.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.Equal(1, await CountRolesAsync([role], ct));
        }
        finally
        {
            await DropRolesAsync([role], ct);
        }
    }

    [Fact]
    public async Task FR_KRN_TEN_02_R028_repeated_creation_is_harmless_and_the_roles_have_no_login()
    {
        var ct = TestContext.Current.CancellationToken;
        var roles = NewRoleNames();
        try
        {
            await using var c = await fx.OpenConnectionAsync(ct);
            await ClusterAdministrator.EnsureNoLoginRolesAsync(c, roles, ct);
            await ClusterAdministrator.EnsureNoLoginRolesAsync(c, roles, ct);
            Assert.Equal(roles.Length, await CountRolesAsync(roles, ct));

            // Created without login, as the CR-004 group roles are.
            await using var cmd = new NpgsqlCommand("select count(*) from pg_roles where rolname = any($1) and not rolcanlogin", c);
            cmd.Parameters.Add(new NpgsqlParameter { Value = roles });
            Assert.Equal((long)roles.Length, (long)(await cmd.ExecuteScalarAsync(ct))!);
        }
        finally
        {
            await DropRolesAsync(roles, ct);
        }
    }

    /// <summary>R-028 review finding 3: a same-named role with powers is refused, never silently reused.</summary>
    [Theory]
    [InlineData("login")]
    [InlineData("createdb")]
    [InlineData("member")]
    public async Task FR_KRN_TEN_02_R028_an_existing_role_with_login_powers_or_memberships_is_refused(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var roles = NewRoleNames();
        try
        {
            await using var c = await fx.OpenConnectionAsync(ct);
#pragma warning disable CA2100 // the names come from NewRoleNames (letters, digits and underscores only)
            var setup = kind switch
            {
                "login" => $"create role \"{roles[0]}\" login",
                "createdb" => $"create role \"{roles[0]}\" nologin createdb",
                _ => $"create role \"{roles[1]}\" nologin; create role \"{roles[0]}\" nologin in role \"{roles[1]}\"",
            };
            await using (var cmd = new NpgsqlCommand(setup, c))
#pragma warning restore CA2100
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ClusterAdministrator.EnsureNoLoginRolesAsync(c, [roles[0]], ct));
            Assert.Contains(roles[0], error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DropRolesAsync(roles, ct);
        }
    }

    [Theory]
    [InlineData("App_RW")]
    [InlineData("x$do$ begin perform 1; end $do$")]
    [InlineData("a\"b")]
    [InlineData("")]
    public async Task FR_KRN_TEN_02_R028_a_role_name_that_is_not_a_plain_lower_case_identifier_is_refused(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await fx.OpenConnectionAsync(ct);
        await Assert.ThrowsAsync<ArgumentException>(() => ClusterAdministrator.EnsureNoLoginRolesAsync(c, [name], ct));
    }

    private static string[] NewRoleNames()
    {
        var stem = "erp_test_r028_" + Guid.NewGuid().ToString("N")[..12];
        return [stem + "_a", stem + "_b", stem + "_c"];
    }

    /// <summary>Waits until the given session is waiting on a lock held by another session.</summary>
    private async Task WaitUntilBlockedAsync(int pid, CancellationToken ct)
    {
        await using var c = await fx.OpenConnectionAsync(ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var cmd = new NpgsqlCommand("select wait_event_type = 'Lock' from pg_stat_activity where pid = $1", c);
            cmd.Parameters.Add(new NpgsqlParameter { Value = pid });
            if (await cmd.ExecuteScalarAsync(ct) is true)
            {
                return;
            }

            await Task.Delay(20, ct);
        }

        Assert.Fail("The second session never waited on the first one's role.");
    }

    private async Task<long> CountRolesAsync(string[] roles, CancellationToken ct)
    {
        await using var c = await fx.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("select count(*) from pg_roles where rolname = any($1)", c);
        cmd.Parameters.Add(new NpgsqlParameter { Value = roles });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task DropRolesAsync(string[] roles, CancellationToken ct)
    {
        await using var c = await fx.OpenConnectionAsync(ct);
        foreach (var role in roles)
        {
#pragma warning disable CA2100 // the names come from NewRoleNames (letters, digits and underscores only)
            await using var cmd = new NpgsqlCommand($"drop role if exists \"{role}\"", c);
#pragma warning restore CA2100
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
