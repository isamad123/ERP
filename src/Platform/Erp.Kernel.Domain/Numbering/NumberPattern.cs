using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Erp.Kernel.Domain.Numbering;

/// <summary>Reset rules of a numbering scheme (FR-KRN-NUM-01; <c>fiscal_yearly</c> added by S1.08 Q5, CR-033).</summary>
#if ERP_CODEGEN
internal static class ResetRules
#else
public static class ResetRules
#endif
{
    public const string Never = "never";
    public const string Yearly = "yearly";
    public const string Monthly = "monthly";
    public const string FiscalYearly = "fiscal_yearly";

    public static IReadOnlyList<string> All { get; } = [Never, Yearly, Monthly, FiscalYearly];
}

/// <summary>What a document type offers its patterns: a company code and a branch code (Q4).</summary>
/// <param name="HasCompany">False for tenant-wide document types (person number, Q6): <c>{company}</c> is refused.</param>
/// <param name="HasBranch">False when the document has no branch: <c>{branch}</c> is refused.</param>
/// <param name="CompanyCodeLength">Length of the company code the number will carry (for the 40-character limit).</param>
/// <param name="BranchCodeLength">Length of the branch code the number will carry.</param>
/// <param name="BranchScheme">True for a scheme of one branch. Only those may use <c>{branch}</c>: a company-wide scheme
/// printing branch codes would share its numbers with a later scheme of that branch (S1.08 review finding 1, CR-058).</param>
#if ERP_CODEGEN
internal sealed record PatternContext
#else
public sealed record PatternContext
#endif
    (bool HasCompany, bool HasBranch, int CompanyCodeLength = 0, int BranchCodeLength = 0, bool BranchScheme = false);

/// <summary>Values one number is rendered with.</summary>
/// <param name="Date">The document's business date (Q5); it decides the period.</param>
/// <param name="FiscalYearStartMonth">1 to 12; the company's setting (Q5).</param>
/// <param name="CompanyCode">Code printed for <c>{company}</c>.</param>
/// <param name="BranchCode">Code printed for <c>{branch}</c>.</param>
#if ERP_CODEGEN
internal sealed record NumberContext
#else
public sealed record NumberContext
#endif
    (DateOnly Date, int FiscalYearStartMonth, string? CompanyCode, string? BranchCode);

/// <summary>A pattern or number the rules refuse; <see cref="Exception.Message"/> is a localization key.</summary>
#if ERP_CODEGEN
internal sealed class NumberPatternException
#else
public sealed class NumberPatternException
#endif
    (string errorKey) : Exception(errorKey)
{
    public string ErrorKey { get; } = errorKey;
}

