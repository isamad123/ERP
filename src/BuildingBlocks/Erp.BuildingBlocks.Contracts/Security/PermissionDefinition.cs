namespace Erp.BuildingBlocks.Contracts.Security;

/// <summary>
/// A permission a module declares, registered into iam.permissions at start-up (KRN-IAM-10). Codes follow
/// <c>module.resource.action</c>. The description is a localization key, never display text.
/// </summary>
public sealed record PermissionDefinition(string Code, string ModuleId, string DescriptionKey, bool IsSensitive);
