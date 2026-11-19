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
    private byte[] _secretsKey = [];

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
        _secretsKey = RandomNumberGenerator.GetBytes(32);
        Secrets = new EncryptedFileSecretStore(Path.Combine(_folder, "secrets.bin"), _secretsKey);
        await TokenSigningKeys.EnsureAsync(Secrets, "it-" + Suffix, ct); // as the migrator's `migrate` command does (S1.05)

        var admin = new ControlPlaneAdmin(Options, Secrets, Clock);
        Admin = await admin.BootstrapAsync(Migrations, "platform.admin@example.test", "Platform Admin", ct);
        var server = new NpgsqlConnectionStringBuilder(_server.ConnectionString);
        // Capacity 120: the job tests of S1.11a give each test its own customer.
        await admin.AddClusterAsync(Admin, ClusterName, "pk-1", server.Host!, server.Port, 120, new DatabaseCredential(server.Username!, server.Password!), ct);
        await admin.AddSigningKeyAsync(Admin, "it-" + Suffix, "entitlement", _entitlementKey.ExportSubjectPublicKeyInfo(), ct);

        await ProvisionAsync(CodeA, ct);
        await ProvisionAsync(CodeB, ct);

        // The test workflow module (slice S1.10a): its document types and outcome handlers, so the isolation suite can probe
        // the workflow endpoints with real requests (the production host has no document types yet).
        App = await StartAppAsync(null, ct, services =>
        {
            Workflow.WorkflowRig.RegisterTestModule(services);
            Documents.DocumentTestServices.Register(services, Path.Combine(_folder, "files")); // the file store of the isolation suite (S1.11c1)
        });
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
        builder.Configuration["Settings:RefreshMilliseconds"] = "0"; // tests write settings with SQL and read them at once (S1.09a)
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
        var payload = new EntitlementPayload(entitlementTenant ?? code, 1, "business", new DateOnly(2026, 11, 1), null, Claims(modules ?? ["hrm"]));
        var pem = (signer ?? _entitlementKey).ExportECPrivateKeyPem();
        var signed = SignedEntitlement.Sign(payload, "it-" + Suffix, pem);
        return new ProvisioningRequest(code, "Customer " + code.ToUpperInvariant(), [HostOf(code)], "pk-1", "PK", "PKR", "sme", ClusterName,
            "Customer " + code.ToUpperInvariant() + " (Private) Limited", "admin@" + code + ".example.test", "First Admin", signed.ToFileJson());
    }

    /// <summary>
    /// Claims in the S1.09b format (ruling Q1): every HR feature key when the hrm module is licensed, no limits unless given
    /// (a missing limit means no limit).
    /// </summary>
    public static JsonObject Claims(IReadOnlyList<string> modules, JsonObject? limits = null, IReadOnlyList<string>? features = null) => new()
    {
        ["deployment"] = "saas",
        ["modules"] = new JsonArray([.. modules.Select(m => (JsonNode)m)]),
        ["features"] = new JsonArray([.. (features ?? (modules.Contains("hrm") ? ["platform.base", "hrm.core", "hrm.payroll", "hrm.performance", "hrm.learning"] : ["platform.base"])).Select(f => (JsonNode)f)]),
        ["packs"] = new JsonArray("pack.country.pk"),
        ["limits"] = limits ?? [],
    };

    /// <summary>Signs a payload with this fixture's registered entitlement key (or <paramref name="signer"/>), as the issuer would.</summary>
    public string Sign(EntitlementPayload payload, ECDsa? signer = null, string? keyId = null) =>
        SignedEntitlement.Sign(payload, keyId ?? "it-" + Suffix, (signer ?? _entitlementKey).ExportECPrivateKeyPem()).ToFileJson();

    public string EntitlementKeyId => "it-" + Suffix;

    /// <summary>
    /// Test shortcut: changes the stored claims of the customer's entitlement in force in the control plane (the source the
    /// API reads, S1.09b) at <paramref name="path"/>; a null value removes the entry. Returns the claims before the change.
    /// </summary>
    public async Task<string> SetClaimAsync(string code, string path, JsonNode? value, CancellationToken cancellationToken)
    {
        await using var cp = await OpenControlPlaneAsync(cancellationToken);
        var before = await ScalarTextAsync(cp, "select e.claims::text from cp.entitlements e join cp.tenants t on t.id = e.tenant_id where t.code = $1 and e.status not in ('superseded', 'scheduled')", cancellationToken, code);
        const string Where = "from cp.tenants t where t.id = e.tenant_id and t.code = $1 and e.status not in ('superseded', 'scheduled')";
        if (value is null)
        {
            await ExecAsync(cp, "update cp.entitlements e set claims = claims #- $2::text[] " + Where, cancellationToken, code, path.Split(','));
        }
        else
        {
            await ExecAsync(cp, "update cp.entitlements e set claims = jsonb_set(claims, $2::text[], $3::jsonb) " + Where, cancellationToken, code, path.Split(','), value.ToJsonString());
        }

        return before;
    }

    /// <summary>
    /// Test shortcut: moves the end date of the customer's entitlement in force so that today (Asia/Karachi) is
    /// <paramref name="daysAfterEnd"/> days after it (null: perpetual). With the default periods (30, 14, 30 days, ruling Q4):
    /// −29..0 Warning, 1..14 Grace, 15..44 Read-only, 45 on Locked. The state follows from the date (S1.09b).
    /// </summary>
    public async Task SetDaysAfterEndAsync(string code, int? daysAfterEnd, CancellationToken cancellationToken)
    {
        var today = Erp.Kernel.Domain.Entitlements.LicenceStates.Today(DateTimeOffset.UtcNow, Erp.Kernel.Domain.Entitlements.LicenceStates.Zone("Asia/Karachi"));
        await using var cp = await OpenControlPlaneAsync(cancellationToken);
        await ExecAsync(cp, """
            update cp.entitlements e set expires_at = $2, effective_from = least(e.effective_from, coalesce($2, e.effective_from))
            from cp.tenants t where t.id = e.tenant_id and t.code = $1 and e.status not in ('superseded', 'scheduled')
            """, cancellationToken, code, daysAfterEnd is { } d ? today.AddDays(-d) : DBNull.Value);
    }

    /// <summary>Puts back claims returned by <see cref="SetClaimAsync"/>.</summary>
    public async Task RestoreClaimsAsync(string code, string claims, CancellationToken cancellationToken)
    {
        await using var cp = await OpenControlPlaneAsync(cancellationToken);
        await ExecAsync(cp, "update cp.entitlements e set claims = $2::jsonb from cp.tenants t where t.id = e.tenant_id and t.code = $1 and e.status not in ('superseded', 'scheduled')", cancellationToken, code, claims);
    }

    public static async Task<string> ScalarTextAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] values)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Remembers the code so the database and logins are dropped afterwards, even if provisioning fails.</summary>
    public void Track(string code)
    {
        lock (_codes) // provisioning race tests (R-031) start runs from parallel tasks
        {
            _codes.Add(code);
        }
    }

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

    /// <summary>
    /// The settings a separately started API host process needs to serve this fixture's customers (slice S1.09a: a change
    /// made on one host is seen by another process): control plane, secret file and key, issuer, audience and the test
    /// token key. Settings left out keep their production defaults.
    /// </summary>
    public IReadOnlyDictionary<string, string> ApiProcessEnvironment() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ConnectionStrings__ControlPlane"] = Options.ConnectionString,
        ["ERP_SECRETS_FILE"] = Path.Combine(_folder!, "secrets.bin"),
        ["ERP_SECRETS_KEY"] = Convert.ToBase64String(_secretsKey),
        ["Auth__Issuer"] = Issuer,
        ["Auth__Audience"] = Audience,
        ["Auth__SigningPublicKeys__0"] = _tokenKey.ExportSubjectPublicKeyInfoPem(),
        ["Tenancy__HostCacheSeconds"] = "0",
    };

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