/// <summary>
/// A numbering pattern (slice S1.08 ruling Q4): <c>{seq:N}</c> (the running number, at least N digits, N from 1 to 12,
/// exactly once), <c>{yyyy}</c>, <c>{yy}</c>, <c>{mm}</c>, <c>{fy}</c> (fiscal year, <c>2026-27</c>; <c>2026</c> when the
/// fiscal year starts in January), <c>{company}</c> and <c>{branch}</c> (codes); every other character is literal text.
/// Numbers always use Latin digits, also in the Urdu interface. A number is at most 40 characters.
/// </summary>
/// <remarks>
/// Pure code with no dependencies: the code generator compiles the same file (as internal types, symbol ERP_CODEGEN) to
/// check default patterns by the same rules.
/// </remarks>
#if ERP_CODEGEN
internal sealed partial class NumberPattern
#else
public sealed partial class NumberPattern
#endif
{
    public const int MaxLength = 40;
    public const int MaxSeqDigits = 12;

    private enum Kind
    {
        Literal,
        Seq,
        Year4,
        Year2,
        Month,
        FiscalYear,
        Company,
        Branch,
    }

    private sealed record Part(Kind Kind, string Text, int Digits = 0);

    private readonly IReadOnlyList<Part> _parts;

    private NumberPattern(string text, IReadOnlyList<Part> parts)
    {
        Text = text;
        _parts = parts;
        SeqDigits = parts.First(p => p.Kind == Kind.Seq).Digits;
    }

    public string Text { get; }

    /// <summary>The N of <c>{seq:N}</c>.</summary>
    public int SeqDigits { get; }

    public bool UsesCompany => _parts.Any(p => p.Kind == Kind.Company);

    public bool UsesBranch => _parts.Any(p => p.Kind == Kind.Branch);

    /// <summary>
    /// The pattern with its padding taken out: <c>{seq:N}</c> becomes <c>{seq}</c> and the digits of the literal text
    /// right before and after it are dropped, because they print like digits of the running number
    /// (<c>EMP-0{seq:4}</c> and <c>EMP-{seq:5}</c> both print <c>EMP-00001</c>). Two patterns of the same shape can print
    /// the same numbers (CR-058, S1.08 review finding 1).
    /// </summary>
    public string Shape
    {
        get
        {
            var b = new StringBuilder();
            for (var i = 0; i < _parts.Count; i++)
            {
                var part = _parts[i];
                var text = part.Kind switch
                {
                    Kind.Literal => part.Text,
                    Kind.Seq => "{seq}",
                    _ => "{" + part.Text + "}",
                };
                if (part.Kind == Kind.Literal && i + 1 < _parts.Count && _parts[i + 1].Kind == Kind.Seq)
                {
                    text = text.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                }

                if (part.Kind == Kind.Literal && i > 0 && _parts[i - 1].Kind == Kind.Seq)
                {
                    text = text.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                }

                b.Append(text);
            }

            return b.ToString();
        }
    }

    /// <summary>Reads a pattern; refuses unknown tokens, unbalanced braces, control characters and a missing or repeated <c>{seq:N}</c>.</summary>
    public static NumberPattern Parse(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new NumberPatternException("errors.numbering.pattern_empty");
        }

        if (pattern.Any(char.IsControl))
        {
            throw new NumberPatternException("errors.numbering.pattern_control_character");
        }

        var parts = new List<Part>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '}')
            {
                throw new NumberPatternException("errors.numbering.pattern_brace");
            }

            if (c != '{')
            {
                literal.Append(c);
                i++;
                continue;
            }

            var end = pattern.IndexOf('}', i + 1);
            var nextOpen = pattern.IndexOf('{', i + 1);
            if (end < 0 || (nextOpen >= 0 && nextOpen < end))
            {
                throw new NumberPatternException("errors.numbering.pattern_brace");
            }

            if (literal.Length > 0)
            {
                parts.Add(new Part(Kind.Literal, literal.ToString()));
                literal.Clear();
            }

            parts.Add(Token(pattern[(i + 1)..end]));
            i = end + 1;
        }

        if (literal.Length > 0)
        {
            parts.Add(new Part(Kind.Literal, literal.ToString()));
        }

        if (parts.Count(p => p.Kind == Kind.Seq) != 1)
        {
            throw new NumberPatternException("errors.numbering.pattern_seq_count");
        }

        return new NumberPattern(pattern, parts);
    }

    private static Part Token(string token)
    {
        switch (token)
        {
            case "yyyy":
                return new Part(Kind.Year4, token);
            case "yy":
                return new Part(Kind.Year2, token);
            case "mm":
                return new Part(Kind.Month, token);
            case "fy":
                return new Part(Kind.FiscalYear, token);
            case "company":
                return new Part(Kind.Company, token);
            case "branch":
                return new Part(Kind.Branch, token);
            default:
                var seq = SeqToken().Match(token);
                if (!seq.Success)
                {
                    throw new NumberPatternException("errors.numbering.pattern_token_unknown");
                }

                var digits = int.Parse(seq.Groups[1].Value, CultureInfo.InvariantCulture);
                return digits is < 1 or > MaxSeqDigits
                    ? throw new NumberPatternException("errors.numbering.pattern_seq_digits")
                    : new Part(Kind.Seq, token, digits);
        }
    }

    /// <summary>
    /// Every rule of Q4 for one scheme: the pattern parses; a yearly series has a year token, a monthly one a year and a
    /// month token, a fiscal-yearly one <c>{fy}</c> (otherwise the same number would be issued again next period);
    /// <c>{company}</c> and <c>{branch}</c> only where the document type has them; at most 40 characters.
    /// </summary>
    /// <returns>The parsed pattern.</returns>
    public static NumberPattern Validate(string? pattern, string? resetRule, PatternContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (resetRule is null || !ResetRules.All.Contains(resetRule))
        {
            throw new NumberPatternException("errors.numbering.reset_rule_unknown");
        }

        var parsed = Parse(pattern);
        var hasYear = parsed._parts.Any(p => p.Kind is Kind.Year4 or Kind.Year2);
        var hasMonth = parsed._parts.Any(p => p.Kind == Kind.Month);
        var hasFiscal = parsed._parts.Any(p => p.Kind == Kind.FiscalYear);
        switch (resetRule)
        {
            case ResetRules.Yearly when !hasYear:
                throw new NumberPatternException("errors.numbering.pattern_year_needed");
            case ResetRules.Monthly when !hasYear || !hasMonth:
                throw new NumberPatternException("errors.numbering.pattern_month_needed");
            case ResetRules.FiscalYearly when !hasFiscal:
                throw new NumberPatternException("errors.numbering.pattern_fiscal_year_needed");
            default:
                break;
        }

        if (parsed.UsesCompany && !context.HasCompany)
        {
            throw new NumberPatternException("errors.numbering.pattern_company_not_allowed");
        }

        if (parsed.UsesBranch && !context.HasBranch)
        {
            throw new NumberPatternException("errors.numbering.pattern_branch_not_allowed");
        }

        // CR-058 (S1.08 review finding 1): branch codes only in a branch's own scheme, and every code followed by a
        // separator, so that one code (K) can never run into another code's characters (K1) or into the digits.
        if (parsed.UsesBranch && !context.BranchScheme)
        {
            throw new NumberPatternException("errors.numbering.pattern_branch_company_scheme");
        }

        for (var i = 0; i < parsed._parts.Count; i++)
        {
            if (parsed._parts[i].Kind is Kind.Company or Kind.Branch && i + 1 < parsed._parts.Count
                && (parsed._parts[i + 1] is not { Kind: Kind.Literal } next || char.IsLetterOrDigit(next.Text[^1])))
            {
                throw new NumberPatternException("errors.numbering.pattern_code_separator");
            }
        }

        var length = parsed._parts.Sum(p => p.Kind switch
        {
            Kind.Literal => p.Text.Length,
            Kind.Seq => p.Digits,
            Kind.Year4 => 4,
            Kind.Year2 or Kind.Month => 2,
            Kind.FiscalYear => 7,
            Kind.Company => context.CompanyCodeLength,
            Kind.Branch => context.BranchCodeLength,
            _ => 0,
        });
        return length > MaxLength ? throw new NumberPatternException("errors.numbering.pattern_too_long") : parsed;
    }

    /// <summary>First year of the fiscal year that contains <paramref name="date"/>.</summary>
    public static int FiscalYearStart(DateOnly date, int startMonth)
    {
        CheckMonth(startMonth);
        return date.Month >= startMonth ? date.Year : date.Year - 1;
    }

    /// <summary>The fiscal-year label: <c>2026-27</c>, or <c>2026</c> when the fiscal year is the calendar year.</summary>
    public static string FiscalYearLabel(DateOnly date, int startMonth)
    {
        var start = FiscalYearStart(date, startMonth);
        return startMonth == 1
            ? start.ToString("0000", CultureInfo.InvariantCulture)
            : start.ToString("0000", CultureInfo.InvariantCulture) + "-" + ((start + 1) % 100).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The period a document falls in (Q5), which is also the counter's scope key: <c>all</c> (never), <c>2026</c>
    /// (calendar year), <c>2026-07</c> (month) or <c>fy2026</c> (fiscal year starting in 2026).
    /// </summary>
    public static string ScopeKey(string resetRule, DateOnly date, int fiscalYearStartMonth) => resetRule switch
    {
        ResetRules.Never => "all",
        ResetRules.Yearly => date.Year.ToString("0000", CultureInfo.InvariantCulture),
        ResetRules.Monthly => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        ResetRules.FiscalYearly => "fy" + FiscalYearStart(date, fiscalYearStartMonth).ToString("0000", CultureInfo.InvariantCulture),
        _ => throw new NumberPatternException("errors.numbering.reset_rule_unknown"),
    };

    /// <summary>The number for running value <paramref name="value"/>; a value wider than N digits prints in full.</summary>
    public string Render(long value, NumberContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        var b = new StringBuilder();
        foreach (var part in _parts)
        {
            b.Append(part.Kind switch
            {
                Kind.Literal => part.Text,
                Kind.Seq => value.ToString(CultureInfo.InvariantCulture).PadLeft(part.Digits, '0'),
                Kind.Year4 => context.Date.Year.ToString("0000", CultureInfo.InvariantCulture),
                Kind.Year2 => (context.Date.Year % 100).ToString("00", CultureInfo.InvariantCulture),
                Kind.Month => context.Date.Month.ToString("00", CultureInfo.InvariantCulture),
                Kind.FiscalYear => FiscalYearLabel(context.Date, context.FiscalYearStartMonth),
                Kind.Company => context.CompanyCode ?? throw new NumberPatternException("errors.numbering.pattern_company_not_allowed"),
                Kind.Branch => context.BranchCode ?? throw new NumberPatternException("errors.numbering.pattern_branch_not_allowed"),
                _ => string.Empty,
            });
        }

        return b.Length > MaxLength ? throw new NumberPatternException("errors.numbering.number_too_long") : b.ToString();
    }

    /// <summary>
    /// Whether this pattern can produce <paramref name="number"/> (letter case ignored, as the documents' unique keys
    /// are), and if so with which running value and in which period (manual override check, Q9). The date tokens of the
    /// number decide the period; a number the pattern would print differently (for example <c>EMP-042</c> for
    /// <c>EMP-{seq:5}</c>) can never be produced and does not match.
    /// </summary>
    public bool TryMatch(string number, string resetRule, int fiscalYearStartMonth, string? companyCode, string? branchCode, DateOnly today, out long value, out string scopeKey)
    {
        ArgumentNullException.ThrowIfNull(number);
        value = 0;
        scopeKey = string.Empty;
        var regex = new StringBuilder("^");
        foreach (var part in _parts)
        {
            regex.Append(part.Kind switch
            {
                Kind.Literal => Regex.Escape(part.Text),
                Kind.Seq => "(?<seq>[0-9]{1,18})",
                Kind.Year4 => "(?<y4>[0-9]{4})",
                Kind.Year2 => "(?<y2>[0-9]{2})",
                Kind.Month => "(?<mm>0[1-9]|1[0-2])",
                Kind.FiscalYear => fiscalYearStartMonth == 1 ? "(?<fy>[0-9]{4})" : "(?<fy>[0-9]{4})-[0-9]{2}",
                Kind.Company => Regex.Escape(companyCode ?? string.Empty),
                Kind.Branch => Regex.Escape(branchCode ?? string.Empty),
                _ => string.Empty,
            });
        }

        regex.Append('$');
        var match = Regex.Match(number, regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || !long.TryParse(match.Groups["seq"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        int? year = match.Groups["y4"].Success ? int.Parse(match.Groups["y4"].Value, CultureInfo.InvariantCulture)
            : match.Groups["y2"].Success ? 2000 + int.Parse(match.Groups["y2"].Value, CultureInfo.InvariantCulture) : null;
        int? month = match.Groups["mm"].Success ? int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture) : null;
        if (match.Groups["fy"].Success)
        {
            var start = int.Parse(match.Groups["fy"].Value, CultureInfo.InvariantCulture);
            month ??= fiscalYearStartMonth;
            var inFiscal = month.Value >= fiscalYearStartMonth ? start : start + 1;
            if (year is not null && year != inFiscal)
            {
                return false;
            }

            year = inFiscal;
        }

        var date = new DateOnly(year ?? today.Year, month ?? (year is null ? today.Month : 1), 1);
        var rendered = Render(value, new NumberContext(date, fiscalYearStartMonth, companyCode, branchCode));
        if (!string.Equals(rendered, number, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        scopeKey = ScopeKey(resetRule, date, fiscalYearStartMonth);
        return true;
    }

    private static void CheckMonth(int month)
    {
        if (month is < 1 or > 12)
        {
            throw new NumberPatternException("errors.numbering.fiscal_month_invalid");
        }
    }

    [GeneratedRegex("^seq:([0-9]{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex SeqToken();
}
