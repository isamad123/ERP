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

    public required bool Required { get; set; }

    /// <summary>
    /// Slice that made a required column optional after the release's first migrations were merged (design key
    /// <c>optional_added_in</c>, S1.08 CR-033). The frozen file still says NOT NULL; the slice's design migration drops it.
    /// </summary>
    public string? OptionalIn { get; set; }

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

    /// <summary>
    /// Slice that added the column after the release's first migrations were merged (design key <c>added_in</c>). The
    /// frozen schema file leaves it out; the slice's design migration adds it.
    /// </summary>
    public string? AddedIn { get; set; }

    /// <summary>
    /// Column the generated API returns but never accepts (design key <c>api_read_only</c>, CR-012). Only purpose-built
    /// services change it; on create a status column starts at its first listed value.
    /// </summary>
    public bool ApiReadOnly { get; set; }

    /// <summary>
    /// Column the generated API accepts on create but never changes afterwards (design key <c>api_create_only</c>, S1.11b2
    /// review finding 2, CR-062): an update that sends another value is refused like a field the caller may not edit; only
    /// a purpose-built service changes it (for <c>iam.users.email</c>: the guarded e-mail change).
    /// </summary>
    public bool ApiCreateOnly { get; set; }

    /// <summary>
    /// Field class (design key <c>field_class</c>, CR-018): <c>salary</c>, <c>personal</c> or <c>restricted</c>. Salary
    /// fields are hidden from every role unless a field rule opens them (GHR-06).
    /// </summary>
    public string? FieldClass { get; set; }

    /// <summary>An encrypted column that a hand-written view shows, so field security can mask or hide it (design key field_view, S1.14a).</summary>
    public bool InFieldCatalogue { get; set; }

    public bool IsSensitive => Kind is ColumnKind.Encrypted or ColumnKind.Hmac;

    public bool IsGenerated => GeneratedExpression is not null;
}

/// <summary>A unique key (constraint or unique index). Items are column names or expressions such as <c>lower(code)</c>.</summary>
/// <remarks>
/// <see cref="AddedIn"/>: slice whose design migration adds the key (design key <c>uniques_added_in</c>, S1.08).
/// <see cref="ReplacedIn"/> and <see cref="BaseWhere"/> (design key <c>uniques_replaced_in</c>, S1.09b): the frozen file
/// created the partial key with <see cref="BaseWhere"/>; the slice's design migration recreates it with <see cref="Where"/>.
/// </remarks>
public sealed record UniqueKey(string Name, IReadOnlyList<string> Items, string? Where, bool NullsNotDistinct, string? AddedIn = null, string? BaseWhere = null, string? ReplacedIn = null)
{
    public bool IsPlain => Where is null && BaseWhere is null && Items.All(Naming.IsIdentifier);
}

/// <summary>An index. <see cref="AddedIn"/>: slice whose design migration creates it (design key <c>indexes_added_in</c>, S1.07).</summary>
public sealed record IndexDef(string Name, IReadOnlyList<string> Items, string? Where, string Purpose, string? AddedIn = null);

public sealed record ExclusionDef(string Name, IReadOnlyList<string> Elements, string? Where);

