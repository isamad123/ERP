using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

/// <summary>Baseline data of a new customer database (baseline-seed.json, embedded in the assembly).</summary>
public sealed class BaselineSeed
{
    /// <summary>
    /// Id of the reserved system user (iam.users is_system) in every customer database. It is the actor of the migrator
    /// and of provisioning (created_by), so it is the same, well-known UUIDv7-shaped value everywhere.
    /// </summary>
    public static readonly Guid SystemUserId = new("019a8a00-0000-7000-8000-000000000001");

    private static readonly Lazy<BaselineSeed> Instance = new(Load);

    [JsonPropertyName("countries")]
    public required Dictionary<string, Dictionary<string, string>> Countries { get; init; }

    [JsonPropertyName("currencies")]
    public required Dictionary<string, CurrencySeed> Currencies { get; init; }

    /// <summary>Code of the baseline role the first administrator receives (defined in the kernel's baseline-roles.json).</summary>
    [JsonPropertyName("admin_role")]
    public required string AdminRole { get; init; }

    [JsonPropertyName("system_user")]
    public required SystemUserSeed SystemUser { get; init; }

    [JsonPropertyName("user_defaults")]
    public required UserDefaults UserDefaultsValue { get; init; }

    public static BaselineSeed Current => Instance.Value;

    private static BaselineSeed Load()
    {
        using var stream = typeof(BaselineSeed).Assembly.GetManifestResourceStream("Erp.Kernel.Infrastructure.ControlPlane.Provisioning.baseline-seed.json")
            ?? throw new InvalidOperationException("baseline-seed.json is not embedded.");
        return JsonSerializer.Deserialize<BaselineSeed>(stream) ?? throw new InvalidDataException("baseline-seed.json is empty.");
    }
}

public sealed class CurrencySeed
{
    [JsonPropertyName("name")]
    public required Dictionary<string, string> Name { get; init; }

    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("decimals")]
    public required int Decimals { get; init; }
}

public sealed class SystemUserSeed
{
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("language")]
    public required string Language { get; init; }

    [JsonPropertyName("time_zone")]
    public required string TimeZone { get; init; }
}

public sealed class UserDefaults
{
    [JsonPropertyName("language")]
    public required string Language { get; init; }

    [JsonPropertyName("time_zone")]
    public required string TimeZone { get; init; }
}
