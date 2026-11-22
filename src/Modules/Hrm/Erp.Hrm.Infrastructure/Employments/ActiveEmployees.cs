using System.Data.Common;
using System.Globalization;
using Erp.Kernel.Contracts.Entitlements;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// What the active employee limit counts (slice S1.20 Build items 5 and 9; FR-KRN-ENT-05, S1.09b ruling Q8). The monthly meter
/// counts employments that are active or on notice. The limit guard at hire also counts employments waiting for their joining
/// date, so hiring ahead of time cannot pass the limit and then exceed it when the daily job makes them active.
/// </summary>
public static class ActiveEmployeeSql
{
    public const string Active = "select count(*) from hrm.employments where status in ('active', 'on_notice')";

    public const string Seats = "select count(*) from hrm.employments where status in ('pre_joining', 'active', 'on_notice')";

    internal static async Task<long> CountAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct)
    {
#pragma warning disable CA2100 // constant SQL text of this class
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);
#pragma warning restore CA2100
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}

/// <summary>The monthly meter of active employees (S1.09b ruling Q8), read by the kernel's licence loop on the customer database.</summary>
public sealed class ActiveEmployeeCounter : IActiveEmployeeCounter
{
    public Task<long> CountAsync(DbConnection customerDatabase, CancellationToken cancellationToken) =>
        ActiveEmployeeSql.CountAsync(customerDatabase, null, ActiveEmployeeSql.Active, cancellationToken);
}

/// <summary>The seats of the active employee limit (<c>employees.active</c>), checked by the kernel's limit guard at hire.</summary>
public sealed class ActiveEmployeeLimitCounter : ILimitCounter
{
    public string LimitKey => LimitKeys.ActiveEmployees;

    public async Task<LimitCount> CountAsync(DbTransaction transaction, Guid? holder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new LimitCount(await ActiveEmployeeSql.CountAsync(transaction.Connection!, transaction, ActiveEmployeeSql.Seats, cancellationToken).ConfigureAwait(false), false);
    }
}
