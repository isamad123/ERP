using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Settings;

namespace Erp.Kernel.Application.Settings;

/// <summary>
/// The settings centre and flag services (slice S1.09a build items 2, 3, 5; rulings Q11, Q12, Q14, Q16). Every change is
/// validated, recorded with a reason in the history and the audit log, and applied at once, or held for a second
/// administrator when the setting needs approval (202 with the request id). <c>iam.*</c> keys keep the S1.06 rules by
/// going through the access settings service.
/// </summary>
public interface ISettingsAdministration
{
    /// <summary>Visible settings, filtered by text, module and area, with the effective value for the company (FR-ADM-SET-01).</summary>
    Task<IReadOnlyList<SettingListItem>> ListAsync(string? search, string? moduleId, string? area, Guid? companyId, CancellationToken cancellationToken);

    /// <summary>The layered view for a company, a role or a user (FR-ADM-SET-03); null when the key is unknown or hidden.</summary>
    Task<SettingDetail?> DetailAsync(string key, Guid? companyId, Guid? roleId, Guid? userId, CancellationToken cancellationToken);

    Task<AccessResult> SetAsync(string key, SetSettingRequest request, CancellationToken cancellationToken);

    /// <summary>Removes the layer's value, so the inherited one applies again.</summary>
    Task<AccessResult> ResetAsync(string key, ResetSettingRequest request, CancellationToken cancellationToken);

    /// <summary>History of a setting, newest first (FR-ADM-SET-06); null when the key is unknown.</summary>
    Task<IReadOnlyList<SettingHistoryItem>?> HistoryAsync(string key, string? layer, Guid? scopeId, CancellationToken cancellationToken);

    /// <summary>Writes the old value of a history row as a new change (no old value: removes the layer's value).</summary>
    Task<AccessResult> RevertAsync(Guid historyId, string? reason, CancellationToken cancellationToken);

    /// <summary>The caller's own view of a self-service setting (FR-ADM-USR-10); null when unknown or not self-service.</summary>
    Task<SettingDetail?> MineAsync(string key, CancellationToken cancellationToken);

    /// <summary>Sets the caller's own user-layer value of a self-service setting.</summary>
    Task<AccessResult> SetMineAsync(string key, SelfSettingRequest request, CancellationToken cancellationToken);

    /// <summary>Flags with their state for the company and the caller (FR-KRN-SET-05).</summary>
    Task<IReadOnlyList<FlagItem>> FlagsAsync(Guid? companyId, CancellationToken cancellationToken);

    Task<AccessResult> SetFlagAsync(string key, SetFlagRequest request, CancellationToken cancellationToken);

    Task<AccessResult> ResetFlagAsync(string key, ResetFlagRequest request, CancellationToken cancellationToken);
}
