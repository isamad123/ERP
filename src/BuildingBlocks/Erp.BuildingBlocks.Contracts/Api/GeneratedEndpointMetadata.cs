namespace Erp.BuildingBlocks.Contracts.Api;

/// <summary>
/// Endpoint metadata attached to every generated endpoint, so tests (for example the tenant-isolation suite, S1.04)
/// can discover each endpoint, its resource and the permission it requires.
/// </summary>
public sealed record GeneratedEndpointMetadata(string Resource, string Action, string Permission);

/// <summary>
/// The entitlement feature key and module an endpoint belongs to (FR-KRN-ENT-03; slice S1.09 ruling Q9). For a customer
/// not licensed for the key or the module the endpoint answers exactly like one that does not exist. Generated endpoints
/// take the key from the data design (<c>entitlement:</c>); an endpoint without this metadata belongs to the kernel
/// (<see cref="PlatformBase"/>).
/// </summary>
public sealed record EntitlementMetadata(string FeatureKey, string Module)
{
    public const string PlatformBase = "platform.base";

    public const string KernelModule = "kernel";

    public static EntitlementMetadata Kernel { get; } = new(PlatformBase, KernelModule);
}

/// <summary>
/// Marks an endpoint that still answers while the customer's licence is locked or the customer is suspended (FR-KRN-ENT-04,
/// FR-KRN-TEN-09; slice S1.09 ruling Q5): the licence page, the capabilities of the web shell and the exports the
/// specification allows. Its own permission check still applies. Sign-in steps are marked by
/// <see cref="IdentityEndpointMetadata.AllowedWhenReadOnly"/>, which covers locked as well.
/// </summary>
public sealed record AllowedWhenLockedMetadata;

/// <summary>
/// Marks an endpoint that changes data but still answers when the customer is read-only or locked: the export of what a person
/// may read (FR-KRN-ENT-04, slice S1.16b). Its own permission check still applies.
/// </summary>
public sealed record AllowedWhenReadOnlyMetadata;

/// <summary>One keyset page of results; <see cref="Next"/> is passed back as <c>after</c> to read the next page.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, Guid? Next);

/// <summary>
/// Metadata of the purpose-built identity endpoints (slice S1.05), so the tenant-isolation suite (S1.04) can find and
/// probe them. <see cref="Kind"/>: <c>anonymous</c> (sign-in steps; customer from the host only), <c>self</c> (acts
/// only on the signed-in user) or <c>user_action</c> (an administrator action on <c>/api/iam/users/{id}/...</c>).
/// <see cref="AllowedWhenReadOnly"/>: sign-in must still work while a customer is read-only (FR-KRN-TEN-09), otherwise
/// nobody could read the data the mode keeps visible.
/// </summary>
public sealed record IdentityEndpointMetadata(string Kind, string? Permission, bool AllowedWhenReadOnly);

/// <summary>
/// Metadata of the purpose-built access-control endpoints (slice S1.06), so the tenant-isolation suite (S1.04) can find
/// and probe them. <see cref="Resource"/> is the table whose ids the route or body carries. <see cref="Kind"/>:
/// <c>create</c> (POST to the collection), <c>record_action</c> (a route with the record's <c>{id}</c>), <c>query</c>
/// (a GET whose query string names records) or <c>catalog</c> (a GET of the customer's catalogue).
/// </summary>
public sealed record AccessEndpointMetadata(string Kind, string Resource, string Permission);

/// <summary>
/// Marks an audit endpoint (slice S1.07) for the tenant-isolation suite: <see cref="Kind"/> is <c>query</c> (search and
/// lists filtered by ids), <c>record</c> (one record's history by id), <c>self</c> (the signed-in user's own sign-ins) or
/// <c>verify</c> (chain check of the bound customer).
/// </summary>
public sealed record AuditEndpointMetadata(string Kind, string Resource, string Permission);

