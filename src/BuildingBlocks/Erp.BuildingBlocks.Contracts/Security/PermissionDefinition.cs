namespace Erp.BuildingBlocks.Contracts.Security;

/// <summary>
/// A permission a module declares, registered into iam.permissions at start-up (KRN-IAM-10). Codes follow
/// <c>module.resource.action</c>. The description is a localization key, never display text. <paramref name="IsApiClient"/> is the
/// manifest flag <c>api_client</c> (slice S1.27c): an API client may be given the permission as a scope; no other permission can
/// reach a client, whatever scopes were saved.
/// </summary>
public sealed record PermissionDefinition(string Code, string ModuleId, string DescriptionKey, bool IsSensitive, bool IsApiClient = false);
