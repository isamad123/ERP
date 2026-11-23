using System.Globalization;

namespace Erp.Hrm.Application.Employments;

/// <summary>
/// IBAN checks (ISO 13616; slice S1.20 Build item 14, FR-HRM-EMP-06): letters and digits only, the country code first, the length of
/// that country where the IBAN registry gives one (else 15 to 34 characters), and the check digits (remainder 1 modulo 97). The
/// lengths are the registry's technical format, not statutory content.
/// </summary>
public static class Iban
{
    public const string Invalid = "errors.hrm.bank_account.iban_invalid";

    /// <summary>IBAN lengths of the ISO 13616 registry for the countries this product serves first and their neighbours.</summary>
    private static readonly Dictionary<string, int> Lengths = new(StringComparer.Ordinal)
    {
        ["AE"] = 23, ["AT"] = 20, ["BE"] = 16, ["BH"] = 22, ["CH"] = 21, ["DE"] = 22, ["DK"] = 18, ["EG"] = 29, ["ES"] = 24, ["FI"] = 18,
        ["FR"] = 27, ["GB"] = 22, ["IE"] = 22, ["IT"] = 27, ["JO"] = 30, ["KW"] = 30, ["LB"] = 28, ["LU"] = 20, ["NL"] = 18, ["NO"] = 15,
        ["OM"] = 23, ["PK"] = 24, ["PL"] = 28, ["PT"] = 25, ["QA"] = 29, ["SA"] = 24, ["SE"] = 24, ["TR"] = 26,
    };

    /// <summary>Upper case without spaces (the form stored and indexed).</summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new string(value.Where(ch => !char.IsWhiteSpace(ch)).Select(char.ToUpperInvariant).ToArray());
    }

    /// <summary>The refusal of a normalized IBAN, or null when it is valid.</summary>
    public static string? Check(string iban)
    {
        ArgumentNullException.ThrowIfNull(iban);
        if (iban.Length is < 15 or > 34 || !iban.All(char.IsAsciiLetterOrDigit) || !char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1])
            || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]) || iban.Any(char.IsAsciiLetterLower))
        {
            return Invalid;
        }

        if (Lengths.TryGetValue(iban[..2], out var length) && iban.Length != length)
        {
            return Invalid;
        }

        var remainder = 0;
        foreach (var ch in iban[4..] + iban[..4])
        {
            var digits = char.IsAsciiDigit(ch) ? ch - '0' : ch - 'A' + 10;
            remainder = digits >= 10 ? ((remainder * 100) + digits) % 97 : ((remainder * 10) + digits) % 97;
        }

        return remainder == 1 ? null : Invalid;
    }
}

/// <summary>One payment account as the split rule sees it.</summary>
public sealed record SplitRow(Guid Id, DateOnly ValidFrom, DateOnly? ValidTo, string SplitKind, decimal? SplitValue, int Priority);

/// <summary>A refusal of the split rule: the key and the first date it fails on.</summary>
public sealed record SplitProblem(string ErrorKey, DateOnly Date);

/// <summary>
/// The split of net pay over an employment's accounts (FR-HRM-EMP-06, FR-HRM-PAY-24): on every date where it has accounts, exactly one
/// takes the remainder, the percentages add up to at most 100 and no two share a priority. The database checks the same at commit
/// (<c>hrm.tg_employee_bank_accounts_split</c> and the priority exclusion).
/// </summary>
public static class PaySplit
{
    public const string NoRemainder = "errors.hrm.bank_account.no_remainder";
    public const string TwoRemainders = "errors.hrm.bank_account.two_remainders";
    public const string PercentOver = "errors.hrm.bank_account.percent_over_100";
    public const string PriorityTaken = "errors.hrm.bank_account.priority_taken";

    public static SplitProblem? Check(IReadOnlyCollection<SplitRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var dates = rows.Select(r => r.ValidFrom).Concat(rows.Where(r => r.ValidTo is not null).Select(r => r.ValidTo!.Value)).Distinct().Order();
        foreach (var d in dates)
        {
            var valid = rows.Where(r => r.ValidFrom <= d && (r.ValidTo is null || r.ValidTo > d)).ToList();
            if (valid.Count == 0)
            {
                continue;
            }

            var remainders = valid.Count(r => r.SplitKind == "remainder");
            if (remainders != 1)
            {
                return new SplitProblem(remainders == 0 ? NoRemainder : TwoRemainders, d);
            }

            if (valid.Where(r => r.SplitKind == "percent").Sum(r => r.SplitValue ?? 0m) > 100m)
            {
                return new SplitProblem(PercentOver, d);
            }

            if (valid.GroupBy(r => r.Priority).Any(g => g.Count() > 1))
            {
                return new SplitProblem(PriorityTaken, d);
            }
        }

        return null;
    }

    /// <summary>A split value as text for the approvals inbox (invariant digits; no unit words).</summary>
    public static string Describe(string kind, decimal? value) =>
        value is { } v ? string.Create(CultureInfo.InvariantCulture, $"{kind} {v:0.####}") : kind;
}
