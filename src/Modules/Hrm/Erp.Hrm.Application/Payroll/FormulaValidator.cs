using System.Text.RegularExpressions;
using Erp.Hrm.Contracts.Payroll;

namespace Erp.Hrm.Application.Payroll;

/// <summary>
/// The <see cref="IFormulaValidator"/> of the payroll engine (slice S1.34b names and cycles; completed by S1.35a, Build item 4, ruling Q1): pure, no
/// database and no package. A first pass reads the characters (component codes are identifiers, named inputs are <c>$identifier</c>, numbers are
/// decimals) and checks names and brackets as S1.34b did; then the formula is parsed by <see cref="FormulaLanguage"/>, the same parser the engine
/// evaluates with, so operand and operator order, the arity of <c>min</c>, <c>max</c>, <c>round</c> and <c>if</c>, comparisons only as a condition
/// of <c>if</c>, and named inputs from the catalogue (<see cref="PayInputNames"/> and the names providers register, R-282) are all checked when the
/// formula is saved. Nothing is evaluated here.
/// </summary>
public sealed partial class FormulaValidator : IFormulaValidator
{
    public const string Empty = "errors.hrm.pay.formula_empty";
    public const string Token = "errors.hrm.pay.formula_token";
    public const string Brackets = "errors.hrm.pay.formula_brackets";
    public const string UnknownName = "errors.hrm.pay.formula_name_unknown";
    public const string Cycle = "errors.hrm.pay.formula_cycle";
    public const int MaxLength = 2000;

    private readonly HashSet<string> _inputs;

    /// <summary>A validator knowing the built-in named inputs and those of <paramref name="providerNames"/> (registered by later input providers).</summary>
    public FormulaValidator(IEnumerable<PayInputNameSet>? providerNames = null)
    {
        _inputs = new HashSet<string>(PayInputNames.BuiltIn, StringComparer.Ordinal);
        foreach (var set in providerNames ?? [])
        {
            _inputs.UnionWith(set.Names);
        }
    }

    /// <summary>The named inputs a formula may use (without the dollar sign), in order.</summary>
    public IReadOnlyList<string> KnownInputs => [.. _inputs.Order(StringComparer.Ordinal)];

    private static IReadOnlySet<string> Functions => FormulaLanguage.Functions;

    [GeneratedRegex(@"\G(?:(?<num>\d+(?:\.\d+)?)|(?<input>\$[A-Za-z][A-Za-z0-9_]*)|(?<id>[A-Za-z][A-Za-z0-9_]*)|(?<op>[+\-*/(),<>=])|(?<ws>\s+))")]
    private static partial Regex TokenPattern();

    public FormulaCheck Check(string? formula, IReadOnlySet<string> knownComponentCodes)
    {
        ArgumentNullException.ThrowIfNull(knownComponentCodes);
        var components = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<FormulaProblem>();
        if (string.IsNullOrWhiteSpace(formula))
        {
            problems.Add(new FormulaProblem(Empty));
            return new FormulaCheck(components, inputs, problems);
        }

        if (formula.Length > MaxLength)
        {
            problems.Add(new FormulaProblem(Token, "length"));
            return new FormulaCheck(components, inputs, problems);
        }

        var depth = 0;
        var position = 0;
        var balanced = true;
        while (position < formula.Length)
        {
            var m = TokenPattern().Match(formula, position);
            if (!m.Success)
            {
                problems.Add(new FormulaProblem(Token, formula[position].ToString()));
                break;
            }

            position += m.Length;
            if (m.Groups["id"].Success)
            {
                var name = m.Groups["id"].Value;
                var isCall = position < formula.Length && formula[position..].TrimStart().StartsWith('(');
                if (isCall && Functions.Contains(name))
                {
                    continue;
                }

                if (isCall)
                {
                    problems.Add(new FormulaProblem(Token, name + "("));
                }
                else if (knownComponentCodes.Contains(name))
                {
                    components.Add(name);
                }
                else
                {
                    problems.Add(new FormulaProblem(UnknownName, name));
                }
            }
            else if (m.Groups["input"].Success)
            {
                inputs.Add(m.Groups["input"].Value);
            }
            else if (m.Groups["op"].Success)
            {
                switch (m.Groups["op"].Value)
                {
                    case "(":
                        depth++;
                        break;
                    case ")":
                        depth--;
                        balanced &= depth >= 0;
                        break;
                }
            }
        }

        if (!balanced || depth != 0)
        {
            problems.Add(new FormulaProblem(Brackets));
        }

        foreach (var input in inputs.Where(i => !_inputs.Contains(i[1..])).Order(StringComparer.Ordinal))
        {
            problems.Add(new FormulaProblem(FormulaLanguage.InputUnknown, input));
        }

        // S1.35a: the grammar itself (operand and operator order, arity, comparisons), by the engine's own parser.
        if (problems.Count == 0)
        {
            problems.AddRange(FormulaLanguage.Parse(formula).Problems);
        }

        return new FormulaCheck(components, inputs, [.. problems.DistinctBy(p => (p.ErrorKey, p.Name))]);
    }

    public IReadOnlyList<string>? FindCycle(IReadOnlyDictionary<string, IReadOnlySet<string>> uses)
    {
        ArgumentNullException.ThrowIfNull(uses);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on the walk, 2 = done
        var path = new List<string>();
        foreach (var start in uses.Keys.Order(StringComparer.Ordinal))
        {
            if (Visit(start) is { } found)
            {
                return found;
            }
        }

        return null;

        IReadOnlyList<string>? Visit(string node)
        {
            if (state.TryGetValue(node, out var s))
            {
                if (s == 1)
                {
                    var from = path.IndexOf(node);
                    return [.. path[from..], node];
                }

                return null;
            }

            state[node] = 1;
            path.Add(node);
            if (uses.TryGetValue(node, out var next))
            {
                foreach (var n in next.Order(StringComparer.Ordinal))
                {
                    if (Visit(n) is { } found)
                    {
                        return found;
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            state[node] = 2;
            return null;
        }
    }
}
