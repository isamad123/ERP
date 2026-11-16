using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;
using Erp.CodeGen.Dictionary;
using Erp.Host.Api;
using Erp.IntegrationTests.Infrastructure;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy;

/// <summary>
/// A real control plane, a real PostgreSQL cluster registered in it, and two customers provisioned end to end
/// (slice S1.03), served through the real API pipeline (<see cref="ApiHost"/>) on an in-process test server.
/// Permission checks, data scopes and field access are allow-all doubles unless a test asks for the real engine (S1.06).
/// </summary>
public sealed class TenancyFixture : IAsyncLifetime
{
    public const string Issuer = "https://id.erp.test";
    public const string Audience = "erp-api";

    private readonly PostgresFixture _server = new();
    private readonly List<string> _codes = [];
    private readonly ECDsa _tokenKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _entitlementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private ScratchDatabase? _controlPlane;
    private string? _folder;

    public string Suffix { get; } = Guid.NewGuid().ToString("N")[..6].Replace('-', 'x');

    public IClock Clock { get; } = new SystemClock();

    public ControlPlaneOptions Options { get; private set; } = null!;

    public EncryptedFileSecretStore Secrets { get; private set; } = null!;

    public MigrationSet Migrations { get; } = MigrationSet.FromRoot(MigratedDatabases.Root);

    public PlatformActor Admin { get; private set; } = null!;

    public string ClusterName => "it-" + Suffix;

    public string AdminConnectionString => _server.ConnectionString;

    public WebApplication App { get; private set; } = null!;

    public string CodeA => "a" + Suffix;

    public string CodeB => "b" + Suffix;

    public static string HostOf(string code) => code + ".erp.test";

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _server.InitializeAsync();
        _controlPlane = await ScratchDatabase.CreateAsync(_server.ConnectionString, "erp_it_cp_" + Suffix, ct);
        _folder = Directory.CreateTempSubdirectory("erp-s103-").FullName;
        Options = new ControlPlaneOptions { ConnectionString = _controlPlane.ConnectionString, HostCacheSeconds = 0, StatementTimeoutSeconds = 30 };
        Secrets = new EncryptedFileSecretStore(Path.Combine(_folder, "secrets.bin"), RandomNumberGenerator.GetBytes(32));
        await TokenSigningKeys.EnsureAsync(Secrets, "it-" + Suffix, ct); // as the migrator's `migrate` command does (S1.05)

        var admin = new ControlPlaneAdmin(Options, Secrets, Clock);
        Admin = await admin.BootstrapAsync(Migrations, "platform.admin@example.test", "Platform Admin", ct);
        var server = new NpgsqlConnectionStringBuilder(_server.ConnectionString);
        await admin.AddClusterAsync(Admin, ClusterName, "pk-1", server.Host!, server.Port, 50, new DatabaseCredential(server.Username!, server.Password!), ct);
        await admin.AddSigningKeyAsync(Admin, "it-" + Suffix, "entitlement", _entitlementKey.ExportSubjectPublicKeyInfo(), ct);

        await ProvisionAsync(CodeA, ct);
        await ProvisionAsync(CodeB, ct);

