using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Host.Api;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>What an endpoint does with a record, judged from its HTTP method and route (S1.04).</summary>
public enum EndpointKind
{
    Unclassified,
    List,
    Get,
    Create,
    Update,
    Delete,

    /// <summary>Anonymous sign-in step (S1.05): the customer comes from the host; A's credentials and tokens must fail on B.</summary>
    IdentityAnonymous,

    /// <summary>Acts only on the signed-in user (S1.05): B's session never sees or changes A's sessions or devices.</summary>
    IdentitySelf,

    /// <summary>Administrator action on <c>/api/iam/users/{id}/...</c> (S1.05): A's user id on B's host is 404.</summary>
    IdentityUserAction,

    /// <summary>Access-control create (S1.06): a body naming A's ids is refused on B's host and creates nothing in A.</summary>
    AccessCreate,

    /// <summary>Access-control action on a record's route (S1.06): A's id on B's host is 404.</summary>
    AccessRecordAction,

    /// <summary>Access-control query (S1.06): a query string naming A's ids is 404 or returns nothing of A's.</summary>
    AccessQuery,

    /// <summary>Access-control catalogue (S1.06): B's catalogue never shows A's roles.</summary>
    AccessCatalog,

    /// <summary>Audit search or list (S1.07): filters naming A's ids return nothing of A's.</summary>
    AuditQuery,

    /// <summary>One record's audit history (S1.07): A's record id on B's host is 404.</summary>
    AuditRecord,

    /// <summary>The signed-in user's own sign-ins (S1.07): B's session never sees A's.</summary>
    AuditSelf,

    /// <summary>Chain check (S1.07): checks only the bound customer's chain.</summary>
    AuditVerify,

    /// <summary>Numbering scheme create (S1.08): a body naming A's company is refused on B's host and creates nothing in A.</summary>
    NumberingCreate,

    /// <summary>Action on a numbering scheme's route (S1.08): A's scheme id on B's host is 404.</summary>
    NumberingRecordAction,

    /// <summary>Registered document types (S1.08): B's list shows nothing of A's, even when the query names A's company.</summary>
    NumberingCatalog,

    /// <summary>Void report (S1.08): A's scheme named in the query string on B's host is 404.</summary>
    NumberingQuery,

    /// <summary>Settings or flags read (S1.09a): a query naming A's company, role or user answers B's data only.</summary>
    SettingsRead,

    /// <summary>Settings or flags change (S1.09a): a body or route naming A's company, group or history row is refused on B's host.</summary>
    SettingsWrite,

    /// <summary>Change of the signed-in user's own value (S1.09a): B's session never changes anything of A's.</summary>
    SettingsSelf,

    /// <summary>Licence read (S1.09b: capabilities, licence page, usage): B's answer shows B's licence and usage only.</summary>
    Licence,

    /// <summary>Workflow list (S1.10a: document types, routes, inbox): B's answer shows nothing of A's.</summary>
    WorkflowCatalog,

    /// <summary>Workflow action on a route, version, task or request id (S1.10a): A's id on B's host is 404.</summary>
    WorkflowRecordAction,

    /// <summary>Workflow create naming A's people (S1.10b delegations): refused on B's host (A's ids are unknown there), nothing written.</summary>
    WorkflowCreate,

    /// <summary>Workflow bulk action on A's task ids (S1.10b bulk approval): B's answer refuses each as not found.</summary>
    WorkflowBulk,

    /// <summary>Job-centre list or health numbers (S1.11a): B's answer shows nothing of A's.</summary>
    JobCatalog,

    /// <summary>Job-centre action on a job or run id (S1.11a): A's id on B's host is 404.</summary>
    JobRecordAction,

    /// <summary>Notification list, preference or administration call of the bound customer (S1.11b1): B's answer shows nothing of A's.</summary>
    NotificationCatalog,

    /// <summary>Notification action on an item or rule id (S1.11b1): A's id on B's host is 404.</summary>
    NotificationRecordAction,

