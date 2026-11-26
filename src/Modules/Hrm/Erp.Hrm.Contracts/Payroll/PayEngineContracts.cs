using System.Text.Json;

namespace Erp.Hrm.Contracts.Payroll;

/// <summary>
/// What an input provider is asked for (slice S1.35 Build item 1, FR-HRM-PAY-07): the run, its pay period and the days it pays, and a batch of employments
/// (the engine asks per batch, so a provider answers many employees with a few queries; S1.35b chunks the same way).
/// </summary>
public sealed record PayInputRequest(Guid CompanyId, Guid PayRunId, Guid PayPeriodId, Guid PayGroupId, DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyCollection<Guid> EmploymentIds);

/// <summary>
/// One typed input of an employee for a run (FR-HRM-PAY-07). Either a <b>named quantity</b> (<see cref="Name"/> and <see cref="Value"/>, optionally
/// dated: worked days, unpaid leave days, overtime hours; formulas read them as <c>$name</c>, the engine sums the values per name) or a <b>component
/// amount</b> (<see cref="ComponentId"/> with <see cref="Amount"/>, or <see cref="Units"/> paid at the component's rate: a bonus, a one-time
/// adjustment, a loan recovery). <see cref="Provider"/>, <see cref="Source"/> and <see cref="SourceId"/> say where it came from; they are kept in
/// the snapshot (ids and amounts only) and handed back when the result is saved (<see cref="IPayInputProvider.ConsumedAsync"/>).
/// </summary>
public sealed record PayInputItem(
    Guid EmploymentId,
    string Provider,
    string Source,
    Guid? SourceId,
    string? Name = null,
    decimal? Value = null,
    DateOnly? Date = null,
    Guid? ComponentId = null,
    decimal? Amount = null,
    decimal? Units = null);

/// <summary>The inputs of one employee that a saved result used, with the result line each component amount went into.</summary>
public sealed record PayInputsConsumed(Guid PayRunId, Guid PayPeriodId, Guid EmploymentId, Guid ResultId, IReadOnlyList<ConsumedPayInput> Items);

/// <summary>One used input and the result line of its component (null for a named quantity).</summary>
public sealed record ConsumedPayInput(PayInputItem Item, Guid? ResultLineId);

/// <summary>
/// A source of payroll inputs (slice S1.35 Build item 1, FR-HRM-PAY-07; CR-093): attendance and leave from locked data, overtime, lateness deductions,
/// retroactive attendance adjustments and the one-time inputs of <c>hrm.pay_inputs</c> are this slice's; loans and advances (S1.41) and expense
/// claims (S1.42) register theirs the same way: <c>services.AddScoped&lt;IPayInputProvider, …&gt;()</c> in the registration both hosts call, and
/// <c>services.AddSingleton(new PayInputNameSet(code, names))</c> for the named quantities it gives (so formulas may name them; an unknown
/// name is refused when a formula is saved). A provider reads in the engine's open transaction on the scope's HR database context; it never
/// writes while answering, and it never needs a permission check (the engine has its own). The answer must depend only on stored data, so the
/// same data gives the same items (FR-HRM-PAY-31).
/// </summary>
public interface IPayInputProvider
{
    /// <summary>A short code (<c>attendance</c>, <c>overtime</c>, <c>loans</c>): the <see cref="PayInputItem.Provider"/> of its items.</summary>
    string Code { get; }

    /// <summary>The inputs of the employments asked, for the run's period. Items of employments not asked are ignored.</summary>
    Task<IReadOnlyList<PayInputItem>> ForAsync(PayInputRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Called in the transaction that saves an employee's result, with the items of this provider the result used: mark them used by the run
    /// (the one-time inputs become <c>consumed</c>; S1.41 stores the result line on the installment). Nothing to do for read-only sources.
    /// </summary>
    Task ConsumedAsync(PayInputsConsumed consumed, CancellationToken cancellationToken);
}

/// <summary>The named quantities a provider gives (without the dollar sign), registered as a singleton next to the provider.</summary>
public sealed record PayInputNameSet(string Provider, IReadOnlyList<string> Names);

/// <summary>
/// The named inputs of the engine and of this slice's providers (slice S1.35 Q1; R-282). Formulas write them with a dollar sign
/// (<c>$paid_days</c>). Days are calendar days unless the name says otherwise; hours are decimal hours.
/// </summary>
public static class PayInputNames
{
    /// <summary>Calendar days of the pay period.</summary>
    public const string PeriodDays = "period_days";

