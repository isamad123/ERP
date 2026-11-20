using System.Text.Json;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Domain.Identity;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>
/// Identity settings (slice S1.05 build items 2, 4, 5; FR-ADM-USR-20, 21, 24). Since slice S1.09a they are declared in the
/// kernel settings manifest (<c>Settings/settings.yaml</c>) with the same keys, defaults, rules and texts, registered by
/// <see cref="Settings.SettingsSetup"/>, and resolved by the one settings resolver; customers change them through the
/// settings services (screens: S1.15).
/// </summary>
public static class IdentitySettings
{
    public const string PasswordMinLength = KernelSettingKeys.IamPasswordMinLength;
    public const string PasswordMinCharacterKinds = KernelSettingKeys.IamPasswordMinCharacterKinds;
    public const string PasswordHistoryCount = KernelSettingKeys.IamPasswordHistoryCount;
    public const string PasswordExpiryDays = KernelSettingKeys.IamPasswordExpiryDays;
    public const string LockoutThreshold = KernelSettingKeys.IamLockoutThreshold;
    public const string LockoutMinutes = KernelSettingKeys.IamLockoutMinutes;
    public const string SessionIdleMinutes = KernelSettingKeys.IamSessionIdleMinutes;
    public const string SessionAbsoluteMinutes = KernelSettingKeys.IamSessionAbsoluteMinutes;
    public const string SessionMaxConcurrent = KernelSettingKeys.IamSessionMaxConcurrent;
    public const string SessionRememberDeviceDays = KernelSettingKeys.IamSessionRememberDeviceDays;
    public const string MfaRequired = KernelSettingKeys.IamMfaRequired;
    public const string MfaGraceDays = KernelSettingKeys.IamMfaGraceDays;
    public const string MfaRecoveryCodes = KernelSettingKeys.IamMfaRecoveryCodes;
    public const string MfaAllowedMethods = KernelSettingKeys.IamMfaAllowedMethods;
    public const string ActivationTokenHours = KernelSettingKeys.IamActivationTokenHours;
    public const string ChallengeMinutes = KernelSettingKeys.IamSignInChallengeMinutes;

    // Slice S1.06 (rulings Q6, Q10): privileged roles and maker-checker.
    public const string MfaRequiredForPrivileged = KernelSettingKeys.IamMfaRequiredForPrivileged;
    public const string PrivilegedPasswordMinLength = KernelSettingKeys.IamPasswordPrivilegedMinLength;
    public const string MakerCheckerSensitiveRoles = KernelSettingKeys.IamMakerCheckerSensitiveRoles;
    public const string MakerCheckerExpiryDays = KernelSettingKeys.IamMakerCheckerExpiryDays;
    public const string PasswordResetMinutes = KernelSettingKeys.IamPasswordResetMinutes;
    public const string PasswordResetPerHour = KernelSettingKeys.IamPasswordResetPerHour;
    public const string EmailCodeMinutes = KernelSettingKeys.IamEmailCodeMinutes;
    public const string EmailCodeMaxAttempts = KernelSettingKeys.IamEmailCodeMaxAttempts;

