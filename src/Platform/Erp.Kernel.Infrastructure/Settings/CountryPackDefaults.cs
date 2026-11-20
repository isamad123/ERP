using System.Text.Json;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Settings;

/// <summary>
/// Country-pack defaults of settings (slice S1.08 ruling P4; S1.09a review finding 1, CR-061), from the embedded
/// <c>Settings/country-defaults.json</c>. Until country packs exist (S1.23) they are the <b>pack layer</b> of a setting for
/// a company of that country: computed when the company is in context, never stored, and shown by the resolver as layer
/// <c>pack</c> with no "set by". A stored pack value wins over the computed one. With no company in context there is no
/// country and the product default applies, as numbering did since S1.08. Every reader (numbering, the settings centre,
/// <c>ISettings</c>) therefore gives the same value and names the same source.
/// </summary>
public static class CountryPackDefaults
{
    private static readonly Lazy<Dictionary<string, IReadOnlyDictionary<string, JsonElement>>> Countries = new(Load);

    /// <summary>The setting keys any country gives a default for.</summary>
    public static IReadOnlySet<string> Keys { get; } = Countries.Value.Values.SelectMany(c => c.Keys).ToHashSet(StringComparer.Ordinal);

    /// <summary>The computed pack value of <paramref name="key"/> for a company of <paramref name="countryCode"/>, if the country gives one.</summary>
    public static StoredSettingValue? ValueFor(string? countryCode, string key) =>
        countryCode is not null && Countries.Value.TryGetValue(countryCode.Trim(), out var values) && values.TryGetValue(key, out var value)
            ? new StoredSettingValue(SettingLayers.Pack, null, value, null, null)
            : null;

    /// <summary>
    /// Adds the computed pack value to <paramref name="values"/> (the values of one key in a context) when the country gives
    /// one and no pack value is stored.
    /// </summary>
    public static IEnumerable<StoredSettingValue> With(IEnumerable<StoredSettingValue> values, string? countryCode, string key)
    {
        var list = values.ToList();
        if (!list.Exists(v => v.Layer == SettingLayers.Pack) && ValueFor(countryCode, key) is { } computed)
        {
            list.Add(computed);
        }

        return list;
    }

    /// <summary>The country of a company (null when unknown or deleted).</summary>
    public static Task<string?> CountryAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid companyId, CancellationToken ct) =>
        IdentityDb.ScalarAsync<string?>(c, tx, "select btrim(country_code) from org.companies where id = $1 and not is_deleted", ct, companyId);

    private static Dictionary<string, IReadOnlyDictionary<string, JsonElement>> Load()
    {
        using var stream = typeof(CountryPackDefaults).Assembly.GetManifestResourceStream("Erp.Kernel.Infrastructure.Settings.country-defaults.json")
            ?? throw new InvalidOperationException("country-defaults.json is not embedded.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("countries").EnumerateObject().ToDictionary(
            country => country.Name,
            country => (IReadOnlyDictionary<string, JsonElement>)country.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
            StringComparer.Ordinal);
    }
}
