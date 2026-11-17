using System.Data.Common;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Entitlements;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Entitlements;

/// <summary>
/// What the kernel's limits count (slice S1.09 ruling Q7), as SQL read in the caller's transaction, shared by the limit
/// guard and the daily usage readings (ruling Q8): named users as ruled in S1.05 (P4: active and locked users, not the
/// system user); concurrent users as the distinct users with a session open now (ruling B2); companies as active,
/// not-deleted companies (ruling B5).
/// </summary>
public static class LimitSql
{
    public const string NamedUsers = "select count(*) from iam.users where not is_system and not is_deleted and status in ('active', 'locked')";

    public const string NamedUserHeld = "select exists (select 1 from iam.users where id = $1 and not is_system and not is_deleted and status in ('active', 'locked'))";

    public const string ConcurrentUsers = "select count(distinct user_id) from iam.sessions where revoked_at is null and expires_at > $1";

    public const string ConcurrentHeld = "select exists (select 1 from iam.sessions where user_id = $2 and revoked_at is null and expires_at > $1)";

    public const string Companies = "select count(*) from org.companies where status = 'active' and not is_deleted";

    public const string CompanyHeld = "select exists (select 1 from org.companies where id = $1 and status = 'active' and not is_deleted)";

    /// <summary>Storage in bytes: the customer database plus the stored files (ruling Q8).</summary>
    public const string StorageBytes = "select pg_database_size(current_database()) + coalesce((select sum(size_bytes) from doc.files where status <> 'deleted'), 0)";

    internal static async Task<long> CountAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken, params object[] values)
    {
#pragma warning disable CA2100 // constant SQL text of this class; values are parameters
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static async Task<bool> HeldAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken, params object[] values) =>
        await CountAsync(connection, transaction, "select (" + sql + ")::int", cancellationToken, values).ConfigureAwait(false) == 1;
}

/// <summary>Named users (FR-KRN-IAM-18, FR-ADM-USR-08): checked when a user is activated by themself or by an administrator.</summary>
public sealed class NamedUserCounter : ILimitCounter
{
    public string LimitKey => LimitKeys.NamedUsers;

    public async Task<LimitCount> CountAsync(DbTransaction transaction, Guid? holder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var c = transaction.Connection!;
        var used = await LimitSql.CountAsync(c, transaction, LimitSql.NamedUsers, cancellationToken).ConfigureAwait(false);
        return new LimitCount(used, holder is { } h && await LimitSql.HeldAsync(c, transaction, LimitSql.NamedUserHeld, cancellationToken, h).ConfigureAwait(false));
    }
}

/// <summary>Concurrent users (FR-ADM-USR-08): checked when a sign-in opens a session; a user already signed in may sign in again.</summary>
public sealed class ConcurrentUserCounter(IClock clock) : ILimitCounter
{
    public string LimitKey => LimitKeys.ConcurrentUsers;

    public async Task<LimitCount> CountAsync(DbTransaction transaction, Guid? holder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var c = transaction.Connection!;
        var now = clock.UtcNow;
        var used = await LimitSql.CountAsync(c, transaction, LimitSql.ConcurrentUsers, cancellationToken, now).ConfigureAwait(false);
        return new LimitCount(used, holder is { } h && await LimitSql.HeldAsync(c, transaction, LimitSql.ConcurrentHeld, cancellationToken, now, h).ConfigureAwait(false));
    }
}

/// <summary>Companies (FR-KRN-IAM-18): checked when a company is created active or made active again.</summary>
public sealed class CompanyCounter : ILimitCounter
{
    public string LimitKey => LimitKeys.Companies;

    public async Task<LimitCount> CountAsync(DbTransaction transaction, Guid? holder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var c = transaction.Connection!;
        var used = await LimitSql.CountAsync(c, transaction, LimitSql.Companies, cancellationToken).ConfigureAwait(false);
        return new LimitCount(used, holder is { } h && await LimitSql.HeldAsync(c, transaction, LimitSql.CompanyHeld, cancellationToken, h).ConfigureAwait(false));
    }
}
