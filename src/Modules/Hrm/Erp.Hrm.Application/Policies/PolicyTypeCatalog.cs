using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Hrm.Contracts.Policies;

namespace Erp.Hrm.Application.Policies;

/// <summary>
/// One registered policy type (slice S1.23a Build item 1): code, names, parameter schema, the attributes its rules may use, whether
/// one policy or several apply, and whether every current employee should be covered (the activation check warns otherwise).
/// </summary>
public sealed record PolicyType(
    string Code, string Module, IReadOnlyDictionary<string, string> Name, IReadOnlyDictionary<string, string> Description, string Cardinality,
    bool Required, IReadOnlyList<string> Attributes, JsonElement Schema)
{
    public const string One = "one";
    public const string Many = "many";

    public bool IsMany => Cardinality == Many;
}

/// <summary>
/// A module's check of a policy type's parameters beyond the JSON Schema subset (S1.32a, CR-086 S1.32a detail): what a model needs, values
/// that repeat. Registered as a singleton beside the type's manifest; the catalogue runs it after the schema.
/// </summary>
public interface IPolicyParameterCheck
{
    string PolicyType { get; }

    /// <summary>Path (as <see cref="PolicySchema.Check"/> writes it) to error keys; empty when valid.</summary>
    IReadOnlyDictionary<string, string[]> Check(JsonElement parameters);
}

/// <summary>
/// Every policy type the modules registered (<see cref="PolicyTypeSource"/>, D-10): read once, checked strictly (a duplicate code, an
/// unknown attribute or an unsupported schema keyword stops the host), then read-only.
/// </summary>
public sealed partial class PolicyTypeCatalog
{
    private readonly Dictionary<string, PolicyType> types;
    private readonly Dictionary<string, List<IPolicyParameterCheck>> checks = new(StringComparer.Ordinal);

    /// <summary>The catalogue of the sources, with the modules' checks beyond the schema (S1.32a: the <c>leave</c> type).</summary>
    public PolicyTypeCatalog(IEnumerable<PolicyTypeSource> sources, IEnumerable<IPolicyParameterCheck>? parameterChecks = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        types = new Dictionary<string, PolicyType>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            using var doc = JsonDocument.Parse(source.Json);
            var root = doc.RootElement;
            var module = root.GetProperty("module").GetString() ?? throw new InvalidOperationException("policy-types.json needs a module.");
            foreach (var t in root.GetProperty("types").EnumerateArray())
            {
                var code = t.GetProperty("code").GetString()!;
                if (!CodeShape().IsMatch(code))
                {
                    throw new InvalidOperationException($"policy type '{code}': codes are lower-case letters, digits and underscores, starting with a letter, at most 40 characters.");
                }

                var cardinality = t.TryGetProperty("cardinality", out var c) ? c.GetString()! : PolicyType.One;
                if (cardinality is not (PolicyType.One or PolicyType.Many))
                {
                    throw new InvalidOperationException($"policy type '{code}': cardinality is one or many.");
                }

                var attributes = t.GetProperty("attributes").EnumerateArray().Select(a => a.GetString()!).ToList();
                if (attributes.Count == 0 || attributes.Any(a => !PolicyRules.Attributes.Contains(a)) || attributes.Distinct(StringComparer.Ordinal).Count() != attributes.Count)
                {
                    throw new InvalidOperationException($"policy type '{code}': attributes are some of {string.Join(", ", PolicyRules.Attributes)}.");
                }

                var schema = t.GetProperty("schema").Clone();
                PolicySchema.EnsureSupported(schema, "policy type " + code);
                if (schema.GetProperty("type").GetString() != "object")
                {
                    throw new InvalidOperationException($"policy type '{code}': the parameters are an object.");
                }

                var type = new PolicyType(code, module, Texts(t.GetProperty("name")), t.TryGetProperty("description", out var d) ? Texts(d) : new Dictionary<string, string>(),
                    cardinality, t.TryGetProperty("required", out var r) && r.GetBoolean(), attributes, schema);
                if (type.Name.GetValueOrDefault("en") is not { Length: > 0 } || type.Name.GetValueOrDefault("ur") is not { Length: > 0 })
                {
                    throw new InvalidOperationException($"policy type '{code}': a name in English and Urdu is required.");
                }

                if (!types.TryAdd(code, type))
                {
                    throw new InvalidOperationException($"policy type '{code}' is registered twice.");
                }
            }
        }

        foreach (var check in parameterChecks ?? [])
        {
            if (!types.ContainsKey(check.PolicyType))
            {
                throw new InvalidOperationException($"a parameter check names policy type '{check.PolicyType}', which no module registered.");
            }

            if (!checks.TryGetValue(check.PolicyType, out var list))
            {
                checks[check.PolicyType] = list = [];
            }

            list.Add(check);
        }
    }

    /// <summary>
    /// The problems of a type's parameters, path to error keys (empty when valid): the schema first, then, when the shape is right, the
    /// module's own checks of what a schema cannot say (S1.32a). Every writer of a policy version asks this.
    /// </summary>
    public Dictionary<string, string[]> Check(PolicyType type, JsonElement parameters)
    {
        ArgumentNullException.ThrowIfNull(type);
        var errors = PolicySchema.Check(type.Schema, parameters);
        if (errors.Count == 0 && checks.TryGetValue(type.Code, out var list))
        {
            foreach (var (path, keys) in list.SelectMany(c => c.Check(parameters)))
            {
                errors[path] = [.. errors.GetValueOrDefault(path, []).Concat(keys).Distinct(StringComparer.Ordinal)];
            }
        }

        return errors;
    }

    public IReadOnlyList<PolicyType> All => types.Values.OrderBy(t => t.Code, StringComparer.Ordinal).ToList();

    public PolicyType? Find(string? code) => code is not null && types.TryGetValue(code, out var type) ? type : null;

    private static Dictionary<string, string> Texts(JsonElement e) =>
        e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex CodeShape();
}