        App = await StartAppAsync(null, ct);
    }

    /// <summary>
    /// Starts the real API pipeline (<see cref="ApiHost"/>) on an in-process test server against this fixture's control
    /// plane. <paramref name="configure"/> may map extra endpoints before the host starts (the S1.04 leak self-test).
    /// The caller disposes the returned host, except <see cref="App"/>, which the fixture disposes.
    /// </summary>
    /// <remarks><paramref name="realAccess"/>: keep the real access engine (slice S1.06) instead of the allow-all doubles.</remarks>
    public async Task<WebApplication> StartAppAsync(Action<WebApplication>? configure, CancellationToken cancellationToken, Action<IServiceCollection>? extra = null, bool realAccess = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // The isolation suite makes the database refuse thousands of cross-customer writes on purpose (answered 409);
        // EF Core logs each refusal as an error, which floods the CI log and hides real test failures.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
        builder.Configuration["ConnectionStrings:ControlPlane"] = Options.ConnectionString;
        builder.Configuration["Tenancy:HostCacheSeconds"] = "0";
        builder.Configuration["Auth:Issuer"] = Issuer;
        builder.Configuration["Auth:Audience"] = Audience;
        builder.Configuration["Auth:SigningPublicKeys:0"] = _tokenKey.ExportSubjectPublicKeyInfoPem();
        var app = ApiHost.Build(builder, Secrets, services =>
        {
            if (!realAccess)
            {
                services.UseAllowAllAccess();
            }

            extra?.Invoke(services);
        });
        configure?.Invoke(app);
        await app.StartAsync(cancellationToken);
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        var ct = CancellationToken.None;
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await using (var connection = await _server.OpenConnectionAsync(ct))
        {
            foreach (var code in _codes)
            {
                await ExecAsync(connection, $"drop database if exists {TenantLogins.DatabaseName(code)} with (force)", ct);
                await ExecAsync(connection, $"drop database if exists {RestoreTarget(code)} with (force)", ct);
                foreach (var (kind, _) in TenantLogins.All)
                {
                    await ExecAsync(connection, $"drop role if exists {TenantLogins.LoginName(code, kind)}", ct);
                }
            }
        }

        if (_controlPlane is not null)
        {
            await _controlPlane.DropAsync(ct);
        }

        Secrets?.Dispose();
        _tokenKey.Dispose();
        _entitlementKey.Dispose();
        if (_folder is not null)
        {
            Directory.Delete(_folder, recursive: true);
        }

        await _server.DisposeAsync();
    }

    public static string RestoreTarget(string code) => "erp_r_" + code;

    public string TempFile(string name) => Path.Combine(_folder!, name);

    public TenantProvisioner Provisioner(MigrationSet? migrations = null) => new(Options, Secrets, Clock, migrations ?? Migrations);

    public ProvisioningRequest Request(string code, IReadOnlyList<string>? modules = null, string? entitlementTenant = null, ECDsa? signer = null)
    {
        var payload = new EntitlementPayload(entitlementTenant ?? code, 1, "business", new DateOnly(2026, 11, 1), null,
            new JsonObject { ["modules"] = new JsonArray([.. (modules ?? ["hrm"]).Select(m => (JsonNode)m)]), ["limits"] = new JsonObject { ["employees"] = 100 } });
        var pem = (signer ?? _entitlementKey).ExportECPrivateKeyPem();
        var signed = SignedEntitlement.Sign(payload, "it-" + Suffix, pem);
        return new ProvisioningRequest(code, "Customer " + code.ToUpperInvariant(), [HostOf(code)], "pk-1", "PK", "PKR", "sme", ClusterName,
            "Customer " + code.ToUpperInvariant() + " (Private) Limited", "admin@" + code + ".example.test", "First Admin", signed.ToFileJson());
    }

    /// <summary>Remembers the code so the database and logins are dropped afterwards, even if provisioning fails.</summary>
    public void Track(string code) => _codes.Add(code);

    public async Task<ProvisioningResult> ProvisionAsync(string code, CancellationToken cancellationToken, IProvisioningProgress? progress = null, MigrationSet? migrations = null)
    {
        Track(code);
        return await Provisioner(migrations).ProvisionAsync(Admin, Request(code), progress, cancellationToken);
    }

    public async Task<ResolvedTenant> TenantAsync(string code, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return (await TenantDirectory.FindByCodeAsync(connection, code, cancellationToken))!;
    }

    /// <summary>A connection to a customer database as one of its own logins.</summary>
    public async Task<NpgsqlConnection> OpenTenantAsync(string code, string loginKind, CancellationToken cancellationToken) =>
        await Provisioner().OpenTenantAsync(await TenantAsync(code, cancellationToken), loginKind, cancellationToken);

    public async Task<NpgsqlConnection> OpenControlPlaneAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>An HTTP client whose requests carry the customer's host name.</summary>
    public HttpClient Client(string host, string? token = null) => Client(App, host, token);

    /// <summary>An HTTP client of <paramref name="app"/> whose requests carry the customer's host name.</summary>
    public static HttpClient Client(WebApplication app, string host, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("http://" + host);
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    /// <summary>A token as the identity slice (S1.05) will issue it: signed, with the customer code and the user id.</summary>
    public string Token(string tenantCode, ECDsa? key = null, Guid? user = null, string? issuer = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim("sub", (user ?? BaselineSeed.SystemUserId).ToString()), new Claim("tenant", tenantCode)]),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key ?? _tokenKey), SecurityAlgorithms.EcdsaSha256),
        });
    }

    public static async Task<long> CountAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] values)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] values)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

[CollectionDefinition(Name)]
public sealed class TenancyGroup : ICollectionFixture<TenancyFixture>
{
    public const string Name = "Tenancy (S1.03)";
}
