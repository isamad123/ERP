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
