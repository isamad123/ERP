using System.Text.Json;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Numbering;

/// <summary>
/// Settings numbering reads (slice S1.08 rulings Q5, P3, P4): the month a company's fiscal year starts in
/// (<c>fiscal_yearly</c> series and <c>{fy}</c>) and the time zone that decides "today" for a document without a date.
/// Declared in the kernel settings manifest since slice S1.09a and resolved by the one settings resolver, exactly as the
/// settings centre shows them (S1.09a review finding 1, CR-061): the company's value, else the customer's, else the
/// default of the company's country (the pack layer, <see cref="CountryPackDefaults"/>; Pakistan: July), else the product
/// default. A stored value the setting does not accept (for example an unknown time zone) is skipped like any other.
/// </summary>
public static class NumberingSettings
{
    public const string FiscalYearStartMonth = KernelSettingKeys.CfgNumberingFiscalYearStartMonth;
    public const string TimeZone = KernelSettingKeys.OrgTimeZone;

    private static readonly string[] Keys = [FiscalYearStartMonth, TimeZone];

    /// <summary>The values that apply to one company (or to tenant-wide documents when <c>companyId</c> is null).</summary>
    public sealed record Values(int FiscalYearStartMonth, string TimeZone, string? CompanyCode);

    /// <summary>Effective values for a company; a stored value that is not valid falls back to the next layer.</summary>
    public static async Task<Values> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid? companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string? code = null;
        if (companyId is not null)
        {
            await using var command = Sql.Command(connection, transaction, "select code from org.companies where id = $1", [companyId]);
            code = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        var resolved = await SettingsStore.ResolveAsync(connection, transaction, Keys, companyId, null, default, cancellationToken).ConfigureAwait(false);
        var month = resolved.TryGetValue(FiscalYearStartMonth, out var m) && m.Value.ValueKind == JsonValueKind.Number ? m.Value.GetInt32() : 1;
        var zone = resolved.TryGetValue(TimeZone, out var z) && z.Value.ValueKind == JsonValueKind.String && IsZone(z.Value.GetString()!) ? z.Value.GetString()! : "Asia/Karachi";
        return new Values(month, zone, code);
    }

    /// <summary>Today's date in the time zone (the injected clock gives the instant).</summary>
    public static DateOnly Today(DateTimeOffset utcNow, string timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);

    private static bool IsZone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);
}
