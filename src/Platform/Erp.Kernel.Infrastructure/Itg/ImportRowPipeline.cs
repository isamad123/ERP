using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>What one row came to: the problems found, or what the writer did (slice S1.16a).</summary>
public sealed record ImportRowResult(int RowNo, IReadOnlyList<ImportRowError> Errors, ImportRowOutcome? Outcome, JsonObject? Values = null)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Takes one file row through map, transform, lookups and key check to the type's writer (slice S1.16a, FR-KRN-INT-03). The
/// database work (code lookups, finding an existing record by its key) runs on the context's own connection and transaction, so a
/// row sees what earlier rows of the same run wrote (a child unit finds its parent from the same file). Each row runs inside a
/// savepoint: a row that fails leaves nothing behind and the run goes on.
/// </summary>
public sealed class ImportRowPipeline
{
    private readonly ImportTypeDefinition _type;
    private readonly TemplateMapping _mapping;
    private readonly string _mode;
    private readonly IImportRowWriter _writer;
    private readonly KernelDbContext _db;
    private readonly ICallerAccess? _access;
    private readonly int[] _sourceIndex;
    private readonly TemplateColumn?[] _mapped;
    private readonly Dictionary<string, Guid?> _lookups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Company, bool Soft)> _tableShape = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keysSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private int _savepoints;

    public ImportRowPipeline(ImportTypeDefinition type, TemplateMapping mapping, string mode, IReadOnlyList<string> headers, IImportRowWriter writer, KernelDbContext db, ICallerAccess? access = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(headers);
        _type = type;
        _mapping = mapping;
        _mode = mode;
        _writer = writer;
        _db = db;
        _access = access;
        _mapped = [.. type.Columns.Select(c => mapping.Columns.FirstOrDefault(m => string.Equals(m.Field, c.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Field, c.Field, StringComparison.OrdinalIgnoreCase)))];
        _sourceIndex = [.. _mapped.Select(m => m is null || string.IsNullOrWhiteSpace(m.Source) ? -1 : IndexOf(headers, m.Source))];
    }

    /// <summary>The headers a template needs that the file does not have (required columns that read a file column).</summary>
    public static IReadOnlyList<string> MissingHeaders(ImportTypeDefinition type, TemplateMapping mapping, IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(headers);
        var missing = new List<string>();
        foreach (var column in type.Columns)
        {
            var m = mapping.Columns.FirstOrDefault(x => string.Equals(x.Field, column.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Field, column.Field, StringComparison.OrdinalIgnoreCase));
            if (column.Required && m is not null && !string.IsNullOrWhiteSpace(m.Source) && m.Default is null && IndexOf(headers, m.Source) < 0)
            {
                missing.Add(m.Source);
            }
        }

        return missing;
    }

    /// <summary>Runs one row. With <paramref name="mapOnly"/> the writer is not called (the template test's mapped sample).</summary>
    public async Task<ImportRowResult> ProcessAsync(int rowNo, string?[] cells, bool dryRun, bool mapOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var errors = new List<ImportRowError>();
        var values = new JsonObject();
        for (var i = 0; i < _type.Columns.Count; i++)
        {
            var column = _type.Columns[i];
            var mapped = _mapped[i];
            var cell = _sourceIndex[i] >= 0 && _sourceIndex[i] < cells.Length ? cells[_sourceIndex[i]] : null;
            var (text, transformError) = ImportTransforms.Apply(cell, mapped?.Transform ?? []);
            if (transformError is not null)
            {
                errors.Add(new ImportRowError(column.Name, transformError));
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                text = mapped?.Default;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                if (column.Required)
                {
                    errors.Add(new ImportRowError(column.Name, "errors.import.required"));
                }

                continue;
            }

            if (column.Lookup is { } table)
            {
                var company = values["companyId"]?.GetValue<string>() is { } c && Guid.TryParse(c, out var cid) ? cid : (Guid?)null;
                var id = await LookupAsync(table, text.Trim(), table == "org.companies" ? null : company, cancellationToken).ConfigureAwait(false);
                if (id is null)
                {
                    errors.Add(new ImportRowError(column.Name, "errors.import.lookup_unknown"));
                }
                else
                {
                    ImportTransforms.Set(values, column.Field, JsonValue.Create(id.Value.ToString()));
                }

                continue;
            }

            var (json, error) = ImportTransforms.ToJson(column, text);
            if (error is not null)
            {
                errors.Add(new ImportRowError(column.Name, error));
            }
            else if (json is not null)
            {
                ImportTransforms.Set(values, column.Field, json);
            }
        }

        if (errors.Count > 0)
        {
            return new ImportRowResult(rowNo, errors, null);
        }

        // A person who holds the right for one company only imports nothing into another (the data scope of the role, S1.06 ruling A).
        if (_access is not null && values["companyId"]?.GetValue<string>() is { } companyText && Guid.TryParse(companyText, out var companyId)
            && !await _access.CoversCompanyAsync(_type.Permission, companyId, cancellationToken).ConfigureAwait(false))
        {
            return new ImportRowResult(rowNo, [new ImportRowError(_type.Columns.FirstOrDefault(c => c.Field == "companyId")?.Name, "errors.import.company_forbidden")], null, values);
        }

        Guid? existing = null;
        if (_type.KeyColumns.Count > 0)
        {
            var key = KeyText(values);
            if (key is not null && !_keysSeen.Add(key))
            {
                return new ImportRowResult(rowNo, [new ImportRowError(_type.KeyColumns[0], "errors.import.duplicate_in_file")], null);
            }

            existing = await FindExistingAsync(values, cancellationToken).ConfigureAwait(false);
            if (existing is not null && !(_mode == "create_or_update" && _type.CanUpdate))
            {
                return new ImportRowResult(rowNo, [new ImportRowError(_type.KeyColumns[0], "errors.import.key_exists")], null);
            }
        }

        if (mapOnly)
        {
            return new ImportRowResult(rowNo, [], new ImportRowOutcome([], existing is null ? "created" : "updated", []), values);
        }

        return await WriteAsync(rowNo, values, existing, dryRun, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportRowResult> WriteAsync(int rowNo, JsonObject values, Guid? existing, bool dryRun, CancellationToken ct)
    {
        var tx = _db.Database.CurrentTransaction;
        var savepoint = tx is null ? null : "import_row_" + (++_savepoints).ToString(CultureInfo.InvariantCulture);
        if (tx is not null && savepoint is not null)
        {
            await tx.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
        }

        ImportRowOutcome outcome;
        try
        {
            outcome = await _writer.WriteAsync(new ImportRow(rowNo, values, dryRun, dryRun ? _known : null), existing, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not Erp.Kernel.Contracts.Jobs.JobStoppedException)
        {
            outcome = ImportRowOutcome.Failed(new ImportRowError(null, e is Erp.BuildingBlocks.Application.Data.DataConflictException ? "errors.import.key_exists" : "errors.import.row_failed"));
        }

        if (outcome.Ok)
        {
            if (dryRun && KeyText(values) is { } known)
            {
                _known.Add(known);
            }

            if (tx is not null && savepoint is not null)
            {
                await tx.ReleaseSavepointAsync(savepoint, ct).ConfigureAwait(false);
            }
        }
        else if (tx is not null && savepoint is not null)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct).ConfigureAwait(false);
        }

        _db.ChangeTracker.Clear();
        return outcome.Ok ? new ImportRowResult(rowNo, [], outcome, values) : new ImportRowResult(rowNo, outcome.Errors, null, values);
    }

    private string? KeyText(JsonObject values)
    {
        var parts = new List<string>();
        foreach (var name in _type.KeyColumns)
        {
            var column = _type.Columns.First(c => c.Name == name);
            var node = Get(values, column.Field);
            if (node is null)
            {
                return null;
            }

            parts.Add(node.ToJsonString().ToLowerInvariant());
        }

        return string.Join('|', parts);
    }

    private static JsonNode? Get(JsonObject values, string field)
    {
        JsonNode? node = values;
        foreach (var part in field.Split('.'))
        {
            node = (node as JsonObject)?[part];
        }

        return node;
    }

    private async Task<Guid?> FindExistingAsync(JsonObject values, CancellationToken ct)
    {
        var sql = new StringBuilder("select id from ").Append(Safe(_type.Table)).Append(" where ");
        var parameters = new List<object>();
        var shape = await ShapeAsync(_type.Table, ct).ConfigureAwait(false);
        foreach (var name in _type.KeyColumns)
        {
            var column = _type.Columns.First(c => c.Name == name);
            var node = Get(values, column.Field);
            if (node is null)
            {
                return null;
            }

            parameters.Add(Parameter(column, node));
            var db = Snake(column.Field);
            sql.Append(parameters.Count > 1 ? " and " : string.Empty).Append(column.Lookup is null && column.Type == "text" ? $"lower({db}) = lower(${parameters.Count})" : $"{db} = ${parameters.Count}");
        }

        if (shape.Soft)
        {
            sql.Append(" and not is_deleted");
        }

        return await ScalarGuidAsync(sql.ToString(), parameters, ct).ConfigureAwait(false);
    }

    private static object Parameter(ImportColumnDefinition column, JsonNode node) => column.Lookup is not null ? Guid.Parse(node.GetValue<string>())
        : column.Type switch
        {
            "number" => node.GetValue<decimal>(),
            "bool" => node.GetValue<bool>(),
            "date" => DateOnly.ParseExact(node.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => node.GetValue<string>(),
        };

    private async Task<Guid?> LookupAsync(string table, string code, Guid? company, CancellationToken ct)
    {
        var shape = await ShapeAsync(table, ct).ConfigureAwait(false);
        var scoped = company is not null && shape.Company;
        var cacheKey = $"{table}|{code.ToLowerInvariant()}|{(scoped ? company : null)}";
        if (_lookups.TryGetValue(cacheKey, out var known) && known is not null)
        {
            return known;
        }

        var sql = $"select id from {Safe(table)} where lower(code) = lower($1)" + (scoped ? " and company_id = $2" : string.Empty) + (shape.Soft ? " and not is_deleted" : string.Empty);
        var found = await ScalarGuidAsync(sql, scoped ? [code, company!.Value] : [code], ct).ConfigureAwait(false);
        if (found is not null)
        {
            _lookups[cacheKey] = found;
        }

        return found;
    }

    private async Task<(bool Company, bool Soft)> ShapeAsync(string table, CancellationToken ct)
    {
        if (_tableShape.TryGetValue(table, out var shape))
        {
            return shape;
        }

        var parts = Safe(table).Split('.');
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var command = Command("select column_name from information_schema.columns where table_schema = $1 and table_name = $2", [parts[0], parts[1]]);
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }
        }

        shape = (columns.Contains("company_id"), columns.Contains("is_deleted"));
        _tableShape[table] = shape;
        return shape;
    }

    private async Task<Guid?> ScalarGuidAsync(string sql, IReadOnlyList<object> parameters, CancellationToken ct)
    {
        await using var command = Command(sql, parameters);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid g ? g : null;
    }

    private NpgsqlCommand Command(string sql, IReadOnlyList<object> parameters)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var command = new NpgsqlCommand(sql, connection, _db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
        foreach (var p in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = p });
        }

        return command;
    }

    /// <summary>A table name from a manifest (never from a file): schema and table in lower case letters and underscores only.</summary>
    private static string Safe(string table) => table.Split('.') is [var a, var b] && a.All(IsName) && b.All(IsName) ? table : throw new InvalidDataException("Not a table name: " + table);

    private static bool IsName(char c) => c is >= 'a' and <= 'z' or '_';

    private static string Snake(string field)
    {
        var last = field.Split('.')[^1];
        var sb = new StringBuilder();
        foreach (var ch in last)
        {
            if (char.IsUpper(ch))
            {
                sb.Append('_').Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return Safe("x." + sb).Split('.')[1];
    }

    private static int IndexOf(IReadOnlyList<string> headers, string source)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            if (string.Equals(headers[i].Trim(), source.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
