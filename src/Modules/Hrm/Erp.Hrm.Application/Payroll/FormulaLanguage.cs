using System.Globalization;
using Erp.Hrm.Contracts.Payroll;

namespace Erp.Hrm.Application.Payroll;

/// <summary>A parsed formula (slice S1.35 Build item 4, ruling Q1): the tree the validator checks and the engine evaluates.</summary>
public abstract record FormulaNode;

/// <summary>A decimal literal.</summary>
public sealed record FormulaNumber(decimal Value) : FormulaNode;

/// <summary>A component code: the component's amount in the calculation.</summary>
public sealed record FormulaComponent(string Code) : FormulaNode;

/// <summary>A named input (<c>$name</c>; <see cref="Name"/> is without the dollar sign).</summary>
public sealed record FormulaInput(string Name) : FormulaNode;

/// <summary>Unary minus.</summary>
public sealed record FormulaNegate(FormulaNode Operand) : FormulaNode;

/// <summary><c>+ - * /</c>.</summary>
public sealed record FormulaBinary(char Op, FormulaNode Left, FormulaNode Right) : FormulaNode;

/// <summary>A comparison (<c>&lt; &lt;= &gt; &gt;= = &lt;&gt;</c>), allowed only as the condition of <c>if</c>.</summary>
public sealed record FormulaCompare(string Op, FormulaNode Left, FormulaNode Right) : FormulaNode;

/// <summary>One of the functions <c>min</c>, <c>max</c>, <c>round</c> and <c>if</c>.</summary>
public sealed record FormulaCall(string Function, IReadOnlyList<FormulaNode> Args) : FormulaNode;

/// <summary>A formula that cannot be evaluated for an employee (division by zero, a value too large): a localization key.</summary>
public sealed class FormulaEvaluationException(string errorKey) : Exception(errorKey)
{
    public string ErrorKey { get; } = errorKey;
}

/// <summary>
/// The formula language of the payroll engine (slice S1.35 Build item 4, ruling Q1; completes the grammar of S1.34 Q3): numbers, component codes,
/// named inputs (<c>$worked_days</c>, <c>$paid_days</c>, <c>$period_days</c> and the other names of <see cref="PayInputNames"/>), <c>+ - * /</c>,
/// brackets, <c>min</c>, <c>max</c>, <c>round</c> and <c>if(condition, a, b)</c> whose condition is one comparison. No loops, no other functions,
/// no input or output; hand-written, no package. Arithmetic is decimal only: every operation is rounded half away from zero to four places
/// (numeric(19,4), ruling Q3); <c>round(x[, places])</c> rounds by the run's rounding mode. The same parser serves <see cref="FormulaValidator"/>
/// (at definition time) and the engine (at calculation), so a formula that passes the check is one the engine can read.
/// </summary>
public static class FormulaLanguage
{
    public const string Syntax = "errors.hrm.pay.formula_syntax";
    public const string InputUnknown = "errors.hrm.pay.formula_input_unknown";
    public const string Arguments = "errors.hrm.pay.formula_arguments";
    public const string Comparison = "errors.hrm.pay.formula_comparison";
    public const string RoundPlaces = "errors.hrm.pay.formula_round_places";
    public const string DivisionByZero = "errors.hrm.pay.formula_division_by_zero";
    public const string Overflow = "errors.hrm.pay.formula_overflow";

