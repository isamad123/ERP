using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Erp.CodeGen.Design;

/// <summary>Naming rules: README "constraint names pk_, fk_, uq_, ck_, ex_ + table + columns; index names ix_".</summary>
public static partial class Naming
{
    /// <summary>PostgreSQL truncates identifiers longer than 63 bytes, which could make two names collide silently.</summary>
    public const int MaxIdentifierLength = 63;

    public static bool IsIdentifier(string text) => IdentifierPattern().IsMatch(text);

    /// <summary>Builds <c>prefix_table_part1_part2</c>; over-long names are cut and end with a stable 8-character hash.</summary>
    public static string DbName(string prefix, string table, IEnumerable<string> parts)
    {
        var words = parts.Select(Slug).Where(p => p.Length > 0);
        var full = string.Join('_', new[] { prefix, table }.Concat(words));
        if (full.Length <= MaxIdentifierLength)
        {
            return full;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8];
        return full[..(MaxIdentifierLength - 9)].TrimEnd('_') + "_" + hash;
    }

    /// <summary>Reduces an expression such as <c>lower(code)</c> to <c>code</c> for use inside a name.</summary>
    public static string Slug(string item)
    {
        var lower = LowerPattern().Match(item);
        if (lower.Success)
        {
            return lower.Groups[1].Value;
        }

        return NonWordPattern().Replace(item.ToLowerInvariant(), "_").Trim('_');
    }

    public static string Pascal(string snake)
    {
        var builder = new StringBuilder(snake.Length);
        foreach (var part in snake.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(part[0])).Append(part.AsSpan(1));
        }

        return builder.ToString();
    }

    public static string Camel(string snake)
    {
        var pascal = Pascal(snake);
        return pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    /// <summary>English singular of the last word of a plural snake_case table name (users → user, companies → company).</summary>
    public static string Singular(string tableName)
    {
        var parts = tableName.Split('_');
        var last = parts[^1];
        parts[^1] = last switch
        {
            _ when last.EndsWith("ies", StringComparison.Ordinal) => last[..^3] + "y",
            _ when SibilantPlural().IsMatch(last) => last[..^2],
            _ when last.EndsWith('s') && !last.EndsWith("ss", StringComparison.Ordinal) => last[..^1],
            _ => last,
        };
        return string.Join('_', parts);
    }

    /// <summary>C# class name for a table: singular PascalCase (hrm.leave_requests → LeaveRequest).</summary>
    public static string EntityName(Table table) => Pascal(Singular(table.Name));

    /// <summary>C# property name for a column; never equal to the class name (CS0542).</summary>
    public static string PropertyName(Table table, Column column)
    {
        var name = Pascal(column.Name);
        return name == EntityName(table) ? name + "Value" : name;
    }

    public static string SqlString(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^lower\((\w+)\)$")]
    private static partial Regex LowerPattern();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWordPattern();

    [GeneratedRegex("(ss|x|ch|sh)es$")]
    private static partial Regex SibilantPlural();
}
