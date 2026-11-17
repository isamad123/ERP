using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Numbering;

/// <summary>
/// Settings numbering reads (slice S1.08 rulings Q5, P3, P4): the month a company's fiscal year starts in
/// (<c>fiscal_yearly</c> series and <c>{fy}</c>) and the time zone that decides "today" for a document without a date.
/// Registered in cfg.settings whenever a customer database is migrated; values are edited in the settings centre
/// (S1.15). The effective value is the company's, else the customer's, else the default for the company's country
/// (<c>country-defaults.json</c>), else the product default.
/// </summary>
public static class NumberingSettings
{
    public const string FiscalYearStartMonth = "cfg.numbering.fiscal_year_start_month";
    public const string TimeZone = "org.time_zone";

    private static readonly Lazy<CountryDefaults> Defaults = new(LoadDefaults);

    private static readonly string[] Layers = ["tenant", "company"];

    /// <summary>The values that apply to one company (or to tenant-wide documents when <c>companyId</c> is null).</summary>
    public sealed record Values(int FiscalYearStartMonth, string TimeZone, string? CompanyCode);

    public static async Task SeedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var product = Defaults.Value.Product;
        var definitions = new (string Key, string Module, string Type, string Default, string Validation, string En, string Ur)[]
        {
            (FiscalYearStartMonth, "cfg", "int", product.FiscalYearStartMonth.ToString(CultureInfo.InvariantCulture), """{"min":1,"max":12}""",
                "Month the fiscal year starts in (1 = January); used by fiscal-year numbering", "مالی سال کس مہینے سے شروع ہوتا ہے (1 = جنوری)؛ مالی سال کی نمبرنگ کے لیے"),
            (TimeZone, "org", "text", JsonSerializer.Serialize(product.TimeZone), "{}",
                "Time zone of the company (IANA name, for example Asia/Karachi)", "کمپنی کا ٹائم زون (IANA نام، مثلاً Asia/Karachi)"),
        };
        foreach (var d in definitions)
        {
            await using var command = Sql.Command(connection, transaction, """
                insert into cfg.settings (key, module_id, data_type, default_value, validation, allowed_layers, requires_approval, description_i18n)
                values ($1, $2, $3, $4, $5, $6, false, $7)
                on conflict (key) do update set module_id = excluded.module_id, data_type = excluded.data_type, default_value = excluded.default_value,
                    validation = excluded.validation, allowed_layers = excluded.allowed_layers, description_i18n = excluded.description_i18n
                """, [d.Key, d.Module, d.Type, null, null, Layers, null]);
            command.Parameters[3] = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = d.Default };
            command.Parameters[4] = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = d.Validation };
            command.Parameters[6] = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(new Dictionary<string, string> { ["en"] = d.En, ["ur"] = d.Ur }) };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Effective values for a company; a stored value that is not valid falls back to the next layer.</summary>
    public static async Task<Values> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid? companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string? country = null;
        string? code = null;
        var month = new List<JsonElement>();
        var zone = new List<JsonElement>();
        await using (var command = Sql.Command(connection, transaction, """
            select k.key, c.value, t.value, co.country_code, co.code
            from (values ($1::text), ($2::text)) as k(key)
            left join org.companies co on co.id = $3::uuid
            left join cfg.setting_values c on c.setting_key = k.key and c.layer = 'company' and c.scope_id = $3::uuid
            left join cfg.setting_values t on t.setting_key = k.key and t.layer = 'tenant' and t.scope_id is null
            """, [FiscalYearStartMonth, TimeZone, companyId]))
        {
            command.Parameters[2].NpgsqlDbType = NpgsqlDbType.Uuid;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var target = reader.GetString(0) == FiscalYearStartMonth ? month : zone;
                for (var i = 1; i <= 2; i++)
                {
                    if (!reader.IsDBNull(i))
                    {
                        target.Add(JsonDocument.Parse(reader.GetString(i)).RootElement.Clone());
                    }
                }

                country ??= reader.IsDBNull(3) ? null : reader.GetString(3).Trim();
                code ??= reader.IsDBNull(4) ? null : reader.GetString(4);
            }
        }

        var fallback = country is not null && Defaults.Value.Countries.TryGetValue(country, out var c) ? c : Defaults.Value.Product;
        var m = month.Where(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) && x is >= 1 and <= 12).Select(v => v.GetInt32()).FirstOrDefault(fallback.FiscalYearStartMonth);
        var z = zone.Where(v => v.ValueKind == JsonValueKind.String && IsZone(v.GetString()!)).Select(v => v.GetString()!).FirstOrDefault(fallback.TimeZone);
        return new Values(m, z, code);
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
