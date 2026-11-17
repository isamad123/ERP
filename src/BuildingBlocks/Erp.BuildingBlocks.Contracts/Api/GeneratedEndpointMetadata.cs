namespace Erp.BuildingBlocks.Contracts.Api;

/// <summary>
/// Endpoint metadata attached to every generated endpoint, so tests (for example the tenant-isolation suite, S1.04)
/// can discover each endpoint, its resource and the permission it requires.
/// </summary>
public sealed record GeneratedEndpointMetadata(string Resource, string Action, string Permission);

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
