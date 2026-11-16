using System.Security.Cryptography;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;
using Erp.Hrm.Api.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Api.ControlPlane.Tenancy;
using Erp.Kernel.Api.Identity;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Api.Generated;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.Generated;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Erp.Host.Api;

/// <summary>
/// Composition of the API host (S1.03): tenant resolution by host name, token checks, per-customer DbContexts and the
/// generated endpoints. Used by Program and by the integration tests, so tests exercise the real pipeline.
/// </summary>
public static class ApiHost
{
    /// <summary>
    /// Settings read: <c>ConnectionStrings:ControlPlane</c>; <c>ERP_SECRETS_FILE</c>, <c>ERP_SECRETS_KEY</c>;
    /// <c>Auth:Issuer</c>, <c>Auth:Audience</c>, <c>Auth:SigningPublicKeys</c> (PEM, ECDSA P-256);
    /// <c>Tenancy:StatementTimeoutSeconds</c>, <c>Tenancy:HostCacheSeconds</c>, <c>Tenancy:MaxPoolSizePerTenant</c>.
    /// <paramref name="overrides"/> runs last (tests replace the permission checker and data scopes there).
    /// </summary>
    public static WebApplication Build(WebApplicationBuilder builder, ISecretStore? secrets = null, Action<IServiceCollection>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var config = builder.Configuration;
        var options = new ControlPlaneOptions
        {
            ConnectionString = config.GetConnectionString("ControlPlane") ?? throw new InvalidOperationException("ConnectionStrings:ControlPlane is not set."),
            StatementTimeoutSeconds = config.GetValue("Tenancy:StatementTimeoutSeconds", 30),
            HostCacheSeconds = config.GetValue("Tenancy:HostCacheSeconds", 30),
            MaxPoolSizePerTenant = config.GetValue("Tenancy:MaxPoolSizePerTenant", 20),
        };
        var services = builder.Services;
        services.AddSingleton<IClock, SystemClock>();
        services.AddHealthChecks();
        services.AddHttpContextAccessor();
        var secretStore = secrets ?? EncryptedFileSecretStore.FromSettings(config["ERP_SECRETS_FILE"], config["ERP_SECRETS_KEY"]);
        services.AddErpTenancy(options, secretStore);
        services.AddDbContext<KernelDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<ITenantConnectionProvider>().DataSource));
        services.AddDbContext<HrmDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<ITenantConnectionProvider>().DataSource));
        services.AddKernelGeneratedRepositories().AddHrmGeneratedRepositories();
        services.AddScoped<ICurrentActor, ClaimsCurrentActor>();

        // Until roles, permissions and data scopes exist (S1.06) nothing is granted: every business endpoint answers 403.
        services.AddSingleton<IPermissionChecker, NoGrantsPermissionChecker>();
        services.AddSingleton<IDataScopeFilter, DataScopesNotConfigured>();

        // Identity (S1.05): the API trusts only tokens signed with the keys in the secret store (created by the migrator's
        // `migrate` command). Auth:SigningPublicKeys may add further public keys (the integration tests use one).
        var signing = TokenSigningKeys.LoadAsync(secretStore, CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("No token signing key in the secret store. Run the migrator's `migrate` command first (docs/runbooks/tenant-operations.md).");
        var issuer = config["Auth:Issuer"] ?? throw new InvalidOperationException("Auth:Issuer is not set.");
        var audience = config["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience is not set.");
        services.AddSingleton(signing);
        services.AddSingleton<IAccessTokenIssuer>(new JwtAccessTokenIssuer(signing, issuer, audience));
        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<FieldKeyCache>();
        services.AddScoped<IFieldEncryptorProvider, TenantFieldEncryptorProvider>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IUserAdministrationService, UserAdministrationService>();

        var keys = signing.Validation.Concat((config.GetSection("Auth:SigningPublicKeys").Get<string[]>() ?? []).Select(ToKey)).ToList();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidAudience = audience,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                IssuerSigningKeys = keys, // no keys configured: every token is refused
                // The API issues and checks its own tokens on the same clock, so no skew is allowed: a revoked session's last
                // access token is refused at most 5 minutes later, as ruled (Q3; S1.05 review finding 7).
                ClockSkew = TimeSpan.Zero,
            };
        });
        services.AddAuthorization();
        overrides?.Invoke(services);

        var app = builder.Build();
        app.UseRouting();
        app.UseErpTenantResolution();
        app.UseAuthentication();
        app.UseErpTokenTenantCheck();
        app.UseAuthorization();
        app.MapHealthChecks("/health");
        app.MapIdentityAuthEndpoints();
        var api = app.MapGroup(string.Empty).RequireAuthorization();
        api.MapIdentityAccountEndpoints();
        api.MapKernelGeneratedEndpoints();
        api.MapHrmGeneratedEndpoints();
        return app;
    }

    private static ECDsaSecurityKey ToKey(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return new ECDsaSecurityKey(key);
    }
}

/// <summary>Grants nothing. Registered until the permission service of slice S1.06 replaces it; endpoints answer 403.</summary>
internal sealed class NoGrantsPermissionChecker : IPermissionChecker
{
    public ValueTask<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

/// <summary>Data scopes arrive in S1.06. Never reached while <see cref="NoGrantsPermissionChecker"/> refuses every call.</summary>
internal sealed class DataScopesNotConfigured : IDataScopeFilter
{
    public IQueryable<T> Apply<T>(IQueryable<T> query, string resource)
        where T : class => throw new InvalidOperationException("Data scopes are not configured (slice S1.06).");
}