    /// <summary>HR create (S1.20a): B's create writes nothing in A and answers nothing of A's.</summary>
    HrmCreate,

    /// <summary>HR action on a record's route (S1.20a: persons, their sections, review cases): A's id on B's host is 404.</summary>
    HrmRecordAction,

    /// <summary>HR list or catalogue of the bound customer (S1.20a): B's answer shows nothing of A's.</summary>
    HrmCatalog,

    /// <summary>The readiness checklist of the bound customer (S1.18a): B's answer for A's company is 404 and shows nothing of A's.</summary>
    SetupCatalog,

    /// <summary>A tick, skip, re-open or go-live naming a company (S1.18a): A's company on B's host is 404 and nothing changes in A.</summary>
    SetupAction,
}

/// <summary>One API endpoint as the API host maps it: method, route and the table it serves.</summary>
public sealed record ApiEndpoint(string Method, string Pattern, string? Resource, bool Anonymous, string? IdentityKind = null, string? AccessKind = null, string? AuditKind = null, string? NumberingKind = null, string? SettingsKind = null, string? LicenceKind = null, string? WorkflowKind = null, string? JobKind = null, string? NotificationKind = null, string? HrmKind = null, string? SetupKind = null)
{
    private const string IdSegment = "/{id:guid}";

    /// <summary>Stable key, also the xUnit theory argument: "GET /api/org/companies/{id:guid}".</summary>
    public string Key => Method + " " + Pattern;

    /// <summary>The collection route without the trailing id segment.</summary>
    public string BasePath => (Pattern.EndsWith(IdSegment, StringComparison.Ordinal) ? Pattern[..^IdSegment.Length] : Pattern).TrimEnd('/');

    public EndpointKind Kind
    {
        get
        {
            switch (HrmKind)
            {
                case "create": return EndpointKind.HrmCreate;
                case "record_action": return EndpointKind.HrmRecordAction;
                case "catalog": return EndpointKind.HrmCatalog;
                case not null: return EndpointKind.Unclassified;
            }

            switch (NotificationKind)
            {
                case "catalog" or "own_catalog": return EndpointKind.NotificationCatalog;
                case "record_action": return EndpointKind.NotificationRecordAction;
                case not null: return EndpointKind.Unclassified;
            }

            switch (JobKind)
            {
                case "catalog": return EndpointKind.JobCatalog;
                case "record_action": return EndpointKind.JobRecordAction;
                case not null: return EndpointKind.Unclassified;
            }

            switch (WorkflowKind)
            {
                case "catalog": return EndpointKind.WorkflowCatalog;
                case "record_action": return EndpointKind.WorkflowRecordAction;
                case "create": return EndpointKind.WorkflowCreate;
                case "bulk": return EndpointKind.WorkflowBulk;
                case not null: return EndpointKind.Unclassified;
            }

            if (LicenceKind is not null)
            {
                return LicenceKind is "capabilities" or "licence" or "usage" ? EndpointKind.Licence : EndpointKind.Unclassified;
            }

            switch (SettingsKind)
            {
                case "read": return EndpointKind.SettingsRead;
                case "write": return EndpointKind.SettingsWrite;
                case "self": return EndpointKind.SettingsSelf;
                case not null: return EndpointKind.Unclassified;
            }

            switch (SetupKind)
            {
                case "catalog": return EndpointKind.SetupCatalog;
                case "action": return EndpointKind.SetupAction;
                case not null: return EndpointKind.Unclassified;
            }

            switch (NumberingKind)
            {
                case "create": return EndpointKind.NumberingCreate;
                case "record_action": return EndpointKind.NumberingRecordAction;
                case "catalog": return EndpointKind.NumberingCatalog;
                case "query": return EndpointKind.NumberingQuery;
                case not null: return EndpointKind.Unclassified;
            }

            switch (AuditKind)
            {
                case "query": return EndpointKind.AuditQuery;
                case "record": return EndpointKind.AuditRecord;
                case "self": return EndpointKind.AuditSelf;
                case "verify": return EndpointKind.AuditVerify;
                case not null: return EndpointKind.Unclassified;
            }

            switch (AccessKind)
            {
                case "create": return EndpointKind.AccessCreate;
                case "record_action": return EndpointKind.AccessRecordAction;
                case "query": return EndpointKind.AccessQuery;
                case "catalog": return EndpointKind.AccessCatalog;
                case not null: return EndpointKind.Unclassified;
            }

            switch (IdentityKind)
            {
                case "anonymous": return EndpointKind.IdentityAnonymous;
                case "self": return EndpointKind.IdentitySelf;
                case "user_action": return EndpointKind.IdentityUserAction;
                case not null: return EndpointKind.Unclassified;
            }

            var byId = Pattern.EndsWith(IdSegment, StringComparison.Ordinal) && Pattern.IndexOf('{', StringComparison.Ordinal) == Pattern.Length - IdSegment.Length + 1;
            var hasParameter = Pattern.Contains('{', StringComparison.Ordinal);
            return (Method, byId, hasParameter) switch
            {
                ("GET", false, false) => EndpointKind.List,
                ("GET", true, _) => EndpointKind.Get,
                ("POST", false, false) => EndpointKind.Create,
                ("PUT", true, _) => EndpointKind.Update,
                ("DELETE", true, _) => EndpointKind.Delete,
                _ => EndpointKind.Unclassified,
            };
        }
    }

