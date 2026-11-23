using System.Text.Json;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Metadata;

/// <summary>
/// Permissions of the label, option-list and custom-field services (slice S1.15b). The generated writes of labels and custom-field
/// definitions are closed and the generated endpoints of option lists and items are left out; create and update (and, for option lists
/// and items, read) keep their codes (CR-020 pattern).
/// </summary>
public static class MetadataPermissions
{
    public const string LabelsRead = "cfg.labels.read";
    public const string LabelsUpdate = "cfg.labels.update";
    public const string OptionListsRead = "cfg.option_lists.read";
    public const string OptionListsCreate = "cfg.option_lists.create";
    public const string OptionListsUpdate = "cfg.option_lists.update";
    public const string OptionItemsCreate = "cfg.option_list_items.create";
    public const string OptionItemsUpdate = "cfg.option_list_items.update";
    public const string CustomFieldsRead = "cfg.custom_field_defs.read";
    public const string CustomFieldsCreate = "cfg.custom_field_defs.create";
    public const string CustomFieldsUpdate = "cfg.custom_field_defs.update";

    /// <summary>Codes whose generated endpoints are closed; the descriptions live in <c>metadata.*.json</c>.</summary>
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(OptionListsRead, "cfg", "permissions.cfg.option_lists.read", false),
        new("cfg.option_list_items.read", "cfg", "permissions.cfg.option_list_items.read", false),
        new("cfg.labels.create", "cfg", "permissions.cfg.labels.create", false),
        new(LabelsUpdate, "cfg", "permissions.cfg.labels.update", false),
        new(OptionListsCreate, "cfg", "permissions.cfg.option_lists.create", false),
        new(OptionListsUpdate, "cfg", "permissions.cfg.option_lists.update", false),
        new(OptionItemsCreate, "cfg", "permissions.cfg.option_list_items.create", false),
        new(OptionItemsUpdate, "cfg", "permissions.cfg.option_list_items.update", false),
        new(CustomFieldsCreate, "cfg", "permissions.cfg.custom_field_defs.create", false),
        new(CustomFieldsUpdate, "cfg", "permissions.cfg.custom_field_defs.update", false),
    ];
}

/// <summary>Limits shared by the three services and the web screens.</summary>
public static class MetadataLimits
{
    public const int LabelTextLength = 300;
    public const int LabelKeyLength = 200;
    public const int NameLength = 120;
    public const int HelpLength = 400;
    public const int CodeLength = 40;
    public const int SectionLength = 60;
    public const int PatternLength = 200;
    public const int TextDefault = 500;
    public const int LongTextDefault = 4000;
    public const int MultiChoiceDefault = 50;
}

/// <summary>The data types a custom field takes in release 1.0 (D-50 data design, CR-071 Q5). <c>file</c> is defined but refused until the documents screens (S1.24).</summary>
public static class CustomFieldTypes
{
    public const string Text = "text";
    public const string LongText = "long_text";
    public const string Number = "number";
    public const string DecimalNumber = "decimal";
    public const string Date = "date";
    public const string Bool = "bool";
    public const string Choice = "choice";
    public const string MultiChoice = "multi_choice";
    public const string User = "user";
    public const string Employee = "employee";
    public const string File = "file";

    public static IReadOnlyList<string> All { get; } = [Text, LongText, Number, DecimalNumber, Date, Bool, Choice, MultiChoice, User, Employee, File];

    /// <summary>The types a definition may use now.</summary>
    public static IReadOnlyList<string> Available { get; } = [Text, LongText, Number, DecimalNumber, Date, Bool, Choice, MultiChoice, User, Employee];

    public static bool NeedsOptionList(string type) => type is Choice or MultiChoice;
}

/// <summary>A name or text in both languages; English is required where the call says so.</summary>
public sealed record Names(string? En, string? Ur);

/// <summary>A refused call of the metadata services: the HTTP status, the localization key of the error and the field it concerns.</summary>
public sealed class MetadataRefusedException(int status, string errorKey, string? field = null) : Exception(errorKey)
{
    public int Status { get; } = status;