    /// <summary>Planned working days of the pay period (roster, weekly off and holidays; a day no plan names counts as working, R-301).</summary>
    public const string WorkingDays = "working_days";

    /// <summary>Calendar days of the period on which the employee is employed in the run's pay group.</summary>
    public const string EmployedDays = "employed_days";

    /// <summary>Employed calendar days less the days of unpaid leave.</summary>
    public const string PaidDays = "paid_days";

    /// <summary>Days with a present, late or on-duty result in the locked attendance (a half day counts one half).</summary>
    public const string WorkedDays = "worked_days";

    /// <summary>Days with an absent result in the locked attendance.</summary>
    public const string AbsentDays = "absent_days";

    /// <summary>Hours worked in the locked attendance.</summary>
    public const string WorkedHours = "worked_hours";

    /// <summary>Days of approved unpaid leave marked as a pay deduction (dated items; they reduce the pro-rata of the components).</summary>
    public const string UnpaidLeaveDays = "unpaid_leave_days";

    /// <summary>Hours of unpaid leave of types counted in hours.</summary>
    public const string UnpaidLeaveHours = "unpaid_leave_hours";

    /// <summary>Leave paid out in the period, in days.</summary>
    public const string LeaveEncashmentDays = "leave_encashment_days";

    /// <summary>Leave paid out in the period, in hours.</summary>
    public const string LeaveEncashmentHours = "leave_encashment_hours";

    /// <summary>Approved payable overtime hours, each weighted by its multiplier.</summary>
    public const string OvertimeHours = "overtime_hours";

    /// <summary>Approved payable overtime minutes, not weighted.</summary>
    public const string OvertimeMinutes = "overtime_minutes";

    /// <summary>Lateness and early-leaving penalties whose action is a pay deduction.</summary>
    public const string LateDeductions = "late_deductions";

    /// <summary>The minutes of those penalties.</summary>
    public const string LateDeductionMinutes = "late_deduction_minutes";

    /// <summary>Approved retroactive adjustments carried into this period: unpaid leave days given back (negative when more are taken).</summary>
    public const string AdjustmentPaidDays = "adjustment_paid_days";

    /// <summary>Approved retroactive adjustments: change of worked days.</summary>
    public const string AdjustmentWorkedDays = "adjustment_worked_days";

    /// <summary>Approved retroactive adjustments: change of payable overtime minutes (not weighted).</summary>
    public const string AdjustmentOvertimeMinutes = "adjustment_overtime_minutes";

    /// <summary>The names the engine itself works out from the snapshot.</summary>
    public static IReadOnlyList<string> Engine { get; } = [PeriodDays, WorkingDays, EmployedDays, PaidDays];

    /// <summary>Every name of the engine and of the providers of slice S1.35a.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } =
    [
        .. Engine, WorkedDays, AbsentDays, WorkedHours, UnpaidLeaveDays, UnpaidLeaveHours, LeaveEncashmentDays, LeaveEncashmentHours,
        OvertimeHours, OvertimeMinutes, LateDeductions, LateDeductionMinutes, AdjustmentPaidDays, AdjustmentWorkedDays, AdjustmentOvertimeMinutes,
    ];
}

/// <summary>
/// A statutory component as a calculator sees it (slice S1.35 Build item 6, ruling Q2): its id, code, kind, version and the free flag codes of the
/// pack (a component is statutory when its version's flags hold <c>"statutory": true</c>; the other flags, such as <c>income_tax</c>, say which
/// calculator it belongs to).
/// </summary>
public sealed record StatutoryComponent(Guid ComponentId, string Code, string Kind, Guid VersionId, IReadOnlyDictionary<string, bool> Flags, string? TaxTreatment);

/// <summary>A line already worked out for the employee (rounded to the currency's minor unit), with its component's flags.</summary>
public sealed record StatutoryLine(Guid ComponentId, string Code, string Kind, decimal Amount, IReadOnlyDictionary<string, bool> Flags);

/// <summary>What a calculator is asked to freeze for a batch of employees when the run's snapshot is taken.</summary>
public sealed record StatutoryFreezeRequest(Guid CompanyId, Guid PayRunId, Guid PayPeriodId, int TaxYear, int PeriodNo, DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyCollection<Guid> EmploymentIds);

/// <summary>
/// Everything a calculator may use for one component of one employee: the component, the calculator's own frozen data of the employee (null when
/// it froze none), the lines worked out before it in the order of evaluation, the named inputs, the period and the currency's minor unit. It must
/// compute from these only, so the same snapshot gives the same amount (FR-HRM-PAY-31).
/// </summary>
public sealed record StatutoryContext(
    StatutoryComponent Component,
    JsonElement? Frozen,
    IReadOnlyList<StatutoryLine> Lines,
    IReadOnlyDictionary<string, decimal> Inputs,
    Guid EmploymentId,
    int TaxYear,
    int PeriodNo,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string CurrencyCode,
    int CurrencyDecimals);

/// <summary>
/// A calculator's answer: the amount (positive; the component's kind says whether it is a deduction or an employer cost) or, when it cannot work it
/// out, a problem key (the employee is then incomplete, never silently zero); the steps for the trace; and facts for the result row
/// (<c>taxable_income_ytd</c>, <c>tax_ytd</c>).
/// </summary>
public sealed record StatutoryOutcome(decimal? Amount, string? ProblemKey, IReadOnlyList<string> Steps, IReadOnlyDictionary<string, decimal>? Facts = null);

/// <summary>
/// The statutory plug-in point (slice S1.35 Build item 6, ruling Q2; CR-093): income tax (S1.36) and contributions (S1.37) register one each
/// (<c>services.AddScoped&lt;IStatutoryCalculator, …&gt;()</c> in both hosts). The engine never calculates a statutory amount itself: for a component
/// marked statutory it asks the one calculator that <see cref="AppliesTo"/> it; with none (or more than one) the employee is incomplete and the run
/// cannot finish. No statutory rate, slab or threshold is ever in code (AS-4): calculators read the pack's tables, frozen in the snapshot.
/// </summary>
public interface IStatutoryCalculator
{
    /// <summary>A short code (<c>income_tax</c>): the key of its frozen data in the snapshot.</summary>
    string Code { get; }

