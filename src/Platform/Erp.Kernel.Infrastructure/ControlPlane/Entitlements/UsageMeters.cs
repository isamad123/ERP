using System.Globalization;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Entitlements;

/// <summary>One daily reading of a meter (ruling Q8).</summary>
public sealed record UsageReading(string Meter, long Value);

/// <summary>
/// Usage metering (FR-KRN-ENT-06, FR-ADM-OPS-07; slice S1.09 ruling Q8): daily readings kept as one value per customer, meter
/// and month in cp.usage_meters. The monthly value is the highest daily reading for users, employees, companies and storage,
/// and the total for API calls. The usage statement of a month lists each meter, its limit and percentage, and the highest
/// percentage; limits come from the entitlement in force at the end of that month.
/// </summary>
public static class UsageMeters
{
    public const string ActiveUsers = "active_users";
    public const string ActiveEmployees = "active_employees";
    public const string Companies = "companies";
    public const string StorageBytes = "storage_bytes";
    public const string ApiCalls = "api_calls";

    /// <summary>Meter → the limit it is measured against (storage in bytes against <c>storage.gb</c>).</summary>
    public static IReadOnlyList<(string Meter, string LimitKey)> Meters { get; } =
    [
        (ActiveUsers, LimitKeys.NamedUsers),
        (ActiveEmployees, LimitKeys.ActiveEmployees),
        (Companies, LimitKeys.Companies),
        (StorageBytes, LimitKeys.StorageGb),
        (ApiCalls, LimitKeys.ApiCalls),
    ];

    private const long BytesPerGb = 1024L * 1024 * 1024;

    /// <summary>The month (<c>yyyymm</c>) of a customer day.</summary>
    public static string Period(DateOnly day) => day.ToString("yyyyMM", CultureInfo.InvariantCulture);

    /// <summary>Records one day's readings: highest of the month, or added for API calls (the caller reads each day's calls once).</summary>
    public static async Task RecordAsync(NpgsqlConnection cp, NpgsqlTransaction? transaction, Guid tenantId, string period, IEnumerable<UsageReading> readings, DateTimeOffset now, Guid actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readings);
        foreach (var reading in readings)
        {
            var combine = reading.Meter == ApiCalls ? "cp.usage_meters.value + excluded.value" : "greatest(cp.usage_meters.value, excluded.value)";
            await Sql.ExecuteAsync(cp, transaction, $"""
                insert into cp.usage_meters (id, tenant_id, meter_key, period, value, as_of, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, $5, $6, $6, $7, $6, $7)
                on conflict (tenant_id, meter_key, period) do update set value = {combine}, as_of = excluded.as_of,
                    updated_at = excluded.updated_at, updated_by = excluded.updated_by, row_version = cp.usage_meters.row_version + 1
                """, cancellationToken, Guid.CreateVersion7(now), tenantId, reading.Meter, period, reading.Value, now, actor).ConfigureAwait(false);
        }
    }

    /// <summary>The usage statement of one month for a customer code (operator command).</summary>
    public static async Task<UsageStatement> StatementAsync(NpgsqlConnection cp, string tenantCode, string period, CancellationToken cancellationToken)
    {
        var tenantId = await Sql.ScalarAsync<Guid?>(cp, null, "select id from cp.tenants where code = $1", cancellationToken, tenantCode).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {tenantCode} is not registered.");
        return (await StatementsAsync(cp, tenantId, period, period, cancellationToken).ConfigureAwait(false)).Single();
    }

    /// <summary>One statement per month from <paramref name="fromPeriod"/> to <paramref name="toPeriod"/> (both <c>yyyymm</c>, at most 36 months).</summary>
    public static async Task<IReadOnlyList<UsageStatement>> StatementsAsync(NpgsqlConnection cp, Guid tenantId, string fromPeriod, string toPeriod, CancellationToken cancellationToken)
    {
        var from = ParsePeriod(fromPeriod);
        var to = ParsePeriod(toPeriod);
        if (to < from || ((to.Year - from.Year) * 12) + to.Month - from.Month >= 36)
        {
            throw new ArgumentException("errors.licence.usage_range_invalid");
        }

        var values = new Dictionary<(string Period, string Meter), long>();
        await using (var command = Sql.Command(cp, null, "select period, meter_key, value from cp.usage_meters where tenant_id = $1 and period between $2 and $3", [tenantId, Period(from), Period(to)]))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
            }
        }

        var statements = new List<UsageStatement>();
        for (var month = from; month <= to; month = month.AddMonths(1))
        {
            var period = Period(month);
            var limits = await LimitsAtAsync(cp, tenantId, month.AddMonths(1).AddDays(-1), cancellationToken).ConfigureAwait(false);
            var lines = new List<UsageLine>();
            foreach (var (meter, limitKey) in Meters)
            {
                if (!values.TryGetValue((period, meter), out var value))
                {
                    continue;
                }

                long? limit = limits.TryGetValue(limitKey, out var l) ? l : null;
                var divisor = meter == StorageBytes ? limit * BytesPerGb : limit;
                int? percent = divisor is > 0 ? (int)Math.Min(999, value * 100 / divisor.Value) : null;
                lines.Add(new UsageLine(meter, value, limitKey, limit, percent));
            }

            statements.Add(new UsageStatement(period, lines, lines.Max(x => x.Percent)));
        }

        return statements;
    }

    private static DateOnly ParsePeriod(string period) =>
        period is { Length: 6 } && DateOnly.TryParseExact(period + "01", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw new ArgumentException("errors.licence.usage_range_invalid");

    /// <summary>
    /// The limits of the entitlement in force on <paramref name="day"/>: the highest version applied and started by then, else the
    /// first one (in force from provisioning, ruling B1).
    /// </summary>
    private static async Task<Dictionary<string, long>> LimitsAtAsync(NpgsqlConnection cp, Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        var limits = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var command = Sql.Command(cp, null, """
            select l.key, l.value::bigint from (
                select claims from cp.entitlements, lateral (select effective_from <= $2 and created_at < ($2 + 1)::timestamptz as started) s
                where tenant_id = $1
                order by s.started desc, case when s.started then -version else version end limit 1) e,
            lateral jsonb_each_text(coalesce(e.claims -> 'limits', '{}'::jsonb)) l
            """, [tenantId, day]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            limits[reader.GetString(0)] = reader.GetInt64(1);
        }

        return limits;
    }
}