    public string ErrorKey { get; } = errorKey;

    public string? Field { get; } = field;
}

// ---------------------------------------------------------------- labels

/// <summary>One row of <c>cfg.labels</c>: a text for a key, language and layer (<c>pack</c> product, <c>tenant</c> customer, <c>company</c>).</summary>
public sealed record LabelOverrideRow(Guid Id, string Key, string Language, string Layer, Guid? ScopeId, string Text, long RowVersion);

public sealed record LabelOverrideList(IReadOnlyList<LabelOverrideRow> Items);

/// <summary>A language not present in the body is left as it is; null or blank removes the override (reset to the product text).</summary>
public sealed record LabelTexts(JsonElement En, JsonElement Ur);

public sealed record LabelOverrideWrite(string? Key, string? Layer, Guid? CompanyId, LabelTexts? Texts);

/// <summary>Label overrides of the customer and its companies (contract "Label administration API", FR-ADM-STU-25, FR-KRN-MET-22).</summary>
public interface ILabelAdministration
{
    /// <summary>Every override of every layer.</summary>
    Task<LabelOverrideList> ListAsync(CancellationToken cancellationToken);

    /// <summary>Sets or removes the texts of one key in one layer; returns the rows now in force for that key and layer.</summary>
    Task<LabelOverrideList> SetAsync(LabelOverrideWrite write, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------- option lists

public sealed record OptionListSummary(Guid Id, string Code, Names Name, bool IsSystem, int ItemCount, long RowVersion);

public sealed record OptionItemView(Guid Id, string Code, Names Label, int Seq, bool IsActive, long RowVersion);

public sealed record OptionListDetail(Guid Id, string Code, Names Name, bool IsSystem, long RowVersion, IReadOnlyList<OptionItemView> Items);

public sealed record OptionListCreate(string? Code, Names? Name);

public sealed record OptionListEdit(Names? Name, long? RowVersion);

public sealed record OptionItemCreate(string? Code, Names? Label, bool? IsActive);

public sealed record OptionItemEdit(Names? Label, bool? IsActive, long? RowVersion);

public sealed record OptionOrder(IReadOnlyList<Guid>? ItemIds);

/// <summary>
/// Option lists and their items (contract "Option list API", S1.15 Q7): codes are fixed once created, items are deactivated and
/// never deleted, system lists can be extended and are never removed.
/// </summary>
public interface IOptionListAdministration
{
    Task<IReadOnlyList<OptionListSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Null when the list does not exist.</summary>
    Task<OptionListDetail?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<OptionListDetail> CreateAsync(OptionListCreate write, CancellationToken cancellationToken);

    Task<OptionListDetail> EditAsync(Guid id, OptionListEdit write, CancellationToken cancellationToken);

    Task<OptionListDetail> AddItemAsync(Guid id, OptionItemCreate write, CancellationToken cancellationToken);

    Task<OptionListDetail> EditItemAsync(Guid id, Guid itemId, OptionItemEdit write, CancellationToken cancellationToken);

    Task<OptionListDetail> OrderAsync(Guid id, OptionOrder write, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------- custom fields

/// <summary>A table that carries custom fields (trait <c>cust</c>): its entity type is the table name, for example <c>hrm.employments</c>.</summary>
public sealed record CustomEntity(string EntityType, string LabelKey, bool CompanyScoped);

public sealed record CustomEntityList(IReadOnlyList<CustomEntity> Items);

public sealed record CustomFieldDefView(
    Guid Id, string EntityType, string FieldKey, string DataType, Names Label, Names? Help, JsonElement Validation, JsonElement? DefaultValue,
    bool IsRequired, bool IsUnique, Guid? OptionListId, string? Section, int Seq, string Status, int Version, long RowVersion);

public sealed record CustomFieldDefList(IReadOnlyList<CustomFieldDefView> Items);

public sealed record CustomFieldCreate(
    string? EntityType, string? FieldKey, string? DataType, Names? Label, Names? Help, JsonElement? Validation, JsonElement? DefaultValue,
    bool? IsRequired, bool? IsUnique, Guid? OptionListId, string? Section, int? Seq);

public sealed record CustomFieldEdit(
    Names? Label, Names? Help, JsonElement? Validation, JsonElement? DefaultValue, bool? IsRequired, bool? IsUnique, string? Section, int? Seq, long? RowVersion);

public sealed record CustomFieldState(long? RowVersion);

/// <summary>One choice of a custom field as a form shows it.</summary>
public sealed record CustomOption(string Code, Names Label);

/// <summary>
/// A custom field as the form of a record shows it (contract "Custom field form API"): <paramref name="Options"/> are the active items of the
/// field's list (the whole list for a retired field, which is shown read-only); <paramref name="Access"/> is what the
/// caller's roles allow for <c>custom.&lt;key&gt;</c> (<c>editable</c>, <c>readonly</c> or <c>masked</c>; hidden fields are left out).
/// </summary>
public sealed record FormCustomField(
    string FieldKey, string DataType, Names Label, Names? Help, JsonElement Validation, JsonElement? DefaultValue, bool IsRequired, bool IsUnique,
    string? Section, int Seq, bool Retired, string Access, IReadOnlyList<CustomOption> Options);

public sealed record FormCustomFieldList(IReadOnlyList<FormCustomField> Items);

/// <summary>Definitions of custom fields (contract "Custom field API", S1.15 Q5 to Q8).</summary>
public interface ICustomFieldAdministration
{
    Task<CustomEntityList> EntitiesAsync(CancellationToken cancellationToken);

    Task<CustomFieldDefList> ListAsync(string? entityType, CancellationToken cancellationToken);

    Task<CustomFieldDefView> CreateAsync(CustomFieldCreate write, CancellationToken cancellationToken);

    Task<CustomFieldDefView> EditAsync(Guid id, CustomFieldEdit write, CancellationToken cancellationToken);

    /// <summary>Hides the field and keeps its values; <paramref name="restore"/> brings it back.</summary>
    Task<CustomFieldDefView> SetStateAsync(Guid id, CustomFieldState write, bool restore, CancellationToken cancellationToken);

    /// <summary>
    /// The fields a record form of <paramref name="entityType"/> shows the caller, or null when the type is not extensible or the
    /// caller may not read the table. <paramref name="companyId"/> picks the roles valid for that company.
    /// </summary>
    Task<FormCustomFieldList?> FormAsync(string entityType, Guid? companyId, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------- values

/// <summary>
/// A write of custom values to check (FR-KRN-MET-02). <paramref name="Values"/> is the object after field security merged the
/// request with the stored values; <paramref name="Stored"/> is the stored object (null on create); <paramref name="CanEdit"/>
/// says whether the caller may edit <c>custom.&lt;key&gt;</c>.
/// </summary>
public sealed record CustomWrite(string EntityType, Guid? RowId, Guid? CompanyId, JsonDocument? Values, JsonDocument? Stored, Func<string, bool> CanEdit, bool Update);

/// <summary>
/// The outcome of a check: <see cref="Errors"/> (<c>custom.&lt;key&gt;</c> to error key) or the <see cref="Values"/> to save. Disposing
/// releases the locks that made the checks of unique fields safe; keep it until the save is done.
/// </summary>
public sealed class CustomCheck(IReadOnlyDictionary<string, string> errors, JsonDocument values, IAsyncDisposable? locks) : IAsyncDisposable
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;

    public JsonDocument Values { get; } = values;

    public ValueTask DisposeAsync() => locks?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>Checks custom values against the active definitions before a generated or hand-written write saves them.</summary>
public interface ICustomFieldValidator
{
    Task<CustomCheck> CheckAsync(CustomWrite write, CancellationToken cancellationToken);
}

/// <summary>
/// Tells whether the target of an <c>employee</c> custom field exists. A module that owns such records registers one; the kernel
/// resolves <c>user</c> fields itself.
/// </summary>
public interface ICustomReferenceResolver
{
    /// <summary>The data type this resolver answers for, for example <c>employee</c>.</summary>
    string DataType { get; }

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
}
