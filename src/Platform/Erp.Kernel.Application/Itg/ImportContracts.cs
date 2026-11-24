using System.Text.Json.Nodes;

namespace Erp.Kernel.Application.Itg;

/// <summary>
/// An import type a module declares in <c>Access/imports.json</c> (slice S1.16a, D-10, CR-072): the table it writes, the
/// columns of its template, how an existing record is found ("create or update"), the permission it needs and the writer
/// that applies a row. <see cref="PostCommit"/> names a step done after a chunk is saved (<c>invite</c>: the invitation e-mail of a new user). A writer is the generated one of the table (the same code as manual entry) unless the type names its
/// own <see cref="Writer"/> (org units: the unit and its place in the hierarchy; users: the invitation).
/// </summary>
public sealed record ImportTypeDefinition(
    string Code,
    string Module,
    string Table,
    IReadOnlyDictionary<string, string> Name,
    string Permission,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<ImportColumnDefinition> Columns,
    string? Writer,
    bool CanUpdate,
    string? PostCommit = null,
    string? ReadPermission = null,
    string? RollbackPermission = null,
    IReadOnlyList<string>? UniqueColumns = null,
    IReadOnlyList<string>? Writes = null,
    IReadOnlyList<string>? RollbackDependents = null)
{
    /// <summary>The tables a row of this type may write: the type's table and the tables its writer reports besides (org units: their links). A rollback deletes nothing outside this list (S1.16a review finding 1).</summary>
    public IReadOnlyList<string> WrittenTables => Writes is { Count: > 0 } ? Writes : [Table];

    /// <summary>The permission a person needs to read other people's batches of this type (their problem files hold the original rows).</summary>
    public string ReadRight => ReadPermission ?? Permission;

    /// <summary>The permission a person needs to roll a batch of this type back (the right to delete what it created).</summary>
    public string RollbackRight => RollbackPermission ?? Permission;
}

/// <summary>
/// One column of an import type. <see cref="Type"/> is text, number, date, bool or option. <see cref="Lookup"/> names the
/// table whose <c>code</c> the cell holds (the engine turns it into the id in <see cref="Field"/>, for example
/// <c>org.companies</c> into <c>companyId</c>); <see cref="Options"/> lists the allowed values of an option column.
/// </summary>
public sealed record ImportColumnDefinition(
    string Name,
    string Field,
    string Type,
    bool Required,
    IReadOnlyDictionary<string, string> Label,
    string? Lookup,
    IReadOnlyList<string>? Options,
    string? Example);

/// <summary>A row as the engine hands it to a writer (<see cref="DryRun"/>: the validation run, whose transaction is rolled back and restarted every few hundred rows; <see cref="KnownKeys"/>: the keys of rows that were fine earlier in the run, which a dry run no longer sees in the database): field values by the write request's property names (camelCase), after mapping, transformation and lookups.</summary>
public sealed record ImportRow(int RowNumber, JsonObject Values, bool DryRun = false, IReadOnlySet<string>? KnownKeys = null)
{
    /// <summary>The key text of a record as <see cref="KnownKeys"/> holds it: each part as lower-case JSON, joined with <c>|</c>.</summary>
    public static string Key(params string[] parts) =>
        string.Join('|', (parts ?? throw new ArgumentNullException(nameof(parts))).Select(p => JsonValue.Create(p)!.ToJsonString().ToLowerInvariant()));
}

/// <summary>A problem found in a row: the field (null for the whole row) and a localization key or plain message.</summary>
public sealed record ImportRowError(string? Field, string Message);

/// <summary>What a writer did with one row. <see cref="Action"/> is <c>created</c> or <c>updated</c>; <see cref="Records"/> lists every record written (table, id, row version) so a rollback can find them.</summary>
public sealed record ImportRowOutcome(IReadOnlyList<ImportRowError> Errors, string? Action, IReadOnlyList<ImportWrittenRecord> Records)
{
    public bool Ok => Errors.Count == 0;

    public static ImportRowOutcome Failed(params ImportRowError[] errors) => new(errors, null, []);
}

public sealed record ImportWrittenRecord(string Table, Guid Id, int? RowVersion);

/// <summary>
/// Applies one row through the same rules as manual entry: permission, data scope, field security, custom fields,
/// numbering and the table's own checks (FR-KRN-INT-03). The engine calls it inside a transaction: the validation run
/// rolls it back, the commit keeps it. A writer never throws for a bad row; it returns the errors.
/// </summary>
public interface IImportRowWriter
{
    /// <summary>The import type code this writer serves, or the table name for the generated writer of a table.</summary>
    string Code { get; }

    /// <summary>Applies the row. <paramref name="existingId"/> is the record the row's key found when the template allows updates; null creates. A row marked <see cref="ImportRow.DryRun"/> must have no effect outside the open transaction (no e-mails, no other connections).</summary>
    Task<ImportRowOutcome> WriteAsync(ImportRow row, Guid? existingId, CancellationToken cancellationToken);
}

