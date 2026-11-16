using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Audit;

/// <summary>
/// The audit writer for every EF Core write (FR-KRN-AUD-01, GHR-09; slice S1.07 build item 1). Before saving it reads the
/// change tracker (create, update with field-level before and after, soft delete, status change) of every audited table;
/// after the rows are written, and before commit, it appends the entries through <c>aud.append_changes</c> in the same
/// transaction, so the chain lock is held only for that last moment (ruling Q2). When the caller has no transaction this
/// interceptor opens one, so the business change and its entries commit together or not at all.
/// </summary>
/// <remarks>
/// Registered once (singleton); per-save state is kept beside the context. The request's services (clock, actor, field
/// key, HR directory) come from the context's application service provider (the request scope).
/// </remarks>
public sealed class ChangeAuditInterceptor(AuditCatalog catalog) : SaveChangesInterceptor
{
    /// <summary>Standard columns the entry itself already carries (who and when) or that only machinery changes.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "id", "created_at", "created_by", "updated_at", "updated_by", "row_version", "deleted_at", "deleted_by",
    };

    private readonly ConditionalWeakTable<DbContext, Pending> _pending = [];

    private sealed class Pending
    {
        public List<Captured> Rows { get; } = [];

        public IDbContextTransaction? Owned { get; set; }
    }

    private sealed record Captured(AuditedTable Table, string Action, Guid EntityId, Guid? CompanyId, Guid? PersonId, Guid? EmploymentId, List<(string Column, bool Redacted, JsonNode? Before, JsonNode? After)> Fields);

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
        {
            var pending = Capture(context);
            _pending.AddOrUpdate(context, pending);
            if (pending.Rows.Count > 0 && context.Database.CurrentTransaction is null)
            {
                pending.Owned = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        throw new NotSupportedException("Audited contexts save asynchronously (SaveChangesAsync), so the audit write joins the same transaction.");

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context || !_pending.TryGetValue(context, out var pending))
        {
            return result;
        }

        _pending.Remove(context);
        try
        {
            if (pending.Rows.Count > 0)
            {
                await WriteAsync(context, pending.Rows, cancellationToken).ConfigureAwait(false);
            }

            if (pending.Owned is { } owned)
            {
                await owned.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (pending.Owned is { } owned)
            {
                await owned.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (pending.Owned is { } owned)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }

        return result;
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await EndAsync(eventData.Context).ConfigureAwait(false);
    }

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await EndAsync(eventData.Context).ConfigureAwait(false);
    }

    private async Task EndAsync(DbContext? context)
    {
        if (context is not null && _pending.TryGetValue(context, out var pending))
        {
            _pending.Remove(context);
            if (pending.Owned is { } owned)
            {
                await owned.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private Pending Capture(DbContext context)
    {
        var pending = new Pending();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var table = catalog.Find(entry.Metadata.ClrType)
                ?? throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} is not in the audit catalogue; regenerate the code (S1.07).");
            if (!table.Audited)
            {
                continue;
            }

            if (CaptureRow(entry, table) is { } row)
            {
                pending.Rows.Add(row);
            }
        }

        return pending;
    }

    private static Captured? CaptureRow(EntityEntry entry, AuditedTable table)
    {
        var properties = entry.Properties.Select(p => (Property: p, Column: p.Metadata.GetColumnName())).ToList();
        object? Current(string? column) => column is null ? null : properties.Find(p => p.Column == column).Property?.CurrentValue;
        object? Original(string column) => properties.Find(p => p.Column == column).Property?.OriginalValue;

        var fields = new List<(string, bool, JsonNode?, JsonNode?)>();
        string action;
        switch (entry.State)
        {
            case EntityState.Added:
                action = "create";
                foreach (var (p, column) in properties.Where(x => !Skipped.Contains(x.Column) && x.Property.CurrentValue is not null))
                {
                    fields.Add((column, p.CurrentValue is byte[], null, ToJson(p.CurrentValue)));
                }

                break;
            case EntityState.Deleted:
                action = "delete";
                foreach (var (p, column) in properties.Where(x => !Skipped.Contains(x.Column)))
                {
                    fields.Add((column, p.OriginalValue is byte[], ToJson(p.OriginalValue), null));
                }

                break;
            default:
                var changed = properties.Where(x => !Skipped.Contains(x.Column) && !SameValue(x.Property.OriginalValue, x.Property.CurrentValue)).ToList();
                if (changed.Count == 0)
                {
                    return null;
                }

                action = changed.Exists(c => c.Column == "is_deleted" && Equals(c.Property.CurrentValue, true)) ? "delete"
                    : changed.Exists(c => c.Column == "status") ? "status"
                    : "update";
                foreach (var (p, column) in changed)
                {
                    fields.Add((column, p.CurrentValue is byte[] || p.OriginalValue is byte[], ToJson(p.OriginalValue), ToJson(p.CurrentValue)));
                }

                break;
        }

        var key = entry.State == EntityState.Deleted ? Original(table.KeyColumn!) : Current(table.KeyColumn);
        return new Captured(table, action, (Guid)key!, Current(table.CompanyColumn) as Guid?, Current(table.PersonColumn) as Guid?, Current(table.EmploymentColumn) as Guid?, fields);
    }

    private static async Task WriteAsync(DbContext context, List<Captured> rows, CancellationToken cancellationToken)
    {
        var services = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
            ?? throw new InvalidOperationException("The audited context has no application service provider (register it with AddDbContext).");
        var clock = services.GetRequiredService<IClock>();
        var audit = services.GetService<IAuditContext>();
        var directory = services.GetService<IWorkforceDirectory>();
        var key = await services.GetRequiredService<IFieldEncryptorProvider>().GetAsync(cancellationToken).ConfigureAwait(false);
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();
        var protector = new AuditValueProtector(connection, transaction, key);
        var now = clock.UtcNow;
        var actor = audit?.ActorUserId;
        var entries = new List<ChangeEntry>(rows.Count);
        foreach (var row in rows)
        {
            var subject = row.PersonId;
            if (subject is null && row.EmploymentId is { } employment && directory is not null && row.Fields.Exists(f => IsPersonal(row.Table.ClassOf(f.Column))))
            {
                var persons = await directory.PersonsOfAsync([employment], cancellationToken).ConfigureAwait(false);
                subject = persons.Count == 1 ? persons[0] : null;
            }

            var changes = new JsonArray();
            foreach (var (column, redacted, before, after) in row.Fields)
            {
                var fieldClass = row.Table.ClassOf(column);
                changes.Add(await protector.FieldAsync(row.Table.Table, column, fieldClass, redacted, before, after, row.Action == "create", row.Action == "delete" && after is null, IsPersonal(fieldClass) ? subject : null, cancellationToken).ConfigureAwait(false));
            }

            var usesSubject = row.Fields.Exists(f => IsPersonal(row.Table.ClassOf(f.Column))) && subject is not null;
            entries.Add(new ChangeEntry(Guid.CreateVersion7(now), now, actor, audit?.ActorKind ?? ActorKinds.System, row.Table.Table, row.EntityId, row.CompanyId,
                row.Action, changes, usesSubject ? subject : null, audit?.CorrelationId, audit?.IpAddress, audit?.Source ?? AuditSources.System, actor ?? BaselineSeed.SystemUserId));
        }

        await AuditDb.AppendChangesAsync(connection, transaction, entries, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPersonal(string? fieldClass) => fieldClass is "personal" or "restricted";

    private static bool SameValue(object? a, object? b) => (a, b) switch
    {
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (JsonDocument x, JsonDocument y) => JsonElement.DeepEquals(x.RootElement, y.RootElement),
        _ => FieldShaping.Same(a, b),
    };

    /// <summary>A column value as JSON (dates as ISO text, numbers exact, jsonb as itself); null stays null.</summary>
    internal static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        byte[] => null,
        JsonDocument d => JsonNode.Parse(d.RootElement.GetRawText()),
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        System.Net.IPAddress ip => JsonValue.Create(ip.ToString()),
        _ => JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web),
    };
}

/// <summary>
/// Q9: an attempt through the application to change, delete or insert audit rows directly is refused by the database
/// (no privilege, or the append-only trigger) and rolled back; this interceptor then records a critical security event in a
/// new transaction. An attempt made directly in the database shows in the PostgreSQL log and is caught by the chain check.
/// </summary>
public sealed class AuditTamperInterceptor : DbCommandInterceptor
{
    public override async Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context && TamperAttempt.Matches(eventData.Exception, command.CommandText))
        {
            var services = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
            if (services is not null)
            {
                await TamperAttempt.RecordAsync(services, command.CommandText, eventData.Exception, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Recognises and records refused writes to audit tables (Q9).</summary>
public static class TamperAttempt
{
    public const string EventType = "aud.tamper_attempt";

    public static bool Matches(Exception? error, string? sql) =>
        error is PostgresException pg
        && (pg.SqlState == PostgresErrorCodes.InsufficientPrivilege || pg.SqlState == "ER001")
        && sql is not null
        && System.Text.RegularExpressions.Regex.IsMatch(sql, @"\b(update|delete\s+from|insert\s+into|truncate(\s+table)?)\s+(only\s+)?""?aud""?\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs one statement of hand-written SQL (identity and access helpers, risk R-012): a refused write to an audit table
    /// is recorded as a critical security event on a fresh connection of the same login, then the error is passed on.
    /// </summary>
    public static async Task<T> GuardAsync<T>(NpgsqlConnection connection, string sql, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PostgresException error) when (Matches(error, sql))
        {
            await RecordOnCloneAsync(connection, sql, error).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RecordOnCloneAsync(NpgsqlConnection connection, string sql, PostgresException error)
    {
        try
        {
            await using var fresh = (NpgsqlConnection)((ICloneable)connection).Clone();
            await fresh.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            var request = AuditAmbient.Current;
            var details = new JsonObject
            {
                ["statement"] = sql.Length <= 500 ? sql : sql[..500],
                ["sql_state"] = error.SqlState,
                ["correlation_id"] = request?.CorrelationId,
                ["ip_address"] = request?.IpAddress,
            };
            // No injected clock reaches these static helpers: the event takes the database's clock.
            await IdentityDb.ExecuteAsync(fresh, null, "select aud.append_security_event(gen_random_uuid(), clock_timestamp(), $1, null, 'aud', 'critical', $2, $3)", CancellationToken.None,
                EventType, new JsonPayload(details.ToJsonString()), BaselineSeed.SystemUserId).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Recording is best effort here: the refusal itself stands and is passed on to the caller.
        }
    }

    /// <summary>Writes the critical event on its own connection (the failed transaction is gone).</summary>
    public static async Task RecordAsync(IServiceProvider services, string sql, Exception error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var tenant = services.GetRequiredService<ITenantConnectionProvider>();
        var clock = services.GetRequiredService<IClock>();
        var audit = services.GetService<IAuditContext>();
        var now = clock.UtcNow;
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var details = new JsonObject
        {
            ["statement"] = sql.Length <= 500 ? sql : sql[..500],
            ["sql_state"] = (error as PostgresException)?.SqlState,
            ["correlation_id"] = audit?.CorrelationId,
            ["ip_address"] = audit?.IpAddress,
        };
        await AuditDb.AppendSecurityEventAsync(connection, null, new SecurityEventRecord(now, EventType, audit?.ActorUserId?.ToString(), "aud", Severities.Critical, details, audit?.ActorUserId ?? BaselineSeed.SystemUserId), cancellationToken).ConfigureAwait(false);
    }
}
