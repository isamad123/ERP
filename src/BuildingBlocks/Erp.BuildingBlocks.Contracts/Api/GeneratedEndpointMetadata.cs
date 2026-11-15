namespace Erp.BuildingBlocks.Contracts.Api;

/// <summary>
/// Endpoint metadata attached to every generated endpoint, so tests (for example the tenant-isolation suite, S1.04)
/// can discover each endpoint, its resource and the permission it requires.
/// </summary>
public sealed record GeneratedEndpointMetadata(string Resource, string Action, string Permission);

/// <summary>One keyset page of results; <see cref="Next"/> is passed back as <c>after</c> to read the next page.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, Guid? Next);
