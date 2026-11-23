using System.Text.Json;
using Erp.Hrm.Application.People;

namespace Erp.Hrm.Application.Payroll;

/// <summary>
/// Create or change a pay group (slice S1.34a, FR-HRM-PAY-01). On create the company, code, name and frequency are required; the
/// currency defaults to the company's base currency (slice S1.34 Q2: one currency per group). <see cref="CutOffDay"/> is the
/// number of days before a period's last day on which its inputs close, <see cref="PaymentDay"/> the number of days after its last
/// day on which it is paid (ruling of slice S1.34a, CR-087). The property names are those of the generated pay group API, so the
/// engine's form posts to the service unchanged; an optional field sent as <c>null</c> is emptied (see <see cref="Parse"/>).
/// </summary>
public sealed class PayGroupRequest
{
    public Guid? CompanyId { get; init; }

    public string? Code { get; init; }

    /// <summary>Names by language code (<c>en</c>, <c>ur</c>); at least one is required on create.</summary>
    public IReadOnlyDictionary<string, string>? NameI18n { get; init; }

    /// <summary>monthly, semi_monthly, bi_weekly or weekly.</summary>
    public string? Frequency { get; init; }

    public string? CurrencyCode { get; init; }

    /// <summary>The month (1 to 12) in which the tax year starts; a statutory value the customer enters from the country pack (AS-4).</summary>
    public int? TaxYearStartMonth { get; init; }

    public int? CutOffDay { get; init; }

    public int? PaymentDay { get; init; }

    public Guid? CompanyBankAccountId { get; init; }

    /// <summary>Total above which a payroll run needs a second approval (used by S1.38). Salary-class data (GHR-06).</summary>
    public decimal? ApprovalThreshold { get; init; }

    public bool? IsActive { get; init; }

    /// <summary>Names of optional fields to empty: cutOffDay, paymentDay, companyBankAccountId, approvalThreshold.</summary>
    public IReadOnlyList<string>? Clear { get; init; }

    /// <summary>Required on update: the row version the caller read.</summary>
    public long? RowVersion { get; init; }

    /// <summary>The optional fields a request may empty.</summary>
    public static readonly string[] Clearable = ["cutOffDay", "paymentDay", "companyBankAccountId", "approvalThreshold"];

    /// <summary>
    /// Reads a request body. A property sent as <c>null</c> cannot be told from a missing one by the binder, so the optional fields
    /// named in <see cref="Clearable"/> that arrive as <c>null</c> are added to <see cref="Clear"/> (the engine's form sends
    /// <c>null</c> for a field the person emptied).
    /// </summary>
    public static PayGroupRequest Parse(JsonElement body)
    {
        var request = body.ValueKind == JsonValueKind.Object ? body.Deserialize<PayGroupRequest>(JsonSerializerOptions.Web) ?? new PayGroupRequest() : new PayGroupRequest();
        if (body.ValueKind != JsonValueKind.Object)
        {
            return request;
        }

        var nulled = body.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Null && Clearable.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .Select(p => Clearable.First(c => string.Equals(c, p.Name, StringComparison.OrdinalIgnoreCase)));
        return request.Clear is null && !nulled.Any() ? request : new PayGroupRequest
        {
            CompanyId = request.CompanyId, Code = request.Code, NameI18n = request.NameI18n, Frequency = request.Frequency, CurrencyCode = request.CurrencyCode,
            TaxYearStartMonth = request.TaxYearStartMonth, CutOffDay = request.CutOffDay, PaymentDay = request.PaymentDay, CompanyBankAccountId = request.CompanyBankAccountId,
            ApprovalThreshold = request.ApprovalThreshold, IsActive = request.IsActive, RowVersion = request.RowVersion,
            Clear = [.. (request.Clear ?? []).Concat(nulled).Distinct(StringComparer.Ordinal)],
        };
    }
}

/// <summary>
/// Generate the periods of one tax year for a pay group (FR-HRM-PAY-01, slice S1.34 Q6). <see cref="WeekStartsOn"/> names the
/// weekday a weekly or two-weekly period starts on (needed only for the group's first periods; later years continue from the last
/// period).
/// </summary>
public sealed class GeneratePeriodsRequest
{
    /// <summary>The calendar year in which the tax year starts.</summary>
    public int? TaxYear { get; init; }

    public string? WeekStartsOn { get; init; }
}

/// <summary>Change the cut-off or payment date of a period that has not opened (FR-HRM-PAY-38).</summary>
public sealed class ChangePeriodRequest
{
    public DateOnly? CutOffDate { get; init; }

    public DateOnly? PaymentDate { get; init; }

    public long? RowVersion { get; init; }
}

/// <summary>One generated period (pure rules, no database).</summary>
public sealed record GeneratedPeriod(int PeriodNo, DateOnly Start, DateOnly End, DateOnly CutOff, DateOnly Payment);

/// <summary>The pay group services (slice S1.34a Build item 1).</summary>
public interface IPayGroupService
{
    Task<PeopleResult> CreateAsync(PayGroupRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> UpdateAsync(Guid groupId, PayGroupRequest request, CancellationToken cancellationToken);
}

/// <summary>The pay period and payroll calendar services (slice S1.34a Build items 2 and 3).</summary>
public interface IPayCalendarService
{
    /// <summary>Generates the periods of a tax year (Q6). Refused when the group already has periods for that tax year.</summary>
    Task<PeopleResult> GenerateAsync(Guid groupId, GeneratePeriodsRequest request, CancellationToken cancellationToken);

    Task<PeopleResult> ChangeAsync(Guid periodId, ChangePeriodRequest request, CancellationToken cancellationToken);

    /// <summary>The periods of a tax year (all groups of the caller's companies, or one group) with their deadlines.</summary>
    Task<PeopleResult> CalendarAsync(Guid? groupId, int? taxYear, CancellationToken cancellationToken);

    /// <summary>The readiness checklist of a period: each registered item passed, failed or not available (FR-HRM-PAY-38).</summary>
    Task<PeopleResult> ReadinessAsync(Guid periodId, CancellationToken cancellationToken);
}

/// <summary>The result of one readiness item.</summary>
public enum ReadinessStatus
{
    /// <summary>The check ran and found nothing missing.</summary>
    Passed,

    /// <summary>The check ran and found something missing (see the details).</summary>
    Failed,

    /// <summary>The slice that owns the check does not exist yet: never shown as passed.</summary>
    NotAvailable,
}

/// <summary>Employees named by a failed check (an employment and its number).</summary>
public sealed record ReadinessEmployee(Guid EmploymentId, string EmployeeNo);

/// <summary>One item's outcome: the status, how many employees are affected and the first of them.</summary>
public sealed record ReadinessOutcome(ReadinessStatus Status, int Count = 0, IReadOnlyList<ReadinessEmployee>? Employees = null);

/// <summary>What a readiness check needs: the period and the group, and the employments of the group in it.</summary>
public sealed record ReadinessContext(Guid CompanyId, Guid PayGroupId, Guid PeriodId, DateOnly Start, DateOnly End);

/// <summary>
/// A readiness check (FR-HRM-PAY-38). Later slices register theirs (attendance locked: S1.31, loans: S1.41, claims: S1.42) by
/// adding a class implementing this interface whose <see cref="Code"/> is listed in a module's <c>pay-readiness.json</c>; an item
/// listed in the registry without a registered check shows as not available, never as passed.
/// </summary>
public interface IPayReadinessCheck
{
    string Code { get; }

    Task<ReadinessOutcome> CheckAsync(ReadinessContext context, CancellationToken cancellationToken);
}