    /// <summary>The identity and access settings this class reads (all <c>iam.*</c> keys of the kernel manifest).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PasswordMinLength, PasswordMinCharacterKinds, PasswordHistoryCount, PasswordExpiryDays, LockoutThreshold, LockoutMinutes,
        SessionIdleMinutes, SessionAbsoluteMinutes, SessionMaxConcurrent, SessionRememberDeviceDays, MfaRequired, MfaGraceDays,
        MfaRecoveryCodes, MfaAllowedMethods, ActivationTokenHours, ChallengeMinutes, MfaRequiredForPrivileged, PrivilegedPasswordMinLength,
        MakerCheckerSensitiveRoles, MakerCheckerExpiryDays, PasswordResetMinutes, PasswordResetPerHour, EmailCodeMinutes, EmailCodeMaxAttempts,
    ];

    /// <summary>
    /// The effective values for one user, from the settings resolver (slice S1.09a): the user's value, then the customer's,
    /// then the shipped default. A stored value the setting no longer accepts (below its minimum, not a whole number) is
    /// skipped and the next layer used, so a bad stored value can never break sign-in (S1.05 review finding 10). MFA is
    /// required when the customer OR the user's own setting requires it: a user setting can tighten the customer's rule but
    /// never relax it (product owner, 2026-11-16; the setting is tighten-only, CR-037).
    /// </summary>
    /// <remarks>
    /// Role layer (slice S1.06 item 9, FR-ADM-USR-20, FR-ADM-USR-21, ruling Q10): MFA is also required when a role the
    /// user holds on <paramref name="now"/>'s date requires it (role-layer value of <c>iam.mfa.required</c>), or when the
    /// user holds a privileged role (one with a sensitive permission) and <c>iam.mfa.required_for_privileged</c> is on; the
    /// grace period then starts when that assignment was made. Privileged users need passwords of at least
    /// <c>iam.password.privileged_min_length</c> characters.
    /// </remarks>
    public static async Task<IdentityPolicy> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid? userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var definitions = await SettingsStore.DefinitionsAsync(connection, transaction, All, cancellationToken).ConfigureAwait(false);
        var stored = (await SettingsStore.ValuesAsync(connection, transaction, All, null, [], userId, cancellationToken).ConfigureAwait(false))
            .ToLookup(v => v.Key, StringComparer.Ordinal);
        var resolved = definitions.ToDictionary(d => d.Key, d => SettingResolution.Resolve(d.Value.Definition, stored[d.Key].Select(v => v.Value)), StringComparer.Ordinal);

        int Int(string key) => resolved[key].Value.GetInt32();
        bool Bool(string key) => resolved[key].Value.ValueKind == JsonValueKind.True;

        var (mfaRequired, mfaSince) = MfaRequirement(definitions[MfaRequired].Definition, stored[MfaRequired].Select(v => v.Value).ToList());
        var roles = userId is { } user ? await RoleFactsAsync(connection, transaction, user, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken).ConfigureAwait(false) : RoleFacts.None;
        var since = new List<DateTimeOffset?> { mfaRequired ? mfaSince : null };
        if (roles.MfaSince is { } byRole)
        {
            mfaRequired = true;
            since.Add(byRole);
        }

        if (roles.PrivilegedSince is { } privileged && Bool(MfaRequiredForPrivileged))
        {
            mfaRequired = true;
            var setAt = resolved[MfaRequiredForPrivileged].Layer == SettingLayers.Tenant
                ? stored[MfaRequiredForPrivileged].FirstOrDefault(v => v.Value.Layer == SettingLayers.Tenant)?.Value.UpdatedAt
                : null;
            since.Add(setAt is { } t && t > privileged ? t : privileged);
        }

        // Null when only the shipped default requires it (grace counts from activation), else the earliest applying start.
        mfaSince = since.Where(d => d is not null).Min();
        var minLength = roles.PrivilegedSince is null ? Int(PasswordMinLength) : Math.Max(Int(PasswordMinLength), Int(PrivilegedPasswordMinLength));
        var methods = resolved[MfaAllowedMethods].Value.EnumerateArray().Select(e => e.GetString()!).ToList();
        return new IdentityPolicy(
            new PasswordPolicy(minLength, Int(PasswordMinCharacterKinds), Int(PasswordHistoryCount), Int(PasswordExpiryDays), Int(LockoutThreshold), Int(LockoutMinutes)),
            new SessionPolicy(Int(SessionIdleMinutes), Int(SessionAbsoluteMinutes), Int(SessionMaxConcurrent), Int(SessionRememberDeviceDays)),
            new MfaPolicy(mfaRequired, mfaSince, Int(MfaGraceDays), Int(MfaRecoveryCodes), methods),
            TimeSpan.FromHours(Int(ActivationTokenHours)),
            TimeSpan.FromMinutes(Int(ChallengeMinutes)),
            new EmailLinkPolicy(TimeSpan.FromMinutes(Int(PasswordResetMinutes)), Int(PasswordResetPerHour), TimeSpan.FromMinutes(Int(EmailCodeMinutes)), Int(EmailCodeMaxAttempts)));
    }

    /// <summary>
    /// Required when the customer's valid value (or the shipped default) or the user's own valid value is true. The grace
    /// period starts at the earliest time a layer that requires it was set (null when only the default requires it).
    /// </summary>
    private static (bool Required, DateTimeOffset? Since) MfaRequirement(SettingDefinition definition, IReadOnlyList<StoredSettingValue> values)
    {
        StoredSettingValue? Valid(string layer) =>
            values.FirstOrDefault(v => v.Layer == layer && definition.Validation.Problem(definition.DataType, v.Value) is null);
        var tenant = Valid(SettingLayers.Tenant);
        var user = Valid(SettingLayers.User);
        var byCustomer = (tenant?.Value ?? definition.Default).ValueKind == JsonValueKind.True;
        var byUser = user is { Value.ValueKind: JsonValueKind.True };
        var since = new[] { byCustomer && tenant is not null ? tenant.UpdatedAt : (DateTimeOffset?)null, byUser ? user!.UpdatedAt : null }
            .Where(d => d is not null).Min();
        return (byCustomer || byUser, since);
    }

    /// <summary>
    /// The user's roles valid on the date (own and group assignments, slice S1.06): since when they hold a privileged role
    /// and since when a role requires MFA (null when none does).
    /// </summary>
    private static async Task<RoleFacts> RoleFactsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, DateOnly today, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Access.AccessSnapshot.AssignmentsCte + """
            select
                min(v.created_at) filter (where exists (
                    select 1 from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id and p.retired_at is null and p.is_sensitive
                    where rp.role_id = v.role_id)),
                min(greatest(v.created_at, sv.updated_at)) filter (where sv.value = 'true'::jsonb)
            from valid v
            left join cfg.setting_values sv on sv.setting_key = 'iam.mfa.required' and sv.layer = 'role' and sv.scope_id = v.role_id
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = today });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new RoleFacts(
            reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    /// <summary>Whether the user holds a privileged role (one with a sensitive permission) on the date, own or through a group.</summary>
    public static async Task<bool> IsPrivilegedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, DateOnly today, CancellationToken cancellationToken) =>
        (await RoleFactsAsync(connection, transaction, userId, today, cancellationToken).ConfigureAwait(false)).PrivilegedSince is not null;

    private sealed record RoleFacts(DateTimeOffset? PrivilegedSince, DateTimeOffset? MfaSince)
    {
        public static RoleFacts None { get; } = new(null, null);
    }
}

/// <summary>All identity policies that apply to one user at one moment.</summary>
public sealed record IdentityPolicy(PasswordPolicy Password, SessionPolicy Session, MfaPolicy Mfa, TimeSpan ActivationTokenLifetime, TimeSpan ChallengeLifetime, EmailLinkPolicy Email);

/// <summary>
/// Reset links and e-mail codes (slice S1.11b2, ruling Q16): how long a reset link and a code stay valid, how many reset
/// e-mails one account gets in an hour, and the wrong entries after which a code stops working.
/// </summary>
public sealed record EmailLinkPolicy(TimeSpan ResetLinkLifetime, int ResetLinksPerHour, TimeSpan CodeLifetime, int CodeAttempts);

/// <summary>
/// Identity set-up of a customer database, run after its migrations (provisioning and the fleet migrator): settings
/// definitions and the customer's field key.
/// </summary>
public static class IdentitySetup
{
    /// <summary>Registers the kernel's settings (its embedded manifest, slice S1.09a) and ensures the field key.</summary>
    public static Task EnsureAsync(NpgsqlConnection owner, BuildingBlocks.Application.Tenancy.ISecretStore secrets, string tenantCode, CancellationToken cancellationToken) =>
        EnsureAsync(owner, secrets, tenantCode, [], cancellationToken);

    /// <summary>Registers the settings of the installed modules' manifests (slice S1.09a) and ensures the field key.</summary>
    public static async Task EnsureAsync(NpgsqlConnection owner, BuildingBlocks.Application.Tenancy.ISecretStore secrets, string tenantCode, IReadOnlyList<string> manifestFolders, CancellationToken cancellationToken)
    {
        await Settings.SettingsSetup.EnsureAsync(owner, null, manifestFolders, cancellationToken).ConfigureAwait(false);
        await TenantFieldKeys.EnsureAsync(secrets, tenantCode, cancellationToken).ConfigureAwait(false);
        await Documents.TenantFileKeys.EnsureAsync(secrets, tenantCode, cancellationToken).ConfigureAwait(false);
    }
}