/// <summary>
/// Marks a numbering endpoint (slice S1.08) for the tenant-isolation suite: <see cref="Kind"/> is <c>create</c> (POST of a
/// new scheme), <c>record_action</c> (a route with the scheme's <c>{id}</c>), <c>catalog</c> (the registered document
/// types, optionally for one company) or <c>query</c> (the void report of a scheme named in the query string).
/// </summary>
public sealed record NumberingEndpointMetadata(string Kind, string Resource, string Permission);

/// <summary>
/// Marks a readiness checklist endpoint (slice S1.18a) for the tenant-isolation suite: <see cref="Kind"/> is <c>catalog</c> (the
/// checklist of the bound customer, optionally for one company named in the query string) or <c>action</c> (a tick, skip,
/// re-open or go-live whose body names a company).
/// </summary>
public sealed record SetupEndpointMetadata(string Kind, string Resource, string Permission);

/// <summary>
/// Marks a settings or feature-flag endpoint (slice S1.09a) for the tenant-isolation suite: <see cref="Kind"/> is
/// <c>read</c> (a GET whose query string may name companies, roles or users), <c>write</c> (a change whose body or route names
/// a company, a user group or a history row) or <c>self</c> (a change of the signed-in user's own value).
/// </summary>
public sealed record SettingsEndpointMetadata(string Kind, string Resource, string Permission);

/// <summary>
/// Marks a licence endpoint (slice S1.09b) for the tenant-isolation suite: <see cref="Kind"/> is <c>capabilities</c> (what the
/// signed-in caller's customer may use), <c>licence</c> (the licence page) or <c>usage</c> (monthly statements of the bound
/// customer; the query string names months only).
/// </summary>
public sealed record LicenceEndpointMetadata(string Kind, string Permission);

/// <summary>
/// Marks a workflow endpoint (slice S1.10) for the tenant-isolation suite: <see cref="Kind"/> is <c>catalog</c> (a list of
/// the bound customer's own document types, routes or inbox tasks) or <c>record_action</c> (a route with the
/// <c>{id}</c> of a route, version, task or request: another customer's id is 404).
/// </summary>
public sealed record WorkflowEndpointMetadata(string Kind, string Resource, string Permission);

/// <summary>
/// Marks a job-centre endpoint (slice S1.11a) for the tenant-isolation suite: <see cref="Kind"/> is <c>catalog</c> (a list or
/// the health numbers of the bound customer) or <c>record_action</c> (a route with the <c>{id}</c> of a job or a run: another
/// customer's id is 404).
/// </summary>
public sealed record JobEndpointMetadata(string Kind, string Resource, string Permission);

/// <summary>
/// Marks an endpoint of the notification API (slice S1.11b1) for the permission and isolation suites. <c>Kind</c> is
/// <c>own_catalog</c> (the signed-in person's own items or preferences: another customer's person is never visible),
/// <c>catalog</c> (a list or action of the bound customer) or <c>record_action</c> (a route with the <c>{id}</c> of an item
/// or rule: another customer's id is 404).
/// </summary>
public sealed record NotificationEndpointMetadata(string Kind, string Resource, string Permission);

/// <summary>
/// Marks a pack manager endpoint (slice S1.23b) for the tenant-isolation suite: <see cref="Kind"/> is <c>catalog</c> (the bound customer's
/// installed packs and the vendor catalogue), <c>query</c> (the objects of one installed pack, named by pack id), <c>action</c> (install
/// or upgrade, naming a pack id) or <c>record_action</c> (a route with a pack item's <c>{id}</c>).
/// </summary>
public sealed record PackEndpointMetadata(string Kind, string Resource, string Permission);

/// <summary>
/// Marks an endpoint an API client may call with its key (slice S1.27c): the permission it needs, which must be flagged <c>api_client</c> in a permission
/// manifest and be one of the client's scopes. An endpoint without this mark answers 403 to every API key; a key never opens a web session or any
/// <c>/api/iam</c> endpoint. The permission is also checked by the endpoint's own service, which sees the client with only its scopes.
/// </summary>
public sealed record ApiClientEndpointMetadata(string Permission);