    public override string ToString() => Key;
}

/// <summary>
/// Every endpoint of the API host, read from the endpoint metadata of the real pipeline (S1.04 build item 1). New
/// endpoints join the isolation suite automatically; one that cannot be probed fails the suite instead of being skipped.
/// </summary>
public static class EndpointInventory
{
    /// <summary>
    /// Endpoints that need no login and serve no customer data, so there is nothing to cross. Adding to this list is a
    /// security decision: it needs the product owner's approval and a note in the slice report.
    /// </summary>
    public static readonly IReadOnlySet<string> TenantFree = new HashSet<string>(StringComparer.Ordinal) { "* /health" };

    /// <summary>
    /// Sign-in steps that answer without a login (slice S1.05). They are customer endpoints: the customer comes from the
    /// host, and the suite probes each with customer A's credentials and tokens on B's host. Reviewed list: a new
    /// anonymous endpoint fails the suite until it is added here (security decision, noted in the slice report).
    /// </summary>
    public static readonly IReadOnlySet<string> AnonymousSignIn = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /api/iam/auth/sign-in",
        "POST /api/iam/auth/mfa",
        "POST /api/iam/auth/password-change",
        "POST /api/iam/auth/mfa-enrolment",
        "POST /api/iam/auth/mfa-enrolment/confirm",
        "POST /api/iam/auth/refresh",
        "POST /api/iam/auth/activate",

        // Slice S1.11b2 (security review, plan points P9 and P11): forgot password answers the same for any login; the
        // reset needs the e-mailed single-use token; the e-mail code needs a valid second-factor challenge.
        "POST /api/iam/auth/password/forgot",
        "POST /api/iam/auth/password/reset",
        "POST /api/iam/auth/mfa/email-code",

