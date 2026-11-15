using Erp.CodeGen.Design;

namespace Erp.CodeGen.Emit;

/// <summary>Where generated C# lives and what each table's types are called.</summary>
public static class CSharpNames
{
    public static string ModuleProject(Table t) => t.Module == "hrm" ? "Hrm" : "Kernel";

    public static string ModuleFolder(Table t) => t.Module == "hrm" ? "src/Modules/Hrm" : "src/Platform";

    public static string Namespace(Table t, string layer) => t.Module switch
    {
        "hrm" => $"Erp.Hrm.{layer}.Generated",
        "controlplane" => $"Erp.Kernel.{layer}.ControlPlane.Generated",
        _ => $"Erp.Kernel.{layer}.Generated.{Naming.Pascal(t.Schema)}",
    };

    /// <summary>Folder of a table's file inside a layer project, e.g. src/Platform/Erp.Kernel.Domain/Generated/Iam.</summary>
    public static string Folder(Table t, string layer) => t.Module switch
    {
        "hrm" => $"src/Modules/Hrm/Erp.Hrm.{layer}/Generated",
        "controlplane" => $"src/Platform/Erp.Kernel.{layer}/ControlPlane/Generated",
        _ => $"src/Platform/Erp.Kernel.{layer}/Generated/{Naming.Pascal(t.Schema)}",
    };

    public static string Entity(Table t) => Naming.EntityName(t);

    public static string EntityFull(Table t) => $"global::{Namespace(t, "Domain")}.{Entity(t)}";

    public static string RepositoryInterface(Table t) => $"I{Entity(t)}Repository";

    public static string RepositoryInterfaceFull(Table t) => $"global::{Namespace(t, "Application")}.{RepositoryInterface(t)}";

    public static string Filter(Table t) => $"{Entity(t)}Filter";

    public static string FilterFull(Table t) => $"global::{Namespace(t, "Application")}.{Filter(t)}";

    public static string DbContextName(Table t) => t.Module switch
    {
        "hrm" => "HrmDbContext",
        "controlplane" => "ControlPlaneDbContext",
        _ => "KernelDbContext",
    };

    public static string Property(Table t, Column c) => Naming.PropertyName(t, c);

    /// <summary>The CLR type of a column on the entity (nullable when the column is optional).</summary>
    public static string ClrType(Column c)
    {
        var (type, isValue) = BaseType(c);
        return c.Required ? type : type + "?";
    }

    public static (string Type, bool IsValueType) BaseType(Column c) => c.Kind switch
    {
        ColumnKind.Uuid => ("global::System.Guid", true),
        ColumnKind.Code or ColumnKind.Text or ColumnKind.Enum or ColumnKind.FixedText => ("string", false),
        ColumnKind.I18n or ColumnKind.Json => ("global::System.Text.Json.JsonDocument", false),
        ColumnKind.Date => ("global::System.DateOnly", true),
        ColumnKind.Timestamp => ("global::System.DateTimeOffset", true),
        ColumnKind.Int4 => ("int", true),
        ColumnKind.BigInteger => ("long", true),
        ColumnKind.Bool => ("bool", true),
        ColumnKind.Money or ColumnKind.Qty or ColumnKind.Rate or ColumnKind.Pct or ColumnKind.Numeric => ("decimal", true),
        ColumnKind.Encrypted or ColumnKind.Hmac or ColumnKind.Bytea => ("byte[]", false),
        ColumnKind.TextArray => ("string[]", false),
        ColumnKind.UuidArray => ("global::System.Guid[]", false),
        ColumnKind.Time => ("global::System.TimeOnly", true),
        ColumnKind.Inet => ("global::System.Net.IPAddress", false),
        _ => throw new InvalidOperationException(c.Kind.ToString()),
    };

    /// <summary>Type of the column in an API request or response (JSON-friendly; nullable so missing values can be detected).</summary>
    public static string DtoType(Column c)
    {
        var (type, _) = BaseType(c);
        return c.Kind switch
        {
            ColumnKind.I18n or ColumnKind.Json => "global::System.Text.Json.JsonElement?",
            ColumnKind.Inet => "string?",
            _ => type + "?",
        };
    }

    public static string TsType(Column c) => c.Kind switch
    {
        ColumnKind.Uuid or ColumnKind.Code or ColumnKind.Text or ColumnKind.FixedText or ColumnKind.Date or ColumnKind.Timestamp or ColumnKind.Time or ColumnKind.Inet => "string",
        ColumnKind.Enum => string.Join(" | ", c.EnumValues.Select(v => "'" + v + "'")),
        ColumnKind.I18n => "Record<string, string>",
        ColumnKind.Json => "unknown",
        ColumnKind.Int4 or ColumnKind.BigInteger => "number",
        // Decimals travel as JSON numbers from System.Text.Json; the client must not do money arithmetic in floating point.
        ColumnKind.Money or ColumnKind.Qty or ColumnKind.Rate or ColumnKind.Pct or ColumnKind.Numeric => "number",
        ColumnKind.Bool => "boolean",
        ColumnKind.Encrypted or ColumnKind.Hmac or ColumnKind.Bytea => "string",
        ColumnKind.TextArray or ColumnKind.UuidArray => "string[]",
        _ => throw new InvalidOperationException(c.Kind.ToString()),
    };

    public static string Route(Table t) => $"/api/{t.Schema}/{t.Name.Replace('_', '-')}";

    public static string Resource(Table t) => t.FullName;

    public static string Permission(Table t, string action) => $"{t.FullName}.{action}";
}