    /// <summary>The functions of the grammar (lower case; component codes cannot take these names).</summary>
    public static readonly IReadOnlySet<string> Functions = new HashSet<string>(["min", "max", "round", "if"], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Comparisons = ["<=", ">=", "<>", "<", ">", "="];

    private enum Kind
    {
        Number,
        Name,
        Input,
        Op,
        End,
    }

    private sealed record Token(Kind Kind, string Text, int Position);

    /// <summary>Parses a formula. The tree is null when there is any problem; problems name the token where the grammar stopped.</summary>
    public static (FormulaNode? Tree, IReadOnlyList<FormulaProblem> Problems) Parse(string? formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
        {
            return (null, [new FormulaProblem(FormulaValidator.Empty)]);
        }

        var (tokens, lexical) = Tokenize(formula);
        if (lexical is not null)
        {
            return (null, [lexical]);
        }

        var parser = new Parser(tokens);
        try
        {
            var tree = parser.Expression();
            if (parser.Peek.Kind != Kind.End)
            {
                return (null, [new FormulaProblem(Syntax, parser.Peek.Text)]);
            }

            var problems = new List<FormulaProblem>();
            CheckShape(tree, conditionAllowed: false, problems);
            return problems.Count == 0 ? (tree, []) : (null, problems.DistinctBy(p => (p.ErrorKey, p.Name)).ToList());
        }
        catch (FormatException e)
        {
            return (null, [new FormulaProblem(Syntax, e.Message)]);
        }
    }

    /// <summary>Every component code and named input a tree uses (inputs without the dollar sign).</summary>
    public static (IReadOnlySet<string> Components, IReadOnlySet<string> Inputs) Names(FormulaNode tree)
    {
        var components = new SortedSet<string>(StringComparer.Ordinal);
        var inputs = new SortedSet<string>(StringComparer.Ordinal);
        Walk(tree);
        return (components, inputs);

        void Walk(FormulaNode n)
        {
            switch (n)
            {
                case FormulaComponent c:
                    components.Add(c.Code);
                    break;
                case FormulaInput i:
                    inputs.Add(i.Name);
                    break;
                case FormulaNegate u:
                    Walk(u.Operand);
                    break;
                case FormulaBinary b:
                    Walk(b.Left);
                    Walk(b.Right);
                    break;
                case FormulaCompare c:
                    Walk(c.Left);
                    Walk(c.Right);
                    break;
                case FormulaCall f:
                    foreach (var a in f.Args)
                    {
                        Walk(a);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Evaluates a tree. <paramref name="component"/> and <paramref name="input"/> answer the value of a name; <paramref name="roundMode"/> is the
    /// run's mode for <c>round</c>; <paramref name="steps"/>, when given, receives each operation as text for the trace (S1.35b stores it).
    /// </summary>
    public static decimal Evaluate(FormulaNode tree, Func<string, decimal> component, Func<string, decimal> input, MidpointRounding roundMode, List<string>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return Eval(tree);
        }
        catch (OverflowException)
        {
            throw new FormulaEvaluationException(Overflow);
        }

        decimal Eval(FormulaNode n)
        {
            switch (n)
            {
                case FormulaNumber num:
                    return num.Value;
                case FormulaComponent c:
                    var cv = component(c.Code);
                    steps?.Add(Invariant($"{c.Code} = {cv}"));
                    return cv;
                case FormulaInput i:
                    var iv = input(i.Name);
                    steps?.Add(Invariant($"${i.Name} = {iv}"));
                    return iv;
                case FormulaNegate u:
                    return -Eval(u.Operand);
                case FormulaBinary b:
                    var l = Eval(b.Left);
                    var r = Eval(b.Right);
                    var value = b.Op switch
                    {
                        '+' => l + r,
                        '-' => l - r,
                        '*' => l * r,
                        '/' => r == 0 ? throw new FormulaEvaluationException(DivisionByZero) : l / r,
                        _ => throw new InvalidOperationException(b.Op.ToString()),
                    };
                    var rounded = PayRounding.Internal(value);
                    steps?.Add(Invariant($"{l} {b.Op} {r} = {rounded}"));
                    return rounded;
                case FormulaCall { Function: "min" } f:
                    return f.Args.Select(Eval).Min();
                case FormulaCall { Function: "max" } f:
                    return f.Args.Select(Eval).Max();
                case FormulaCall { Function: "round" } f:
                    var x = Eval(f.Args[0]);
                    var places = f.Args.Count > 1 ? (int)((FormulaNumber)f.Args[1]).Value : 0;
                    var r2 = decimal.Round(x, places, roundMode);
                    steps?.Add(Invariant($"round({x}, {places}) = {r2}"));
                    return r2;
                case FormulaCall { Function: "if" } f:
                    var cmp = (FormulaCompare)f.Args[0];
                    var a = Eval(cmp.Left);
                    var c2 = Eval(cmp.Right);
                    var holds = cmp.Op switch
                    {
                        "<" => a < c2,
                        "<=" => a <= c2,
                        ">" => a > c2,
                        ">=" => a >= c2,
                        "=" => a == c2,
                        "<>" => a != c2,
                        _ => throw new InvalidOperationException(cmp.Op),
                    };
                    steps?.Add(Invariant($"if({a} {cmp.Op} {c2}) is {(holds ? "true" : "false")}"));
                    return Eval(f.Args[holds ? 1 : 2]); // only the branch taken is evaluated
                default:
                    throw new InvalidOperationException(n.GetType().Name);
            }
        }
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>Comparisons only as the condition of <c>if</c>; the arity of each function; <c>round</c>'s places a whole number 0 to 4.</summary>
    private static void CheckShape(FormulaNode n, bool conditionAllowed, List<FormulaProblem> problems)
    {
        switch (n)
        {
            case FormulaCompare c:
                if (!conditionAllowed)
                {
                    problems.Add(new FormulaProblem(Comparison, c.Op));
                }

                CheckShape(c.Left, false, problems);
                CheckShape(c.Right, false, problems);
                break;
            case FormulaNegate u:
                CheckShape(u.Operand, false, problems);
                break;
            case FormulaBinary b:
                CheckShape(b.Left, false, problems);
                CheckShape(b.Right, false, problems);
                break;
            case FormulaCall f:
                var ok = f.Function switch
                {
                    "min" or "max" => f.Args.Count >= 2,
                    "round" => f.Args.Count is 1 or 2,
                    "if" => f.Args.Count == 3 && f.Args[0] is FormulaCompare,
                    _ => false,
                };
                if (!ok)
                {
                    problems.Add(new FormulaProblem(Arguments, f.Function));
                }

                if (f.Function == "round" && f.Args.Count == 2 && !(f.Args[1] is FormulaNumber { Value: >= 0 and <= 4 } places && places.Value == decimal.Truncate(places.Value)))
                {
                    problems.Add(new FormulaProblem(RoundPlaces, "round"));
                }

                for (var i = 0; i < f.Args.Count; i++)
                {
                    CheckShape(f.Args[i], f.Function == "if" && i == 0, problems);
                }

                break;
        }
    }

    private static (List<Token> Tokens, FormulaProblem? Problem) Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsAsciiDigit(ch))
            {
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    i++;
                }

                if (i < s.Length && s[i] == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
                {
                    i++;
                    while (i < s.Length && char.IsAsciiDigit(s[i]))
                    {
                        i++;
                    }
                }

                tokens.Add(new Token(Kind.Number, s[start..i], start));
                continue;
            }

            if (char.IsAsciiLetter(ch) || (ch == '$' && i + 1 < s.Length && char.IsAsciiLetter(s[i + 1])))
            {
                i++;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(ch == '$' ? Kind.Input : Kind.Name, s[start..i], start));
                continue;
            }

            var two = i + 1 < s.Length ? s.Substring(i, 2) : null;
            if (two is "<=" or ">=" or "<>")
            {
                tokens.Add(new Token(Kind.Op, two, start));
                i += 2;
                continue;
            }

            if ("+-*/(),<>=".Contains(ch, StringComparison.Ordinal))
            {
                tokens.Add(new Token(Kind.Op, ch.ToString(), start));
                i++;
                continue;
            }

            return (tokens, new FormulaProblem(FormulaValidator.Token, ch.ToString()));
        }

        tokens.Add(new Token(Kind.End, "end", s.Length));
        return (tokens, null);
    }

    /// <summary>
    /// Recursive descent: expression = additive [comparison additive]; additive = term {(+|-) term}; term = unary {(*|/) unary};
    /// unary = - unary | primary; primary = number | name | $name | function ( expression {, expression} ) | ( expression ).
    /// </summary>
    private sealed class Parser(List<Token> tokens)
    {
        private const int MaxDepth = 64;
        private int _at;
        private int _depth;

        public Token Peek => tokens[_at];

        private Token Next() => tokens[_at++];

        private bool Accept(string op)
        {
            if (Peek.Kind == Kind.Op && Peek.Text == op)
            {
                _at++;
                return true;
            }

            return false;
        }

        private void Expect(string op)
        {
            if (!Accept(op))
            {
                throw new FormatException(Peek.Text);
            }
        }

        public FormulaNode Expression()
        {
            if (++_depth > MaxDepth)
            {
                throw new FormatException(Peek.Text);
            }

            var left = Additive();
            if (Peek.Kind == Kind.Op && Comparisons.Contains(Peek.Text, StringComparer.Ordinal))
            {
                var op = Next().Text;
                left = new FormulaCompare(op, left, Additive());
            }

            _depth--;
            return left;
        }

        private FormulaNode Additive()
        {
            var left = Term();
            while (Peek.Kind == Kind.Op && Peek.Text is "+" or "-")
            {
                var op = Next().Text[0];
                left = new FormulaBinary(op, left, Term());
            }

            return left;
        }

        private FormulaNode Term()
        {
            var left = Unary();
            while (Peek.Kind == Kind.Op && Peek.Text is "*" or "/")
            {
                var op = Next().Text[0];
                left = new FormulaBinary(op, left, Unary());
            }

            return left;
        }

        private FormulaNode Unary()
        {
            if (Accept("-"))
            {
                if (++_depth > MaxDepth)
                {
                    throw new FormatException(Peek.Text);
                }

                var operand = Unary();
                _depth--;
                return new FormulaNegate(operand);
            }

            return Primary();
        }

        private FormulaNode Primary()
        {
            var t = Next();
            switch (t.Kind)
            {
                case Kind.Number:
                    return decimal.TryParse(t.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? new FormulaNumber(value) : throw new FormatException(t.Text);
                case Kind.Input:
                    return new FormulaInput(t.Text[1..]);
                case Kind.Name when Peek.Kind == Kind.Op && Peek.Text == "(":
                    if (!Functions.Contains(t.Text))
                    {
                        throw new FormatException(t.Text + "(");
                    }

                    _at++;
                    var args = new List<FormulaNode> { Expression() };
                    while (Accept(","))
                    {
                        args.Add(Expression());
                    }

                    Expect(")");
                    return new FormulaCall(t.Text.ToLowerInvariant(), args);
                case Kind.Name:
                    return new FormulaComponent(t.Text);
                case Kind.Op when t.Text == "(":
                    var inner = Expression();
                    Expect(")");
                    return inner;
                default:
                    throw new FormatException(t.Text);
            }
        }
    }
}