    /// <summary>Whether this calculator works out the component (by its flags).</summary>
    bool AppliesTo(StatutoryComponent component);

    /// <summary>
    /// Reads what the calculation will need (table and rule versions and rows, year-to-date figures) for a batch of employees when the snapshot is
    /// taken; the answer per employment is stored in the snapshot (ids and amounts only) and handed back in <see cref="StatutoryContext.Frozen"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, JsonElement>> FreezeAsync(StatutoryFreezeRequest request, CancellationToken cancellationToken);

    /// <summary>Works out one component from the snapshot only.</summary>
    StatutoryOutcome Calculate(StatutoryContext context);
}

/// <summary>Events of the payroll engine in <c>hrm.outbox_messages</c> (slice S1.35a; ruling Q8: the journal event comes at finalize, S1.38 and S1.45).</summary>
public static class PayRunEvents
{
    /// <summary>
    /// Every employee of a run has a result (FR-HRM-PAY-34): ids, counts and the engine version, never an amount:
    /// {payRunId, runNo, companyId, payGroupId, payPeriodId, kind, employeeCount, engineVersion, calculatedBy, requestId}; published again each time a
    /// recalculation makes the run calculated (S1.35b; <c>requestId</c> names the calculation request).
    /// </summary>
    public const string Calculated = "hrm.pay_run.calculated";

    public const string AggregateType = "hrm.pay_run";
}

/// <summary>Permissions of the payroll engine (slice S1.35a, CR-093 ruling Q9; the trace permission is S1.35b's).</summary>
public static class PayRunPermissions
{
    /// <summary>Create a pay run and calculate it (the calculation locks the period's attendance and leave first, FR-XM-26).</summary>
    public const string Run = "hrm.pay.run";

    /// <summary>Read a run's results and lines (the amounts also need the salary field class, GHR-06).</summary>
    public const string ResultsRead = "hrm.pay.results.read";

    /// <summary>Add one-time inputs to a period (also through the import centre); amounts need edit access to the salary class.</summary>
    public const string InputsCreate = "hrm.pay_inputs.create";

    /// <summary>Cancel a one-time input that no run has used.</summary>
    public const string InputsCancel = "hrm.pay_inputs.cancel";

    public const string RunsRead = "hrm.pay_runs.read";

    /// <summary>Read the calculation trace of a result (FR-HRM-PAY-32; the amounts in it also need the salary field class, GHR-06).</summary>
    public const string TraceRead = "hrm.pay.trace.read";

    /// <summary>Every code declared in permissions.json for the engine (kept in step by a unit test).</summary>
    public static IReadOnlyList<string> HandWritten { get; } = [Run, ResultsRead, InputsCreate, InputsCancel, TraceRead];
}
