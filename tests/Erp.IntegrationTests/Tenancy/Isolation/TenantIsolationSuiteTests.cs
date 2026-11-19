using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// S1.04 tenant-isolation suite (FR-KRN-TEN-03, FR-KRN-TEN-04, spec 03 section 10 invariant 10, D-47): every API endpoint
/// is called as customer B with customer A's ids, hosts and tokens crossed, and must refuse; A's database must stay
/// unchanged. Runs as its own CI job (<c>tenant-isolation</c>), selected by the trait below.
/// </summary>
[Collection(IsolationGroup.Name)]
[Trait("Suite", "TenantIsolation")]
public sealed class TenantIsolationSuiteTests(IsolationFixture fx)
{
    public static TheoryData<string> Endpoints => EndpointInventory.CustomerEndpoints();

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task FR_KRN_TEN_03_FR_KRN_TEN_04_Spec03_10_inv10_endpoint_never_reaches_another_customers_data(string endpoint)
    {
        var ct = TestContext.Current.CancellationToken;
        var failures = await new CrossTenantProbe(fx, fx.Tenancy.App).RunAsync(EndpointInventory.Find(endpoint), ct);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void FR_KRN_TEN_03_S104_every_endpoint_of_the_running_host_is_covered_by_the_suite()
    {
        // The theory rows come from the production composition; the host under test must map exactly the same endpoints.
        var live = EndpointInventory.Read(fx.Tenancy.App).Select(e => e.Key).ToList();
        Assert.Equal(EndpointInventory.All.Select(e => e.Key).ToList(), live);
        Assert.True(live.Count > 400, $"only {live.Count} endpoints discovered; discovery is broken");

        var problems = new List<string>();
        foreach (var endpoint in EndpointInventory.All)
        {
            if (EndpointInventory.TenantFree.Contains(endpoint.Key))
            {
                if (!endpoint.Anonymous)
                {
                    problems.Add($"{endpoint}: listed as tenant-free but needs a login; remove it from the list so it is probed.");
                }

                continue;
            }

            if (endpoint.Anonymous && !(EndpointInventory.AnonymousSignIn.Contains(endpoint.Key) && endpoint.Kind == EndpointKind.IdentityAnonymous))
            {
                problems.Add($"{endpoint}: answers without a login. Every customer endpoint needs authorization (or a reviewed entry in TenantFree or AnonymousSignIn).");
            }

            if (!endpoint.Anonymous && EndpointInventory.AnonymousSignIn.Contains(endpoint.Key))
            {
                problems.Add($"{endpoint}: listed as an anonymous sign-in step but needs a login; remove it from AnonymousSignIn.");
            }

            var identity = endpoint.Kind is EndpointKind.IdentityAnonymous or EndpointKind.IdentitySelf or EndpointKind.IdentityUserAction;
            if (endpoint.Kind == EndpointKind.Unclassified || (endpoint.Resource is null && !identity))
            {
                problems.Add($"{endpoint}: cannot be probed (no GeneratedEndpointMetadata or IdentityEndpointMetadata, or an unknown route shape).");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Equal(EndpointInventory.AnonymousSignIn.Count, EndpointInventory.All.Count(e => e.Kind == EndpointKind.IdentityAnonymous));
    }

    [Fact]
    public async Task FR_KRN_TEN_03_S104_no_customer_login_can_connect_to_another_customers_database_or_the_control_plane()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await fx.Tenancy.TenantAsync(fx.CodeA, ct);
        var b = await fx.Tenancy.TenantAsync(fx.CodeB, ct);
        var controlPlane = new NpgsqlConnectionStringBuilder(fx.Tenancy.Options.ConnectionString).Database!;
        var tried = 0;
        foreach (var (from, to) in new[] { (a, b), (b, a) })
        {
            foreach (var (kind, _) in TenantLogins.All)
            {
                var credential = DatabaseCredential.Parse((await fx.Tenancy.Secrets.GetAsync(TenantLogins.SecretRef(from.AppSecretRef, kind), ct))!);
                foreach (var database in new[] { to.DatabaseName, controlPlane })
                {
                    var builder = new NpgsqlConnectionStringBuilder { Host = to.ClusterHost, Port = to.ClusterPort, Database = database, Username = credential.Username, Password = credential.Password, Pooling = false };
                    await using var connection = new NpgsqlConnection(builder.ConnectionString);
                    var error = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync(ct));
                    Assert.True(error.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"{credential.Username} -> {database}: {error.SqlState} {error.MessageText}");
                    tried++;
                }
            }

            // Positive control: the same logins do open their own database.
            var own = DatabaseCredential.Parse((await fx.Tenancy.Secrets.GetAsync(TenantLogins.SecretRef(from.AppSecretRef, TenantLogins.ReadOnly), ct))!);
            var ownBuilder = new NpgsqlConnectionStringBuilder { Host = from.ClusterHost, Port = from.ClusterPort, Database = from.DatabaseName, Username = own.Username, Password = own.Password, Pooling = false };
            await using var mine = new NpgsqlConnection(ownBuilder.ConnectionString);
            await mine.OpenAsync(ct);
        }

        Assert.Equal(2 * TenantLogins.All.Count * 2, tried);
    }

    [Fact]
    public async Task FR_KRN_TEN_03_S104_the_suite_catches_an_endpoint_that_reads_without_the_customer_connection()
    {
        // A deliberately broken endpoint: it keeps the first customer's data source it ever sees and reads by id through
        // it for every later caller (a cached connection is a realistic way to get this wrong).
        var ct = TestContext.Current.CancellationToken;
        System.Data.Common.DbDataSource? cached = null;
        const string Route = "/api/test/leaky-companies/{id:guid}";
        await using var leaky = await fx.Tenancy.StartAppAsync(app => app.MapGet(Route, async (Guid id, HttpContext http) =>
            {
                cached ??= http.RequestServices.GetRequiredService<ITenantConnectionProvider>().DataSource;
                await using var command = cached.CreateCommand("select id, code from org.companies where id = $1");
                command.Parameters.Add(new NpgsqlParameter { Value = id });
                await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);
                return await reader.ReadAsync(http.RequestAborted) ? Results.Ok(new { id = reader.GetGuid(0), code = reader.GetString(1) }) : Results.NotFound();
            })
            .RequireAuthorization()
            .WithMetadata(new GeneratedEndpointMetadata("org.companies", "read", "org.companies.read")), ct);

        var endpoint = EndpointInventory.Read(leaky).Single(e => e.Pattern == Route);
        var failures = await new CrossTenantProbe(fx, leaky).RunAsync(endpoint, ct);

        Assert.Contains(failures, f => f.Contains("[B host + B token]: expected 404, got 200", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("LEAK", StringComparison.Ordinal));
        TestContext.Current.SendDiagnosticMessage("Leak self-test, probe output:" + Environment.NewLine + string.Join(Environment.NewLine, failures));

        // The same probe passes the correct endpoint for the same table.
        Assert.Empty(await new CrossTenantProbe(fx, fx.Tenancy.App).RunAsync(EndpointInventory.Find("GET /api/org/companies/{id:guid}"), ct));
    }

    [Fact]
    public async Task FR_KRN_TEN_03_S104_the_suite_catches_an_endpoint_that_keeps_the_customer_in_shared_state()
    {
        // S1.04 review finding 1. A deliberately broken endpoint: each request puts its customer's data source in state
        // shared by all requests, waits, then reads through that state. One request at a time it behaves; two customers at
        // once read through whichever came last.
        var ct = TestContext.Current.CancellationToken;
        System.Data.Common.DbDataSource? shared = null;
        const string Route = "/api/test/shared-state-companies/{id:guid}";
        await using var broken = await fx.Tenancy.StartAppAsync(app => app.MapGet(Route, async (Guid id, HttpContext http) =>
            {
                shared = http.RequestServices.GetRequiredService<ITenantConnectionProvider>().DataSource;
                await Task.Delay(100, http.RequestAborted);
                return await ReadCompanyAsync(shared, id, created: false, http.RequestAborted);
            })
            .RequireAuthorization()
            .WithMetadata(new GeneratedEndpointMetadata("org.companies", "read", "org.companies.read")), ct);

        var failures = await new CrossTenantProbe(fx, broken).RunAsync(EndpointInventory.Read(broken).Single(e => e.Pattern == Route), ct);

        Assert.Contains(failures, f => f.Contains("[A and B at the same time]", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, f => f.Contains("[B host + B token]", StringComparison.Ordinal)); // one at a time it passes
        TestContext.Current.SendDiagnosticMessage("Shared-state self-test, probe output:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task FR_KRN_TEN_03_S104_the_suite_catches_a_create_that_answers_with_the_other_customers_data()
    {
        // S1.04 review finding 2. A deliberately broken create: it writes nothing and answers 201 with the record last read by
        // id, through the first customer's data source it saw.
        var ct = TestContext.Current.CancellationToken;
        System.Data.Common.DbDataSource? first = null;
        Guid? lastRead = null;
        const string Base = "/api/test/echo-companies";
        await using var broken = await fx.Tenancy.StartAppAsync(app =>
            {
                app.MapGet(Base + "/{id:guid}", async (Guid id, HttpContext http) =>
                    {
                        var source = http.RequestServices.GetRequiredService<ITenantConnectionProvider>().DataSource;
                        first ??= source;
                        lastRead = id;
                        return await ReadCompanyAsync(source, id, created: false, http.RequestAborted);
                    })
                    .RequireAuthorization()
                    .WithMetadata(new GeneratedEndpointMetadata("org.companies", "read", "org.companies.read"));
                // Two parameters, so this maps as a handler whose IResult is written (an HttpContext-only lambda is a RequestDelegate and answers an empty 200).
                app.MapPost(Base, async (HttpContext http, CancellationToken aborted) => await ReadCompanyAsync(first!, lastRead!.Value, created: true, aborted))
                    .RequireAuthorization()
                    .WithMetadata(new GeneratedEndpointMetadata("org.companies", "create", "org.companies.create"));
            }, ct);

        var failures = await new CrossTenantProbe(fx, broken).RunAsync(EndpointInventory.Read(broken).Single(e => e.Pattern == Base && e.Method == "POST"), ct);

        Assert.Contains(failures, f => f.Contains("[B host + B token]: LEAK", StringComparison.Ordinal));
        TestContext.Current.SendDiagnosticMessage("Create-echo self-test, probe output:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task FR_KRN_TEN_03_S104_the_suite_catches_an_endpoint_that_writes_into_the_other_customers_database()
    {
        // S1.04 review finding 3. A deliberately broken update: it answers 404 as it should, but first writes through the
        // first customer's data source it saw. Only the fingerprint of A's database can catch it.
        var ct = TestContext.Current.CancellationToken;
        System.Data.Common.DbDataSource? first = null;
        const string Base = "/api/test/scribbling-companies";
        await using var broken = await fx.Tenancy.StartAppAsync(app =>
            {
                app.MapGet(Base + "/{id:guid}", async (Guid id, HttpContext http) =>
                    {
                        var source = http.RequestServices.GetRequiredService<ITenantConnectionProvider>().DataSource;
                        first ??= source;
                        return await ReadCompanyAsync(source, id, created: false, http.RequestAborted);
                    })
                    .RequireAuthorization()
                    .WithMetadata(new GeneratedEndpointMetadata("org.companies", "read", "org.companies.read"));
                app.MapPut(Base + "/{id:guid}", async (Guid id, HttpContext http) =>
                    {
                        await using var command = first!.CreateCommand("update org.companies set updated_at = updated_at + interval '1 millisecond' where id = $1");
                        command.Parameters.Add(new NpgsqlParameter { Value = id });
                        await command.ExecuteNonQueryAsync(http.RequestAborted);
                        return Results.NotFound();
                    })
                    .RequireAuthorization()
                    .WithMetadata(new GeneratedEndpointMetadata("org.companies", "update", "org.companies.update"));
            }, ct);

        var failures = await new CrossTenantProbe(fx, broken).RunAsync(EndpointInventory.Read(broken).Single(e => e.Pattern == Base + "/{id:guid}" && e.Method == "PUT"), ct);

        Assert.Contains(failures, f => f.Contains("customer A's database changed", StringComparison.Ordinal) && f.Contains("org.companies", StringComparison.Ordinal));
        TestContext.Current.SendDiagnosticMessage("Write self-test, probe output:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static async Task<IResult> ReadCompanyAsync(System.Data.Common.DbDataSource source, Guid id, bool created, CancellationToken ct)
    {
        await using var command = source.CreateCommand("select id, code, row_version from org.companies where id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Results.NotFound();
        }

        var row = new { id = reader.GetGuid(0), code = reader.GetString(1), rowVersion = reader.GetInt64(2) };
        return created ? Results.Created("/api/test/echo-companies/" + row.id, row) : Results.Ok(row);
    }
}
