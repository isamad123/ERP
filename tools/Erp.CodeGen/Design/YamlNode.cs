using YamlDotNet.Serialization;

namespace Erp.CodeGen.Design;

/// <summary>Reads YAML into plain lists, maps and strings and offers strict accessors with clear errors.</summary>
public static class YamlNode
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    public static object? Load(string path) => Deserializer.Deserialize<object?>(File.ReadAllText(path));

    public static IReadOnlyDictionary<string, object?> Map(object? node, string where) =>
        node switch
        {
            null => new Dictionary<string, object?>(),
            Dictionary<object, object?> map => map.ToDictionary(kv => (string)kv.Key, kv => kv.Value, StringComparer.Ordinal),
            _ => throw new DesignException(where, "expected a mapping"),
        };

    public static IReadOnlyList<object?> List(object? node, string where) =>
        node switch
        {
            null => [],
            List<object?> list => list,
            _ => throw new DesignException(where, "expected a list"),
        };

    public static string Text(object? node, string where) =>
        node as string ?? throw new DesignException(where, "expected text");

    public static IReadOnlyList<string> Texts(object? node, string where) =>
        List(node, where).Select(x => Text(x, where)).ToList();

    public static string? OptionalText(IReadOnlyDictionary<string, object?> map, string key, string where) =>
        map.TryGetValue(key, out var value) && value is not null ? Text(value, where + "." + key) : null;
}
