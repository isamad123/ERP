using System.Text.Json;
using Erp.Kernel.Domain.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>
/// Identity settings registered in cfg.settings (slice S1.05 build items 2, 4, 5; FR-ADM-USR-20, 21, 24). Shipped
/// defaults are product-owner ruling Q5; customers change them in cfg.setting_values (screens: S1.15). Seeded
/// idempotently by key whenever a customer database is migrated.
/// </summary>
public static class IdentitySettings
{
    public const string PasswordMinLength = "iam.password.min_length";
    public const string PasswordMinCharacterKinds = "iam.password.min_character_kinds";
    public const string PasswordHistoryCount = "iam.password.history_count";
    public const string PasswordExpiryDays = "iam.password.expiry_days";
    public const string LockoutThreshold = "iam.lockout.threshold";
    public const string LockoutMinutes = "iam.lockout.minutes";
    public const string SessionIdleMinutes = "iam.session.idle_minutes";
    public const string SessionAbsoluteMinutes = "iam.session.absolute_minutes";
    public const string SessionMaxConcurrent = "iam.session.max_concurrent";
    public const string SessionRememberDeviceDays = "iam.session.remember_device_days";
    public const string MfaRequired = "iam.mfa.required";
    public const string MfaGraceDays = "iam.mfa.grace_days";
    public const string MfaRecoveryCodes = "iam.mfa.recovery_codes";
    public const string MfaAllowedMethods = "iam.mfa.allowed_methods";
    public const string ActivationTokenHours = "iam.activation.token_hours";
    public const string ChallengeMinutes = "iam.sign_in.challenge_minutes";

    /// <summary>Definition of one setting: key, type, default (JSON), minimum, layers and descriptions (en, ur).</summary>
    public sealed record Definition(string Key, string DataType, string DefaultJson, int? Min, string[] Layers, string English, string Urdu);

    public static IReadOnlyList<Definition> All { get; } =
    [
        new(PasswordMinLength, "int", "10", 6, ["tenant"], "Minimum password length", "پاس ورڈ کی کم از کم لمبائی"),
        new(PasswordMinCharacterKinds, "int", "3", 1, ["tenant"], "Character kinds required (of upper, lower, digit, symbol)", "درکار حروف کی اقسام (بڑے، چھوٹے، ہندسے، علامات میں سے)"),
        new(PasswordHistoryCount, "int", "5", 0, ["tenant"], "Previous passwords that cannot be reused", "پچھلے پاس ورڈ جو دوبارہ استعمال نہیں ہو سکتے"),
        new(PasswordExpiryDays, "int", "0", 0, ["tenant"], "Days until a password expires (0 = never)", "پاس ورڈ کی میعاد کے دن (0 = کبھی نہیں)"),
        new(LockoutThreshold, "int", "5", 0, ["tenant"], "Failed sign-ins before the account locks (0 = never)", "اکاؤنٹ بند ہونے سے پہلے ناکام سائن ان (0 = کبھی نہیں)"),
        new(LockoutMinutes, "int", "15", 1, ["tenant"], "Minutes an automatic lock lasts", "خودکار بندش کے منٹ"),
        new(SessionIdleMinutes, "int", "30", 5, ["tenant"], "Minutes without activity before a session ends", "غیر فعالیت کے منٹ جن کے بعد سیشن ختم ہو جاتا ہے"),
        new(SessionAbsoluteMinutes, "int", "720", 5, ["tenant"], "Maximum session length in minutes", "سیشن کی زیادہ سے زیادہ مدت منٹوں میں"),
        new(SessionMaxConcurrent, "int", "3", 1, ["tenant"], "Sessions a user may hold at once", "ایک صارف کے بیک وقت سیشن"),
        new(SessionRememberDeviceDays, "int", "0", 0, ["tenant"], "Days a trusted device is remembered (0 = off; used from slice S1.12)", "قابل اعتماد آلہ یاد رکھنے کے دن (0 = بند)"),
        new(MfaRequired, "bool", "false", null, ["tenant", "user"], "Second factor required at sign-in", "سائن ان پر دوسرا مرحلہ لازمی"),
        new(MfaGraceDays, "int", "7", 0, ["tenant"], "Days allowed to enrol a second factor once required", "لازمی ہونے کے بعد دوسرا مرحلہ اندراج کرنے کے دن"),
        new(MfaRecoveryCodes, "int", "10", 1, ["tenant"], "Recovery codes issued at enrolment", "اندراج پر جاری کردہ بحالی کوڈ"),
        new(MfaAllowedMethods, "list", "[\"totp\"]", null, ["tenant"], "Allowed second-factor methods", "دوسرے مرحلے کے اجازت یافتہ طریقے"),
        new(ActivationTokenHours, "int", "72", 1, ["tenant"], "Hours an account activation link stays valid", "اکاؤنٹ فعال کرنے کا لنک کتنے گھنٹے درست رہتا ہے"),
        new(ChallengeMinutes, "int", "10", 1, ["tenant"], "Minutes to complete a sign-in step (second factor, password change)", "سائن ان کا اگلا مرحلہ مکمل کرنے کے منٹ"),
    ];

