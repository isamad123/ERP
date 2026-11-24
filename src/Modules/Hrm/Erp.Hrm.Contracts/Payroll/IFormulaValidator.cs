namespace Erp.Hrm.Contracts.Payroll;

/// <summary>
/// One problem found in a formula: a localization key and the name or text it is about (slice S1.34b, FR-HRM-PAY-03).
/// </summary>
public sealed record FormulaProblem(string ErrorKey, string? Name = null);

/// <summary>
/// What the formula validator found: the component codes and named inputs the formula uses, and its problems (empty when valid).
/// </summary>
public sealed record FormulaCheck(IReadOnlySet<string> Components, IReadOnlySet<string> Inputs, IReadOnlyList<FormulaProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Stores-and-name-checks only: the contract through which the pay set-up checks a component formula (slice S1.34 Q3, S1.34b). A
/// formula is a small expression over component codes and named inputs (<c>$name</c>), the operators + - * / ( ) and the functions
/// min, max and round. This slice checks that the characters belong to the grammar, the brackets balance, every component code
/// exists and no component leads back to itself; it never evaluates. S1.35 completes the grammar and evaluates through the same
/// contract. No statutory value is ever read or written here (AS-4).
/// </summary>
public interface IFormulaValidator
{
    /// <summary>Checks one formula against the codes of the components that exist in the company.</summary>
    FormulaCheck Check(string? formula, IReadOnlySet<string> knownComponentCodes);

    /// <summary>
    /// Finds a cycle in "component uses components" references (code to the codes it uses). Returns the codes along the first cycle
    /// found, the first code repeated at the end, or null when there is none.
    /// </summary>
    IReadOnlyList<string>? FindCycle(IReadOnlyDictionary<string, IReadOnlySet<string>> uses);
}
