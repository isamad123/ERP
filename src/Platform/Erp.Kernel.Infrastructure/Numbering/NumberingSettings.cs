using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Numbering;

/// <summary>
/// Settings numbering reads (slice S1.08 rulings Q5, P3, P4): the month a company's fiscal year starts in
/// (<c>fiscal_yearly</c> series and <c>{fy}</c>) and the time zone that decides "today" for a document without a date.
/// Declared in the kernel settings manifest since slice S1.09a (same keys, defaults, rules and texts) and resolved by the
/// one settings resolver: the company's value, else the customer's; when neither is set (the product layer), the default
/// for the company's country (<c>country-defaults.json</c>), else the product default. A stored time zone that is not a
/// known IANA zone is skipped like any value the setting no longer accepts.
/// </summary>
public static class NumberingSettings
{
    public const string FiscalYearStartMonth = KernelSettingKeys.CfgNumberingFiscalYearStartMonth;
    public const string TimeZone = KernelSettingKeys.OrgTimeZone;

    private static readonly Lazy<CountryDefaults> Defaults = new(LoadDefaults);

    private static readonly string[] Keys = [FiscalYearStartMonth, TimeZone];

    /// <summary>The values that apply to one company (or to tenant-wide documents when <c>companyId</c> is null).</summary>
    public sealed record Values(int FiscalYearStartMonth, string TimeZone, string? CompanyCode);

    /// <summary>Effective values for a company; a stored value that is not valid falls back to the next layer.</summary>
    public static async Task<Values> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid? companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string? country = null;
        string? code = null;
        if (companyId is not null)
        {
            await using var command = Sql.Command(connection, transaction, "select country_code, code from org.companies where id = $1", [companyId]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                country = reader.GetString(0).Trim();
                code = reader.GetString(1);
            }
        }

        var definitions = await SettingsStore.DefinitionsAsync(connection, transaction, Keys, cancellationToken).ConfigureAwait(false);
        var stored = (await SettingsStore.ValuesAsync(connection, transaction, Keys, companyId, [], null, cancellationToken).ConfigureAwait(false))
            .Where(v => v.Key != TimeZone || v.Value.Value.ValueKind != JsonValueKind.String || IsZone(v.Value.Value.GetString()!))
            .ToLookup(v => v.Key, StringComparer.Ordinal);
        var fallback = country is not null && Defaults.Value.Countries.TryGetValue(country, out var c) ? c : Defaults.Value.Product;
        ResolvedSetting? Resolve(string key) =>
            definitions.TryGetValue(key, out var d) ? SettingResolution.Resolve(d.Definition, stored[key].Select(v => v.Value)) : null;
        var month = Resolve(FiscalYearStartMonth) is { Layer: not SettingLayers.Product } m ? m.Value.GetInt32() : fallback.FiscalYearStartMonth;
        var zone = Resolve(TimeZone) is { Layer: not SettingLayers.Product } z ? z.Value.GetString()! : fallback.TimeZone;
        return new Values(month, zone, code);
    }

    /// <summary>Today's date in the time zone (the injected clock gives the instant).</summary>
    public static DateOnly Today(DateTimeOffset utcNow, string timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);

    private static bool IsZone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    private static CountryDefaults LoadDefaults()
    {
        using var stream = typeof(NumberingSettings).Assembly.GetManifestResourceStream("Erp.Kernel.Infrastructure.Numbering.country-defaults.json")
            ?? throw new InvalidOperationException("country-defaults.json is not embedded.");
        return JsonSerializer.Deserialize<CountryDefaults>(stream) ?? throw new InvalidDataException("country-defaults.json is empty.");
    }

    private sealed class CountryDefaults
    {
        [JsonPropertyName("product")]
        public required CountryDefault Product { get; init; }

        [JsonPropertyName("countries")]
        public required Dictionary<string, CountryDefault> Countries { get; init; }
    }

    private sealed class CountryDefault
    {
        [JsonPropertyName("fiscal_year_start_month")]
        public int FiscalYearStartMonth { get; init; }

        [JsonPropertyName("time_zone")]
        public required string TimeZone { get; init; }
    }
}
