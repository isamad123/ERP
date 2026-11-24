using System.Text.Json;
using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Payroll;

/// <summary>
/// Create or change a pay component (slice S1.34b, FR-HRM-PAY-02, FR-HRM-CMP-03). The property names are those of the generated
/// API. The source of a component created here is always <c>tenant</c>: components of a country pack arrive through the pack installer (S1.23c).
/// </summary>
public sealed class PayComponentRequest
{
    public Guid? CompanyId { get; init; }

    public string? Code { get; init; }

    public IReadOnlyDictionary<string, string>? NameI18n { get; init; }

    /// <summary>earning, deduction, employer_contribution, reimbursement or informational.</summary>
    public string? Kind { get; init; }

    public int? PayslipOrder { get; init; }

    public bool? IsRecurring { get; init; }

    public bool? IsOneTimeAllowed { get; init; }

    public bool? IsActive { get; init; }

    /// <summary>The first version, so a component is complete when created (optional; versions can be added later).</summary>
    public PayComponentVersionRequest? Version { get; init; }

    public long? RowVersion { get; init; }

    public static PayComponentRequest Parse(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object ? body.Deserialize<PayComponentRequest>(JsonSerializerOptions.Web) ?? new PayComponentRequest() : new PayComponentRequest();
}

/// <summary>
/// A new effective-dated version of a component's calculation definition (FR-HRM-PAY-02, -03, -05): the method and what it needs,
/// the pro-rata rule with its parameters, the statutory-base flags (free codes the pack declares: <c>{"taxable": true}</c>) and the
/// tax-treatment label. The old version stays; it ends where the new one starts.
/// </summary>
public sealed class PayComponentVersionRequest
{
    public DateOnly? ValidFrom { get; init; }

    public string? Method { get; init; }

    /// <summary>The formula text of method <c>formula</c> (names are checked, never evaluated here).</summary>
    public string? Formula { get; init; }

    /// <summary>The components a <c>percent_of</c> version is a percentage of.</summary>
    public IReadOnlyList<Guid>? BaseComponentIds { get; init; }

    /// <summary>The statutory table a <c>table_lookup</c> version reads, by code. A code that does not exist yet makes the component incomplete (Q4).</summary>
    public string? StatutoryTableCode { get; init; }

    /// <summary>none, calendar_days, working_days, fixed_30 or custom.</summary>
    public string? ProRataRule { get; init; }

    /// <summary>Parameters of <c>custom</c>: <c>{"divisor": 26, "count": "working_days"}</c> (ruling of S1.34b).</summary>
    public JsonElement? ProRataParams { get; init; }

    /// <summary>Flag code to on or off. The codes are the pack's; no value is coded here (AS-4).</summary>
    public IReadOnlyDictionary<string, bool>? StatutoryFlags { get; init; }

    /// <summary>A code such as regular, irregular or exempt (the pack's labels).</summary>
    public string? TaxTreatment { get; init; }
}

/// <summary>A pay component, its versions and what is missing for it to be used by a run (the "incomplete" reasons, Q4).</summary>
public sealed record PayComponentView(object Component, IReadOnlyList<object> Versions, IReadOnlyList<string> Incomplete);

/// <summary>Create or change a salary structure and, optionally, set its component list from a date (FR-HRM-PAY-04).</summary>
public sealed class SalaryStructureRequest
{
    public Guid? CompanyId { get; init; }

    public string? Code { get; init; }

    public IReadOnlyDictionary<string, string>? NameI18n { get; init; }

    /// <summary>The default assignment rule: <c>{"by":"grade","gradeIds":[..]}</c>, <c>{"by":"employee_type","employeeTypes":[..]}</c> or <c>{"by":"individual"}</c>.</summary>
    public JsonElement? AssignRule { get; init; }

    public bool? IsActive { get; init; }

    /// <summary>The complete list of components from <see cref="Components"/>.ValidFrom on (components left out end that day).</summary>
    public StructureComponentSet? Components { get; init; }

    /// <summary>The requester's reason, shown to the approver.</summary>
    public string? Reason { get; init; }

    public long? RowVersion { get; init; }

    public static SalaryStructureRequest Parse(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object ? body.Deserialize<SalaryStructureRequest>(JsonSerializerOptions.Web) ?? new SalaryStructureRequest() : new SalaryStructureRequest();
}

/// <summary>The components of a structure from a date: each with its order of evaluation, defaults and whether it is mandatory.</summary>
public sealed class StructureComponentSet
{
    public DateOnly? ValidFrom { get; init; }

    public IReadOnlyList<StructureComponentInput>? Items { get; init; }
}

public sealed class StructureComponentInput
{
    public Guid? ComponentId { get; init; }

    public int? EvaluationOrder { get; init; }

    public decimal? DefaultAmount { get; init; }

    public decimal? DefaultPercent { get; init; }

    public bool? IsMandatory { get; init; }

    public string? CurrencyCode { get; init; }
}

/// <summary>
/// Assign a structure with amounts to an employee from a date, or revise it (FR-HRM-PAY-04, -06, CMP-03). A revision is a new
/// row from <see cref="ValidFrom"/>: nothing is edited. The reason code is of the employment event kind <c>salary_revision</c>.
/// </summary>
public sealed class CompensationRequest
{
    public Guid? StructureId { get; init; }

    public DateOnly? ValidFrom { get; init; }

    public string? ReasonCode { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<CompensationLineInput>? Lines { get; init; }

    /// <summary>The requester's reason for the second person (maker-checker).</summary>
    public string? Reason { get; init; }

    public static CompensationRequest Parse(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object ? body.Deserialize<CompensationRequest>(JsonSerializerOptions.Web) ?? new CompensationRequest() : new CompensationRequest();
}

public sealed class CompensationLineInput
{
    public Guid? ComponentId { get; init; }

    public decimal? Amount { get; init; }

    public decimal? Percent { get; init; }

    public decimal? Units { get; init; }
}

public interface IPayComponentService
{
    Task<PeopleResult> CreateAsync(PayComponentRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid componentId, PayComponentRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> AddVersionAsync(Guid componentId, PayComponentVersionRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> GetAsync(Guid componentId, DateOnly? asOf, CancellationToken cancellationToken);

    /// <summary>Checks a formula without saving: the names it uses and its problems.</summary>
    Task<PeopleResult> CheckFormulaAsync(Guid companyId, string? formula, CancellationToken cancellationToken);
}

public interface ISalaryStructureService
{
    Task<PeopleResult> CreateAsync(SalaryStructureRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid structureId, SalaryStructureRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> GetAsync(Guid structureId, DateOnly? asOf, CancellationToken cancellationToken);
}

public interface ICompensationService
{
    Task<PeopleResult> AssignAsync(Guid employmentId, CompensationRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> GetAsync(Guid employmentId, DateOnly? asOf, CancellationToken cancellationToken);
}
