namespace Erp.CodeGen.Design;

/// <summary>Which database a table lives in (D-47): every customer database, or the single control-plane database.</summary>
public enum DatabaseKind
{
    Tenant,
    ControlPlane,
}

/// <summary>The type shorthands of docs/data-design/README.md, plus the PostgreSQL types the design names directly.</summary>
public enum ColumnKind
{
    Uuid,
    Code,
    Text,
    I18n,
    Date,
    Timestamp,
    Int4,
    BigInteger,
    Bool,
    Money,
    Qty,
    Rate,
    Pct,
    Json,
    Encrypted,
    Hmac,
    Enum,
    FixedText,
    Numeric,
    TextArray,
    UuidArray,
    Bytea,
    Time,
    Inet,
}

public sealed class Column
{
    public required string Name { get; init; }

    public required ColumnKind Kind { get; init; }

    /// <summary>PostgreSQL type as written in DDL, for example <c>numeric(19,4)</c> or <c>character(3)</c>.</summary>
    public required string SqlType { get; init; }

    public required bool Required { get; init; }

    /// <summary>Allowed values for enum columns and the trait status column.</summary>
    public IReadOnlyList<string> EnumValues { get; init; } = [];

    /// <summary>SQL default expression, if any.</summary>
    public string? Default { get; init; }

    /// <summary>Expression of a stored generated column, if any.</summary>
    public string? GeneratedExpression { get; init; }

    /// <summary>Descriptive text, kept as a column comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Where the column came from: a trait name, "status", or "design".</summary>
    public required string Origin { get; init; }

    public bool IsSensitive => Kind is ColumnKind.Encrypted or ColumnKind.Hmac;

    public bool IsGenerated => GeneratedExpression is not null;
}

/// <summary>A unique key (constraint or unique index). Items are column names or expressions such as <c>lower(code)</c>.</summary>
public sealed record UniqueKey(string Name, IReadOnlyList<string> Items, string? Where, bool NullsNotDistinct)
{
    public bool IsPlain => Where is null && Items.All(Naming.IsIdentifier);
}

public sealed record IndexDef(string Name, IReadOnlyList<string> Items, string? Where, string Purpose);

public sealed record ExclusionDef(string Name, IReadOnlyList<string> Elements, string? Where);

public sealed record CheckDef(string Name, string Sql);

public sealed record ForeignKey(string Name, IReadOnlyList<string> Columns, string TargetTable, IReadOnlyList<string> TargetColumns)
{
    public bool CrossesSchema(string fromSchema) => !TargetTable.StartsWith(fromSchema + ".", StringComparison.Ordinal);
}

public sealed record PartitionDef(string Column, ColumnKind ColumnKind);

/// <summary>
/// The real database objects that replace a placeholder (rules.yaml <c>implemented</c>). They go into a new migration
/// file of the owning slice, because migrations that are already merged are never edited.
/// </summary>
public sealed record RuleImplementation(string Slice, string Sql, IReadOnlyList<(string Table, string Trigger)> Triggers);

/// <summary>
/// A rule the database cannot enforce yet; a later slice owns it (docs/registers/todo-stubs.md). Once
/// <see cref="Implementation"/> is set the placeholder is still created by the original (frozen) migration and then
/// dropped by the owning slice's migration.
/// </summary>
public sealed record StubDef(string Table, string Name, string Owner, string Rule, RuleImplementation? Implementation = null)
{
    public string FunctionName => "todo_" + Name;

    public bool IsPending => Implementation is null;
}

public sealed class Table
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public string FullName => Schema + "." + Name;

    public required DatabaseKind Database { get; init; }

    /// <summary>"kernel", "hrm" or "controlplane": the migration set and code project the table belongs to.</summary>
    public required string Module { get; init; }

    public required string SourceFile { get; init; }

    public string? Purpose { get; init; }

    public required IReadOnlyList<string> Traits { get; init; }

    public required IReadOnlyList<string> Requirements { get; init; }

    public List<Column> Columns { get; } = [];

    public List<string> PrimaryKey { get; } = [];

    public List<UniqueKey> Uniques { get; } = [];

    public List<IndexDef> Indexes { get; } = [];

    public List<ExclusionDef> Exclusions { get; } = [];

    public List<CheckDef> Checks { get; } = [];

    public List<ForeignKey> ForeignKeys { get; } = [];

    public List<StubDef> Stubs { get; } = [];

    public List<string> Comments { get; } = [];

    public PartitionDef? Partition { get; set; }

    public bool IsAppendOnly => Traits.Contains("app");

    /// <summary>Columns the append-only trigger still allows to change (documented exception, e.g. iam.sessions).</summary>
    public List<string> AppendOnlyUpdatable { get; } = [];

    public bool IsSoftDeleted => Traits.Contains("soft");

    public bool IsCompanyScoped => Traits.Contains("co");

    public bool IsEffectiveDated => Traits.Contains("eff");

    public bool HasRowVersion => Columns.Exists(c => c.Name == "row_version");

    public bool IsOutboxOrInbox { get; init; }

    /// <summary>True when the table has a single <c>id</c> key column (the shape generated repositories and APIs use).</summary>
    public bool HasIdKey => Columns.Exists(c => c.Name == "id") && PrimaryKey.Count > 0 && PrimaryKey[0] == "id";

    /// <summary>Set for company-scoped tables that other company-scoped tables reference (README rule 12).</summary>
    public bool NeedsCompanyIdKey { get; set; }

    public Column Column(string name) =>
        Columns.Find(c => c.Name == name) ?? throw new DesignException(FullName, $"column '{name}' does not exist");

    public bool HasColumn(string name) => Columns.Exists(c => c.Name == name);

    public override string ToString() => FullName;
}

public sealed class DesignModel
{
    public required IReadOnlyList<Table> Tables { get; init; }

    public required Settings Settings { get; init; }

    public IEnumerable<Table> DesignTables => Tables.Where(t => !t.IsOutboxOrInbox);

    /// <summary>Every placeholder ever generated, implemented or not (the frozen first migrations still create them).</summary>
    public IEnumerable<StubDef> AllStubs => Tables.SelectMany(t => t.Stubs).OrderBy(s => s.Table, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal);

    /// <summary>Placeholders whose rule is not implemented yet (the register and the expected catalogue).</summary>
    public IEnumerable<StubDef> Stubs => AllStubs.Where(s => s.IsPending);

    /// <summary>Placeholders replaced by a real rule (rules.yaml <c>implemented</c>).</summary>
    public IEnumerable<StubDef> ImplementedStubs => AllStubs.Where(s => !s.IsPending);

    public Table Table(string fullName) =>
        Tables.FirstOrDefault(t => t.FullName == fullName) ?? throw new DesignException(fullName, "table does not exist");

    public bool TryGetTable(string fullName, out Table table)
    {
        table = Tables.FirstOrDefault(t => t.FullName == fullName)!;
        return table is not null;
    }
}

/// <summary>Raised when the design cannot be read without guessing. The generator stops; nothing is written.</summary>
public sealed class DesignException(string where, string problem) : Exception($"{where}: {problem}")
{
    public string Where { get; } = where;

    public string Problem { get; } = problem;
}