/// <summary>A refusal of the import centre with the HTTP status of its answer; <see cref="ErrorKey"/> is a localization key (<c>errors.import.*</c>).</summary>
public sealed class ImportException(string errorKey, int status = 400, string? detail = null) : Exception(errorKey)
{
    public string ErrorKey { get; } = errorKey;

    public int Status { get; } = status;

    public string? Detail { get; } = detail;
}

public sealed record ImportColumnView(string Name, string Field, string Type, bool Required, IReadOnlyDictionary<string, string> Label, string? Lookup, IReadOnlyList<string>? Options, string? Example);

/// <summary>An import type as the centre shows it; <see cref="CanRun"/> says whether the caller holds the permission the type needs.</summary>
public sealed record ImportTypeView(string Code, string Module, IReadOnlyDictionary<string, string> Name, string Permission, bool CanUpdate, bool CanRun, IReadOnlyList<ImportColumnView> Columns);

public sealed record ImportTemplateView(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string EntityType, string Format, string Mode, bool IsDefault, int Version, bool IsActive, long RowVersion, JsonNode? Mapping);

/// <summary>The body of a template create (code, name, entity type, format, mode, mapping) or update (name, mode, mapping and the row version read).</summary>
public sealed record ImportTemplateWrite(string? Code, Dictionary<string, string>? Name, string? EntityType, string? Format, string? Mode, JsonNode? Mapping, long? RowVersion);

public sealed record ImportBatchView(
    Guid Id,
    Guid TemplateId,
    IReadOnlyDictionary<string, string> TemplateName,
    string EntityType,
    string FileName,
    string Status,
    string Mode,
    int? TotalRows,
    int? OkRows,
    int? ErrorRows,
    int? CreatedRows,
    int? UpdatedRows,
    decimal? ProgressPct,
    Guid? JobRunId,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? CommittedAt,
    DateTimeOffset? RolledBackAt,
    bool CanCommit,
    bool CanRollback,
    string? RollbackBlockedKey);

public sealed record ImportErrorView(int RowNo, string? Field, string Message, string Phase);

public sealed record ImportPage<T>(IReadOnlyList<T> Items, string? Next);

public sealed record ImportSampleRow(int RowNo, JsonNode? Values);

public sealed record ImportTestResult(int TotalRows, int ErrorRows, IReadOnlyList<ImportErrorView> Errors, IReadOnlyList<ImportSampleRow> Sample);

/// <summary>A file the centre hands back (a template or the error file).</summary>
public sealed record ImportFileContent(string FileName, string ContentType, byte[] Content);

/// <summary>The import centre (slice S1.16a, FR-ADM-DAT-01, FR-ADM-INT-04): types, templates, batches (upload, check, commit, roll back) and their problems.</summary>
public interface IImportService
{
    Task<IReadOnlyList<ImportTypeView>> TypesAsync(CancellationToken cancellationToken);

    /// <summary>The largest upload this person may make (setting <c>doc.upload.max_mb</c>); the endpoint limits the request body to it before reading it (S1.16a review finding 9).</summary>
    Task<long> UploadLimitAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ImportTemplateView>> TemplatesAsync(string? type, CancellationToken cancellationToken);

    Task<ImportTemplateView> CreateTemplateAsync(ImportTemplateWrite request, CancellationToken cancellationToken);

    Task<ImportTemplateView> UpdateTemplateAsync(Guid id, ImportTemplateWrite request, CancellationToken cancellationToken);

    Task<ImportFileContent> TemplateFileAsync(Guid id, string? format, CancellationToken cancellationToken);

    Task<ImportTestResult> TestTemplateAsync(Guid id, string fileName, byte[] content, CancellationToken cancellationToken);

    Task<ImportBatchView> UploadAsync(Guid templateId, string fileName, byte[] content, CancellationToken cancellationToken);

    Task<ImportBatchView> ValidateAsync(Guid batchId, CancellationToken cancellationToken);

    Task<ImportBatchView> CommitAsync(Guid batchId, bool skipErrors, CancellationToken cancellationToken);

    Task<ImportBatchView> RollbackAsync(Guid batchId, string? reason, CancellationToken cancellationToken);

    Task<ImportBatchView> GetAsync(Guid batchId, CancellationToken cancellationToken);

    Task<ImportPage<ImportBatchView>> ListAsync(string? type, string? status, string? cursor, int limit, CancellationToken cancellationToken);

    Task<ImportPage<ImportErrorView>> ErrorsAsync(Guid batchId, int limit, string? cursor, CancellationToken cancellationToken);

    Task<ImportFileContent> ErrorFileAsync(Guid batchId, CancellationToken cancellationToken);
}
