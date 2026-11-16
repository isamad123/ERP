namespace Erp.BuildingBlocks.Application.Audit;

/// <summary>
/// How the change log records one table (slice S1.07, generated from the data design into each module's audit catalogue).
/// <see cref="KeyColumn"/> identifies a row (<c>entity_id</c>); <see cref="CompanyColumn"/>, <see cref="PersonColumn"/> and
/// <see cref="EmploymentColumn"/> locate its company and data subject; <see cref="FieldClasses"/> maps column to
/// <c>salary</c>, <c>personal</c> or <c>restricted</c>.
/// </summary>
public sealed record AuditedTable(
    Type EntityType,
    string Table,
    bool Audited,
    string? Reason,
    string? KeyColumn,
    string? CompanyColumn,
    string? PersonColumn,
    string? EmploymentColumn,
    IReadOnlyDictionary<string, string>? FieldClasses)
{
    public string? ClassOf(string column) => FieldClasses is { } classes && classes.TryGetValue(column, out var c) ? c : null;
}

/// <summary>Every table of the modules the host loads, by CLR type (registered by the host from each module's catalogue).</summary>
public sealed class AuditCatalog(IEnumerable<AuditedTable> tables)
{
    private readonly Dictionary<Type, AuditedTable> _byType = tables.ToDictionary(t => t.EntityType);
    private readonly Dictionary<string, AuditedTable> _byName = tables.ToDictionary(t => t.Table, StringComparer.Ordinal);

    public IReadOnlyCollection<AuditedTable> All => _byType.Values;

    public AuditedTable? Find(Type entityType) => _byType.GetValueOrDefault(entityType);

    public AuditedTable? Find(string table) => _byName.GetValueOrDefault(table);
}

/// <summary>Where a change came from (FR-KRN-AUD-01, CR-026): decided by the server, never taken from request input.</summary>
public static class AuditSources
{
    public const string Ui = "ui";
    public const string Api = "api";
    public const string Import = "import";
    public const string Job = "job";
    public const string System = "system";
}

/// <summary>Who acted (aud.change_log.actor_kind).</summary>
public static class ActorKinds
{
    public const string User = "user";
    public const string ApiClient = "api_client";
    public const string Job = "job";
    public const string System = "system";
}

/// <summary>
/// The request's audit context (slice S1.07): one correlation id per request (returned in the <c>X-Correlation-Id</c>
/// answer header), the client address and the source. Outside a request (migrator, jobs) the values describe the process.
/// </summary>
public interface IAuditContext
{
    Guid CorrelationId { get; }

    string? IpAddress { get; }

    string Source { get; }

    /// <summary>The acting user, or null when no person acts (system).</summary>
    Guid? ActorUserId { get; }

    string ActorKind { get; }
}
