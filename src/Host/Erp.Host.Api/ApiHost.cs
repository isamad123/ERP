using System.Security.Cryptography;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;
using Erp.Hrm.Api.Generated;
using Erp.Hrm.Api.Employments;
using Erp.Hrm.Api.People;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.People;
using Erp.Hrm.Infrastructure.Access;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Api.ControlPlane.Tenancy;
using Erp.Kernel.Api.Access;
using Erp.Kernel.Api.Metadata;
using Erp.Kernel.Api.Org;
using Erp.Kernel.Application.Org;
using Erp.Kernel.Infrastructure.Org;
using Erp.Kernel.Api.Audit;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Api.Identity;
using Erp.Kernel.Contracts.Identity;
using Erp.Kernel.Api.Numbering;
using Erp.Kernel.Application.Numbering;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Infrastructure.Numbering;
using Erp.Kernel.Api.Settings;
using Erp.Kernel.Api.Jobs;
using Erp.Kernel.Api.Documents;
using Erp.Kernel.Api.Notifications;
using Erp.Kernel.Infrastructure.Documents;
using Erp.Kernel.Api.Views;
using Erp.Kernel.Api.Workflow;
using Erp.Kernel.Infrastructure.Jobs;
using Erp.Kernel.Infrastructure.Notifications;
using Erp.Kernel.Infrastructure.Workflow;
using Erp.Kernel.Api.Entitlements;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Application.Settings;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.Settings;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Application.Audit;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Api.Generated;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.Entitlements;
using Erp.Kernel.Application.ControlPlane.Tenancy;
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
    /// <c>Tenancy:StatementTimeoutSeconds</c>, <c>Tenancy:HostCacheSeconds</c>, <c>Tenancy:MaxPoolSizePerTenant</c>;
    /// <c>Settings:RefreshMilliseconds</c> (how often a host checks for settings changes, slice S1.09a);
    /// <c>Web:Root</c> (the web application's build folder, slice S1.12a).
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
        // Audit (S1.07): every EF save writes its change-log entries in the same transaction; one correlation id per request.
        services.AddKernelAudit([.. KernelAuditCatalog.All, .. HrmAuditCatalog.All]);
        services.AddScoped<IAuditContext, HttpAuditContext>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IAuditVerifier, AuditVerifier>();
        services.AddDbContext<KernelDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<ITenantConnectionProvider>().DataSource).UseErpAudit(sp)
            .AddInterceptors(sp.GetRequiredService<CompanyLimitInterceptor>())); // the company limit, after the audit interceptor (S1.09b)
        services.AddDbContext<HrmDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<ITenantConnectionProvider>().DataSource).UseErpAudit(sp));
        services.AddKernelGeneratedRepositories().AddHrmGeneratedRepositories();
        services.AddScoped<ICurrentActor, ClaimsCurrentActor>();

        // Access control (S1.06): one engine decides permissions, data scopes and field access for every endpoint. The HR
        // module answers the team and department questions through the kernel's Contracts interface (Q3, D-10).
        services.AddKernelAccess();
        services.AddHrmAccess();
        services.AddHrmPeople(); // person services and the handler of held merges (S1.20a)
        // Employments (S1.20b): hire, events, as-of reads, user link; the approval of held events, the line-manager directory and
        // the active employee counters. The kernel's user directory answers whether a user may be linked (D-10).
        services.AddHrmEmployment().AddHrmEmploymentApi();
        services.AddSingleton<Erp.Kernel.Contracts.Workflow.IWorkflowDocumentTypeSource>(new WorkflowManifestSource(EmploymentServices.WorkflowManifest));
        services.AddSingleton(new NotificationManifestSource(EmploymentServices.NotificationManifest)); // S1.20c reminders
        services.AddScoped<Erp.Kernel.Contracts.Identity.IUserDirectory, Erp.Kernel.Infrastructure.Identity.UserDirectory>();
        services.AddSingleton(new ResourceCatalog([.. KernelResourceCatalog.All, .. HrmResourceCatalog.All]));
        services.AddScoped<AccessAdministrationService>();
        services.AddScoped<IRoleService>(sp => sp.GetRequiredService<AccessAdministrationService>());
        services.AddScoped<IAssignmentService>(sp => sp.GetRequiredService<AccessAdministrationService>());
        services.AddScoped<IMakerCheckerService>(sp => sp.GetRequiredService<AccessAdministrationService>());
        services.AddScoped<IAccessPreviewService>(sp => sp.GetRequiredService<AccessAdministrationService>());
        services.AddScoped<IUserConsoleService, UserConsoleService>();
        services.AddScoped<IOrganizationAdministration, OrganizationAdministrationService>(); // S1.14a: registrations, bank accounts, holiday years
        services.AddScoped<IOrgUnitService, OrgUnitService>(); // S1.14b: hierarchy on a date, compare, move of a unit
        services.AddScoped<IAccessConsoleService, AccessConsoleService>();
        services.AddScoped<IAccessSettingsService>(sp => sp.GetRequiredService<AccessAdministrationService>());

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
        services.AddScoped<IIdentityEmailService, IdentityEmailService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IUserAdministrationService, UserAdministrationService>();

        // Numbering (S1.08): one kernel service gives every document number; modules declare their document types.
        services.AddSingleton<IDocumentTypeSource, HrmNumberingTypes>();
        services.AddSingleton(sp => new DocumentTypeRegistry(sp.GetServices<IDocumentTypeSource>()));
        services.AddScoped<INumberAllocator, NumberAllocator>();
        services.AddScoped<INumberVoids, NumberVoids>();
        services.AddScoped<INumberingAdministration, NumberingAdministrationService>();

        // Settings and feature flags (S1.09a): one resolver over a per-host cache that follows cfg.config_versions, so a
        // change is in force on every host within about Settings:RefreshMilliseconds (default 1000, ruling Q13).
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SettingsCacheOptions(TimeSpan.FromMilliseconds(config.GetValue("Settings:RefreshMilliseconds", 1000))));
        services.AddSingleton<SettingsCache>();
        // Entitlements (S1.09b): the licence of each request is worked out from the stored entitlements and the customer's day.
        services.AddSingleton<ILicenceResolver, LicenceResolver>();
        services.AddScoped<ILicensedFeatures, EntitlementFeatures>();
        services.AddScoped<ILicenceService, LicenceService>();
        // Session context of the web shell (S1.12b, S1.12 ruling Q9).
        services.AddScoped<ISessionContextService, SessionContextService>();
        // Saved list views and label overrides of the web lists and forms (S1.12c, S1.12 rulings Q11 and Q12).
        services.AddScoped<Erp.Kernel.Contracts.Views.ISavedViewService, Erp.Kernel.Infrastructure.Views.SavedViewService>();
        services.AddScoped<Erp.Kernel.Contracts.Views.ILabelOverrideService, Erp.Kernel.Infrastructure.Views.LabelOverrideService>();
        // Labels, option lists and custom fields (S1.15b): the services behind the editors and the check of custom values on every write.
        services.AddScoped<Erp.Kernel.Contracts.Metadata.ILabelAdministration, Erp.Kernel.Infrastructure.Metadata.LabelAdministration>();
        services.AddScoped<Erp.Kernel.Contracts.Metadata.IOptionListAdministration, Erp.Kernel.Infrastructure.Metadata.OptionListAdministration>();
        services.AddScoped<Erp.Kernel.Contracts.Metadata.ICustomFieldAdministration, Erp.Kernel.Infrastructure.Metadata.CustomFieldAdministration>();
        services.AddScoped<Erp.Kernel.Contracts.Metadata.ICustomFieldValidator, Erp.Kernel.Infrastructure.Metadata.CustomFieldValidator>();
        // Limits (S1.09b, ruling Q7): one guard; the kernel counts named users, concurrent users and companies.
        services.AddSingleton<ILimitCounter, NamedUserCounter>();
        services.AddSingleton<ILimitCounter, ConcurrentUserCounter>();
        services.AddSingleton<ILimitCounter, CompanyCounter>();
        services.AddScoped<LimitWarnings>();
        services.AddScoped<ILimitWarningSource>(sp => sp.GetRequiredService<LimitWarnings>());
        services.AddScoped<ILimitGuard, LimitGuard>();
        services.AddSingleton<CompanyLimitInterceptor>();
        services.AddScoped<SettingsService>();
        services.AddScoped<ISettings>(sp => sp.GetRequiredService<SettingsService>());
        services.AddScoped<IFeatureFlags>(sp => sp.GetRequiredService<SettingsService>());
        services.AddScoped<ISettingsAdministration, SettingsAdministrationService>();
        services.AddScoped<Erp.Kernel.Contracts.Settings.IBrandingService, Erp.Kernel.Infrastructure.Settings.BrandingService>(); // S1.15a

        // Workflow engine (S1.10): modules register their document types (IWorkflowDocumentTypeSource), outcome handlers
        // and, for HR, the approver directory; routes are data seeded by provisioning and migrate.
        services.AddKernelWorkflow();

        // Job centre (S1.11a): lists and queues runs; the worker host runs them. Modules register their job manifests here too.
        services.AddKernelJobCentre();
        services.AddKernelNotifications();

        // Files (S1.11c1; docs/runbooks/file-store.md): the operator's store folder and virus scanner.
        services.AddKernelDocuments(key => config[key]);

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
        app.Services.VerifyWorkflow(); // manifests and outcome handlers checked at start (review S1.10a finding 4)
        var webRoot = WebHosting.Root(config); // the web application (slice S1.12a, ruling Q2); null = API only
        app.UseErpSecurityHeaders();
        app.UseErpAuditCorrelation();
        app.UseRouting();
        app.UseErpLimitResponses();
        app.UseErpTenantResolution(new TenantResolutionOptions { IsWebApplicationRequest = webRoot is null ? null : WebHosting.IsWebRequest });
        if (webRoot is not null)
        {
            app.UseErpWebApplication(webRoot);
        }

        app.UseAuthentication();
        app.UseErpTokenTenantCheck();
        app.UseAuthorization();
        app.MapHealthChecks("/health");
        app.MapIdentityAuthEndpoints();
        app.MapBrandingEndpoints(); // S1.15a: the public sign-in page's branding
        var api = app.MapGroup(string.Empty).RequireAuthorization();
        api.MapIdentityAccountEndpoints();
        api.MapAccessEndpoints();
        api.MapUserConsoleEndpoints();
        api.MapOrganizationEndpoints();
        api.MapAccessConsoleEndpoints();
        api.MapAuditEndpoints();
        api.MapNumberingEndpoints();
        api.MapSettingsEndpoints();
        api.MapLicenceEndpoints();
        api.MapSessionContextEndpoints();
        api.MapViewEndpoints();
        api.MapMetadataEndpoints();
        api.MapWorkflowEndpoints();
        api.MapJobEndpoints();
        api.MapNotificationEndpoints();
        api.MapDocumentEndpoints();
        api.MapTemplateEndpoints();
        api.MapKernelGeneratedEndpoints();
        api.MapHrmGeneratedEndpoints();
        api.MapHrmPeopleEndpoints();
        api.MapHrmEmploymentEndpoints();
        return app;
    }

    private static ECDsaSecurityKey ToKey(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return new ECDsaSecurityKey(key);
    }
}