    /// <summary>Registers or refreshes the definitions; customer values in cfg.setting_values are never touched.</summary>
    public static async Task SeedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        foreach (var d in All)
        {
            await using var command = new NpgsqlCommand("""
                insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n)
                values ($1, 'iam', $2, $3, $4, $5, false, $6)
                on conflict (key) do update set data_type = excluded.data_type, default_value = excluded.default_value,
                    validation = excluded.validation, allowed_layers = excluded.allowed_layers, description_i18n = excluded.description_i18n
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = d.Key });
            command.Parameters.Add(new NpgsqlParameter { Value = d.DataType });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = d.DefaultJson });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = d.Min is { } min ? JsonSerializer.Serialize(new { min }) : "{}" });
            command.Parameters.Add(new NpgsqlParameter { Value = d.Layers });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(new Dictionary<string, string> { ["en"] = d.English, ["ur"] = d.Urdu }) });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The effective values for one user: user layer, then tenant layer, then the shipped default.</summary>
    public static async Task<IdentityPolicy> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid? userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand("""
            select s.key, coalesce(u.value, t.value, s.default_value), coalesce(u.updated_at, t.updated_at)
            from cfg.settings s
            left join cfg.setting_values t on t.setting_key = s.key and t.layer = 'tenant' and t.scope_id is null
            left join cfg.setting_values u on u.setting_key = s.key and u.layer = 'user' and u.scope_id = $1 and 'user' = any (s.allowed_layers)
            where s.module_id = 'iam'
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)userId ?? DBNull.Value });
        var values = new Dictionary<string, (JsonElement Value, DateTimeOffset? Since)>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                using var doc = JsonDocument.Parse(reader.GetString(1));
                values[reader.GetString(0)] = (doc.RootElement.Clone(), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
            }
        }

        int Int(string key) => values.TryGetValue(key, out var v) && v.Value.ValueKind == JsonValueKind.Number ? v.Value.GetInt32() : int.Parse(All.First(d => d.Key == key).DefaultJson, System.Globalization.CultureInfo.InvariantCulture);
        var mfaRequired = values.TryGetValue(MfaRequired, out var m) && m.Value.ValueKind == JsonValueKind.True;
        var methods = values.TryGetValue(MfaAllowedMethods, out var am) && am.Value.ValueKind == JsonValueKind.Array
            ? am.Value.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : ["totp"];
        return new IdentityPolicy(
            new PasswordPolicy(Int(PasswordMinLength), Int(PasswordMinCharacterKinds), Int(PasswordHistoryCount), Int(PasswordExpiryDays), Int(LockoutThreshold), Int(LockoutMinutes)),
            new SessionPolicy(Int(SessionIdleMinutes), Int(SessionAbsoluteMinutes), Int(SessionMaxConcurrent), Int(SessionRememberDeviceDays)),
            new MfaPolicy(mfaRequired, mfaRequired ? m.Since : null, Int(MfaGraceDays), Int(MfaRecoveryCodes), methods),
            TimeSpan.FromHours(Int(ActivationTokenHours)),
            TimeSpan.FromMinutes(Int(ChallengeMinutes)));
    }
}

/// <summary>All identity policies that apply to one user at one moment.</summary>
public sealed record IdentityPolicy(PasswordPolicy Password, SessionPolicy Session, MfaPolicy Mfa, TimeSpan ActivationTokenLifetime, TimeSpan ChallengeLifetime);

/// <summary>
/// Identity set-up of a customer database, run after its migrations (provisioning and the fleet migrator): settings
/// definitions and the customer's field key.
/// </summary>
public static class IdentitySetup
{
    public static async Task EnsureAsync(NpgsqlConnection owner, BuildingBlocks.Application.Tenancy.ISecretStore secrets, string tenantCode, CancellationToken cancellationToken)
    {
        await IdentitySettings.SeedAsync(owner, cancellationToken).ConfigureAwait(false);
        await TenantFieldKeys.EnsureAsync(secrets, tenantCode, cancellationToken).ConfigureAwait(false);
    }
}
