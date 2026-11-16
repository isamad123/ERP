namespace Erp.Kernel.Domain.Identity;

/// <summary>Checks a new password against the customer's policy (FR-ADM-USR-20). Returns localization keys, never text.</summary>
public static class PasswordRules
{
    public const string TooShort = "errors.password.too_short";
    public const string TooSimple = "errors.password.too_simple";
    public const string Reused = "errors.password.reused";
    public const string Empty = "errors.password.empty";

    /// <summary>The rule violations of <paramref name="password"/> (history is checked separately against stored hashes).</summary>
    public static IReadOnlyList<string> Check(string? password, PasswordPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrEmpty(password))
        {
            return [Empty];
        }

        var errors = new List<string>();
        if (new System.Globalization.StringInfo(password).LengthInTextElements < policy.MinLength)
        {
            errors.Add(TooShort);
        }

        if (CharacterKinds(password) < policy.MinCharacterKinds)
        {
            errors.Add(TooSimple);
        }

        return errors;
    }

    /// <summary>How many of the four kinds (upper case, lower case, digit, symbol) the password contains.</summary>
    public static int CharacterKinds(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        bool upper = false, lower = false, digit = false, symbol = false;
        foreach (var c in password)
        {
            if (char.IsUpper(c))
            {
                upper = true;
            }
            else if (char.IsLower(c))
            {
                lower = true;
            }
            else if (char.IsDigit(c))
            {
                digit = true;
            }
            else if (!char.IsWhiteSpace(c) || c == ' ')
            {
                symbol = true;
            }
        }

        return (upper ? 1 : 0) + (lower ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0);
    }

    /// <summary>True when a password set at <paramref name="changedAt"/> has expired under the policy (0 days = never).</summary>
    public static bool IsExpired(DateTimeOffset changedAt, DateTimeOffset now, PasswordPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.ExpiryDays > 0 && now >= changedAt.AddDays(policy.ExpiryDays);
    }
}
