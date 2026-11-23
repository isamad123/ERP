namespace Erp.Kernel.Contracts.Metadata;

/// <summary>
/// One custom field as a business module's rules see it (slice S1.23a): key, data type, status (<c>active</c> or <c>retired</c>), labels,
/// and for choice fields the codes of the option list's active items with their labels.
/// </summary>
public sealed record CustomFieldInfo(string FieldKey, string DataType, string Status, Names Label, IReadOnlyList<CustomOption> Options);

/// <summary>
/// The custom fields defined on an extensible table, for module rules that name them (contract <c>ICustomFieldCatalog</c>; the policy
/// framework's rules on a custom field of the employment, S1.23a). Read-only and without a permission check: the caller's own endpoint
/// checks its permission; only definitions (never values) are answered.
/// </summary>
public interface ICustomFieldCatalog
{
    Task<IReadOnlyList<CustomFieldInfo>> FieldsAsync(string entityType, CancellationToken cancellationToken);
}