        // Slice S1.15a (FR-ADM-SEC-08, S1.15 ruling Q3): the sign-in page needs the customer's logo, product name, banner and
        // terms before anyone is known. They answer from the host's customer only and carry no other data. Security decision of
        // the product owner (CR-071), noted in the slice report.
        "GET /api/kernel/branding",
        "GET /api/kernel/branding/logo",
    };

    private static readonly Lazy<IReadOnlyList<ApiEndpoint>> Discovered = new(DiscoverFromHost);

    /// <summary>The endpoints of <see cref="ApiHost"/> as built for production (not started, no database needed).</summary>
    public static IReadOnlyList<ApiEndpoint> All => Discovered.Value;

    /// <summary>xUnit theory rows: one per endpoint that needs a customer.</summary>
    public static TheoryData<string> CustomerEndpoints()
    {
        var data = new TheoryData<string>();
        foreach (var endpoint in All.Where(e => !TenantFree.Contains(e.Key)))
        {
            data.Add(endpoint.Key);
        }

        return data;
    }

    public static ApiEndpoint Find(string key) => All.Single(e => e.Key == key);

    /// <summary>Reads the endpoints of a built host.</summary>
    public static IReadOnlyList<ApiEndpoint> Read(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var list = new List<ApiEndpoint>();
        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            var access = endpoint.Metadata.GetMetadata<AccessEndpointMetadata>();
            var audit = endpoint.Metadata.GetMetadata<AuditEndpointMetadata>();
            var numbering = endpoint.Metadata.GetMetadata<NumberingEndpointMetadata>();
            var settings = endpoint.Metadata.GetMetadata<SettingsEndpointMetadata>();
            var licence = endpoint.Metadata.GetMetadata<LicenceEndpointMetadata>();
            var workflow = endpoint.Metadata.GetMetadata<WorkflowEndpointMetadata>();
            var job = endpoint.Metadata.GetMetadata<JobEndpointMetadata>();
            var notification = endpoint.Metadata.GetMetadata<NotificationEndpointMetadata>();
            var hrm = endpoint.Metadata.GetMetadata<Erp.Hrm.Contracts.HrmEndpointMetadata>();
            var setup = endpoint.Metadata.GetMetadata<SetupEndpointMetadata>();
            var resource = endpoint.Metadata.GetMetadata<GeneratedEndpointMetadata>()?.Resource ?? access?.Resource ?? audit?.Resource ?? numbering?.Resource ?? settings?.Resource ?? workflow?.Resource ?? job?.Resource ?? notification?.Resource ?? hrm?.Resource ?? setup?.Resource
                ?? (licence is null ? null : "cp.entitlements");
            var identity = endpoint.Metadata.GetMetadata<IdentityEndpointMetadata>()?.Kind;
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null || endpoint.Metadata.GetMetadata<IAuthorizeData>() is null;
            foreach (var method in methods is { Count: > 0 } ? methods : ["*"])
            {
                list.Add(new ApiEndpoint(method, "/" + endpoint.RoutePattern.RawText!.TrimStart('/'), resource, anonymous, identity, access?.Kind, audit?.Kind, numbering?.Kind, settings?.Kind, licence?.Kind, workflow?.Kind, job?.Kind, notification?.Kind, hrm?.Kind, setup?.Kind));
            }
        }

        return [.. list.OrderBy(e => e.Pattern, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<ApiEndpoint> DiscoverFromHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["ConnectionStrings:ControlPlane"] = "Host=discovery.invalid;Database=unused";
        builder.Configuration["Auth:Issuer"] = "https://discovery.invalid";
        builder.Configuration["Auth:Audience"] = "discovery";
        var secrets = new SigningKeyOnly();
        TokenSigningKeys.EnsureAsync(secrets, "discovery", CancellationToken.None).GetAwaiter().GetResult();
        var app = ApiHost.Build(builder, secrets);
        return Read(app);
    }

    /// <summary>
    /// Discovery never opens a database. The host only reads its token signing key at start-up (S1.05), so this store
    /// holds a throw-away key in memory and nothing else.
    /// </summary>
    private sealed class SigningKeyOnly : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string reference, CancellationToken cancellationToken) =>
            reference.StartsWith("platform/token-signing/", StringComparison.Ordinal)
                ? Task.FromResult(_values.GetValueOrDefault(reference))
                : throw new InvalidOperationException("not used during discovery");

        public Task PutAsync(string reference, string value, CancellationToken cancellationToken)
        {
            _values[reference] = value;
            return Task.CompletedTask;
        }
    }
}
