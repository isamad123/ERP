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
    string? PostCommit = null);

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

/// <summary>A row as the engine hands it to a writer (<see cref="DryRun"/>: the validation run, whose transaction is rolled back): field values by the write request's property names (camelCase), after mapping, transformation and lookups.</summary>
public sealed record ImportRow(int RowNumber, JsonObject Values, bool DryRun = false);

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