/// <summary>A check constraint. <see cref="AddedIn"/>: slice whose design migration adds it (the check of an enum column added later).</summary>
/// <remarks>
/// <see cref="ReplacedIn"/> and <see cref="BaseSql"/> (design key <c>enum_added_in</c>, S1.08): the frozen file created the
/// check as <see cref="BaseSql"/>; the slice's design migration replaces it with <see cref="Sql"/>.
/// </remarks>
public sealed record CheckDef(string Name, string Sql, string? AddedIn = null, string? BaseSql = null, string? ReplacedIn = null);

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

    /// <summary>
    /// The entitlement feature key the table belongs to (design key <c>entitlement:</c>, file default with a table override;
    /// slice S1.09 ruling Q9, CR-036): its generated endpoints answer only for customers licensed for the key. Customer-database
    /// tables always have one; control-plane tables never do.
    /// </summary>
    public string? Entitlement { get; init; }

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

    /// <summary>
    /// Slice that added the whole table after the release's first migrations were merged (design key <c>added_in</c>).
    /// The frozen files leave it out; the slice's design migration creates it.
    /// </summary>
    public string? AddedIn { get; init; }

    /// <summary>Updatable exceptions added later (design key <c>updatable_added_in</c>): slice → columns.</summary>
    public List<(string Slice, string Column)> UpdatableAddedIn { get; } = [];

    /// <summary>Table comments added later (with an <c>updatable_added_in</c> change): slice → text.</summary>
    public List<(string Slice, string Text)> CommentsAddedIn { get; } = [];

    /// <summary>The append-only exceptions as the release's first (frozen) migration created them.</summary>
    public IEnumerable<string> BaseAppendOnlyUpdatable => AppendOnlyUpdatable.Where(c => !UpdatableAddedIn.Exists(u => u.Column == c));

    /// <summary>Slices whose design migration changes this table (added table, columns or exceptions).</summary>
    /// <summary>
    /// Slice whose design migration adds a foreign key: the slice that added its column after the first migrations
    /// (S1.11a, CR-045: <c>job.runs.actor_user_id</c>); null for keys of the first migrations or of a table added later.
    /// </summary>
    public string? ForeignKeyAddedIn(ForeignKey fk) =>
        AddedIn is null ? fk.Columns.Select(c => Columns.Find(x => x.Name == c)?.AddedIn).FirstOrDefault(a => a is not null) : null;

    public IEnumerable<string> ChangedIn =>
        new[] { AddedIn }.Concat(Columns.Select(c => c.AddedIn)).Concat(UpdatableAddedIn.Select(u => u.Slice)).Concat(CommentsAddedIn.Select(c => c.Slice))
            .Concat(Indexes.Select(i => i.AddedIn)).Concat(Checks.Select(c => c.AddedIn))
            .Concat(Columns.Select(c => c.OptionalIn)).Concat(Checks.Select(c => c.ReplacedIn)).Concat(Uniques.Select(u => u.AddedIn)).Concat(Uniques.Select(u => u.ReplacedIn))
            .Where(s => s is not null).Select(s => s!).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Columns that locate a row for data scopes (design key <c>scope</c>, CR-018): dimension (company, branch, org_unit,
    /// employment, person, user) to column name. Empty means the table is not scoped: the permission alone decides.
    /// </summary>
    public SortedDictionary<string, string> ScopeLocators { get; } = new(StringComparer.Ordinal);

    /// <summary>Text columns that data-scope attribute conditions may test (design key <c>scope.attributes</c>, Q5).</summary>
    public List<string> ScopeAttributes { get; } = [];

    /// <summary>
    /// Foreign-key column through whose parent row this table's rows are located (design key <c>scope: {via: column}</c>,
    /// review S1.06 finding 1, CR-025); the parent must be directly scoped. Null for other tables.
    /// </summary>
    public string? ScopeVia { get; set; }

    /// <summary>The parent table named by <see cref="ScopeVia"/> (resolved after the foreign keys).</summary>
    public Table? ScopeViaTable { get; set; }

    /// <summary>True when data scopes apply to the table (its own locators or its parent's).</summary>
    public bool IsDataScoped => ScopeLocators.Count > 0 || ScopeVia is not null;

    /// <summary>
    /// Generated API actions allowed (design key <c>api_actions</c>, CR-020/CR-021); null means every action the table's
    /// shape supports. Purpose-built services replace the closed ones.
    /// </summary>
    public IReadOnlyList<string>? ApiActions { get; set; }

    public bool AllowsApi(string action) => ApiActions is null || ApiActions.Contains(action);

    /// <summary>
    /// False when the change log skips the table (design keys <c>audit: false</c> and <c>audit_reason</c>, slice S1.07
    /// Q4, CR-026): append-only tables, where the row itself is the record, and machinery. Every other table is audited.
    /// </summary>
    public bool Audited { get; set; } = true;

    /// <summary>Why the table is not change-logged (required with <c>audit: false</c>).</summary>
    public string? AuditReason { get; set; }

    /// <summary>
    /// The uuid column that identifies a row in the change log (<c>entity_id</c>): <c>id</c>, else the first uuid column
    /// of the primary key; null when the key has no uuid column (such tables must be <c>audit: false</c>).
    /// </summary>
    public string? AuditKeyColumn =>
        HasIdKey ? "id" : PrimaryKey.FirstOrDefault(k => Columns.Exists(c => c.Name == k && c.Kind == ColumnKind.Uuid));

    /// <summary>Numbered columns of the table (design key <c>numbering</c>, S1.08 CR-033).</summary>
    public List<NumberingDef> Numbering { get; } = [];

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

/// <summary>
/// One numbered column (design key <c>numbering</c>, slice S1.08, CR-033): the document type code, when the number is
/// given (<c>create</c>: the generated API fills it; <c>post</c>: the module's post service calls the allocator), whether
/// customers may switch the series to gap-free, whether a typed-in number may replace it (HRM-EMP-04), whether one
/// series serves the whole customer (no company, Q6), the default pattern and reset rule (Q7), the column holding the
/// document's business date (Q5) and whether the table has a branch (<c>{branch}</c> and branch schemes, Q4, P2).
/// </summary>
public sealed record NumberingDef(
    string Column,
    string DocumentType,
    string Assign,
    bool GapFreeAllowed,
    bool OverrideAllowed,
    bool TenantWide,
    string DefaultPattern,
    string DefaultReset,
    string? DateColumn,
    string? CompanyColumn,
    string? BranchColumn);

/// <summary>A database function a slice writes (rules.yaml <c>functions</c>, slice S1.07): reviewed SQL, emitted as a migration.</summary>
/// <remarks><paramref name="Triggers"/>: the triggers the SQL creates, as (table, trigger name) (slice S1.09a).</remarks>
/// <remarks><paramref name="ConstraintFields"/>: constraint names the SQL raises and the column each one is about, as (table, constraint, column), so the web form shows the refusal next to the field (slice S1.14a).</remarks>
public sealed record FunctionDef(string Slice, string Name, string Purpose, string Sql, IReadOnlyList<(string Table, string Trigger)> Triggers,
    IReadOnlyList<(string Table, string Constraint, string Column)> ConstraintFields);

public sealed class DesignModel
{
    public required IReadOnlyList<Table> Tables { get; init; }

    /// <summary>Database functions written by slices (rules.yaml <c>functions</c>).</summary>
    public IReadOnlyList<FunctionDef> Functions { get; init; } = [];

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
