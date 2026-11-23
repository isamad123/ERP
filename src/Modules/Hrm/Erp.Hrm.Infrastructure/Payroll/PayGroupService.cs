using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// Pay groups (slice S1.34a Build item 1; FR-HRM-PAY-01): frequency, currency (one per group, the company's by default), the month
/// the tax year starts, the cut-off and payment offsets, the paying company account and the approval threshold. The generated writes
/// of <c>hrm.pay_groups</c> are closed: this service checks the numbers (the database repeats them), keeps the frequency and tax-year
/// start fixed once periods exist and refuses to switch off a group that still has employees. The approval threshold is salary-class
/// data: a caller without edit access to it cannot set it and does not see it (GHR-06). An employee's group is never edited here: it
/// changes through an employment event (S1.20b assignments), which refuses a move while the old group has an unfinished run.
/// No statutory value is coded: the tax-year start month is the customer's entry from the country pack (AS-4).
/// </summary>
internal sealed partial class PayGroupService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    EmploymentApplier applier) : IPayGroupService
{
    public const string CodeRequired = "errors.hrm.pay.code_required";
    public const string CodeTaken = "errors.hrm.pay.code_taken";
    public const string NameRequired = "errors.hrm.pay.name_required";
    public const string FrequencyInvalid = "errors.hrm.pay.frequency";
    public const string FrequencyLocked = "errors.hrm.pay.frequency_locked";
    public const string CurrencyUnknown = "errors.hrm.pay.currency_unknown";
    public const string StartMonth = "errors.hrm.pay.start_month";
    public const string OffsetRange = "errors.hrm.pay.offset_range";
    public const string ThresholdNegative = "errors.hrm.pay.threshold_negative";
    public const string BankAccountUnknown = "errors.hrm.pay.bank_account_unknown";
    public const string InUse = "errors.hrm.pay.group_in_use";

    private const int CodeLength = 40;
    private const int NameLength = 200;
    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];
    private static readonly string[] Clearable = PayGroupRequest.Clearable;

    private DbContextScopeSource Source => new(db);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex CodePattern();

    public async Task<PeopleResult> CreateAsync(PayGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.GroupsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CompanyId is not { } company)
        {
            errors["companyId"] = [PersonService.Required];
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        var row = new PayGroup
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = company,
            NameI18n = JsonDocument.Parse("{}"),
            IsActive = request.IsActive ?? true,
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, PayGroupAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var set = new List<string>();
        Apply(row, request, creating: true, errors, set);
        if (request.CurrencyCode is null && errors.Count == 0)
        {
            // One currency per group, the company's by default (slice S1.34 Q2).
            row.CurrencyCode = (await db.Database.SqlQuery<string>($"select base_currency_code::text as \"Value\" from org.companies where id = {company}").ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                ?? string.Empty;
            if (row.CurrencyCode.Length == 0)
            {
                errors["companyId"] = ["errors.data.not_found"];
            }
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var map = await fields.ForAsync(PayGroupAccess.Fields, "create", company, cancellationToken).ConfigureAwait(false);
        if (set.Where(k => PayGroupAccess.Fields.Find(k) is not null && !map.CanEdit(k)).ToList() is { Count: > 0 } denied)
        {
            return NotEditable(denied);
        }

        db.Add(row);
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid groupId, PayGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.GroupsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<PayGroup>().FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, PayGroupAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, PayGroupAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CompanyId is { } other && other != row.CompanyId)
        {
            errors["companyId"] = [PersonService.CannotClear];
        }

        var (frequency, startMonth, wasActive) = (row.Frequency, row.TaxYearStartMonth, row.IsActive);
        var set = new List<string>();
        var before = Snapshot(row);
        Apply(row, request, creating: false, errors, set);
        set = [.. set.Where(k => Snapshot(row).GetValueOrDefault(k) != before.GetValueOrDefault(k))]; // only what really changed needs edit access (a form sends every field)
        if ((row.Frequency != frequency || row.TaxYearStartMonth != startMonth)
            && await db.Set<PayPeriod>().AsNoTracking().AnyAsync(p => p.PayGroupId == row.Id, cancellationToken).ConfigureAwait(false))
        {
            errors[row.Frequency != frequency ? "frequency" : "taxYearStartMonth"] = [FrequencyLocked];
        }

        if (wasActive && !row.IsActive && await InUseAsync(row, cancellationToken).ConfigureAwait(false))
        {
            errors["isActive"] = [InUse];
        }

        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        var plan = await fields.PlanAsync(PayGroupAccess.Fields, PayGroupAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false);
        var access = (await plan.ResolveAsync([row], Source, cancellationToken).ConfigureAwait(false))[0];
        if (set.Distinct().Where(k => PayGroupAccess.Fields.Find(k) is not null && !access.CanEdit(k)).ToList() is { Count: > 0 } denied)
        {
            db.ChangeTracker.Clear();
            return NotEditable(denied);
        }

        EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string?> Snapshot(PayGroup r) => new(StringComparer.Ordinal)
    {
        ["code"] = r.Code,
        ["name_i18n"] = r.NameI18n.RootElement.GetRawText(),
        ["frequency"] = r.Frequency,
        ["currency_code"] = r.CurrencyCode,
        ["tax_year_start_month"] = r.TaxYearStartMonth.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["cut_off_day"] = r.CutOffDay?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["payment_day"] = r.PaymentDay?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["company_bank_account_id"] = r.CompanyBankAccountId?.ToString(),
        ["approval_threshold"] = r.ApprovalThreshold?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["is_active"] = r.IsActive ? "1" : "0",
    };

    /// <summary>True when a current employee's assignment today is in the group (a group with people cannot be switched off).</summary>
    private async Task<bool> InUseAsync(PayGroup row, CancellationToken ct)
    {
        var today = await applier.TodayAsync(row.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
        return await (from a in db.Set<EmploymentAssignment>().AsNoTracking()
                      join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
                      where a.PayGroupId == row.Id && (a.ValidTo == null || a.ValidTo > today) && CurrentStatuses.Contains(e.Status)
                      select a.Id).AnyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Copies the request into the row, collecting field errors and the field keys that were set.</summary>
    private static void Apply(PayGroup row, PayGroupRequest r, bool creating, Dictionary<string, string[]> errors, List<string> set)
    {
        void Fail(string field, string key) => errors[field] = [.. errors.GetValueOrDefault(field, []).Append(key).Distinct(StringComparer.Ordinal)];
        var clear = (r.Clear ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var name in clear.Where(c => !Clearable.Contains(c, StringComparer.Ordinal)))
        {
            Fail("clear", PersonService.CannotClear);
        }

        if (creating || r.Code is not null)
        {
            var code = r.Code?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                Fail("code", CodeRequired);
            }
            else if (code.Length > CodeLength)
            {
                Fail("code", PersonService.TooLong);
            }
            else if (!CodePattern().IsMatch(code))
            {
                Fail("code", CodeRequired);
            }
            else
            {
                row.Code = code;
                set.Add("code");
            }
        }

        if (creating || r.NameI18n is not null)
        {
            var names = (r.NameI18n ?? new Dictionary<string, string>()).Select(n => (Key: n.Key.Trim().ToLowerInvariant(), Value: n.Value?.Trim() ?? string.Empty)).Where(n => n.Value.Length > 0).ToList();
            if (names.Count == 0 || names.Any(n => n.Key.Length is < 2 or > 5))
            {
                Fail("nameI18n", NameRequired);
            }
            else if (names.Any(n => n.Value.Length > NameLength))
            {
                Fail("nameI18n", PersonService.TooLong);
            }
            else
            {
                row.NameI18n = JsonSerializer.SerializeToDocument(names.DistinctBy(n => n.Key).ToDictionary(n => n.Key, n => n.Value));
                set.Add("name_i18n");
            }
        }

        if (creating || r.Frequency is not null)
        {
            if (r.Frequency is null || !PayPeriodRules.Frequencies.Contains(r.Frequency, StringComparer.Ordinal))
            {
                Fail("frequency", FrequencyInvalid);
            }
            else
            {
                row.Frequency = r.Frequency;
                set.Add("frequency");
            }
        }

        if (r.CurrencyCode is not null)
        {
            var currency = r.CurrencyCode.Trim().ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
            {
                Fail("currencyCode", CurrencyUnknown);
            }
            else
            {
                row.CurrencyCode = currency;
                set.Add("currency_code");
            }
        }

        if (creating || r.TaxYearStartMonth is not null)
        {
            if (r.TaxYearStartMonth is not { } month)
            {
                Fail("taxYearStartMonth", PersonService.Required);
            }
            else if (month is < 1 or > 12)
            {
                Fail("taxYearStartMonth", StartMonth);
            }
            else
            {
                row.TaxYearStartMonth = month;
                set.Add("tax_year_start_month");
            }
        }

        Offset(r.CutOffDay, clear.Contains("cutOffDay"), "cutOffDay", "cut_off_day", v => row.CutOffDay = v);
        Offset(r.PaymentDay, clear.Contains("paymentDay"), "paymentDay", "payment_day", v => row.PaymentDay = v);
        if (r.CompanyBankAccountId is { } account)
        {
            row.CompanyBankAccountId = account;
            set.Add("company_bank_account_id");
        }
        else if (clear.Contains("companyBankAccountId"))
        {
            row.CompanyBankAccountId = null;
            set.Add("company_bank_account_id");
        }

        if (r.ApprovalThreshold is { } threshold)
        {
            if (threshold < 0)
            {
                Fail("approvalThreshold", ThresholdNegative);
            }
            else
            {
                row.ApprovalThreshold = decimal.Round(threshold, 4, MidpointRounding.ToEven);
                set.Add("approval_threshold");
            }
        }
        else if (clear.Contains("approvalThreshold"))
        {
            row.ApprovalThreshold = null;
            set.Add("approval_threshold");
        }

        if (r.IsActive is { } active)
        {
            row.IsActive = active;
            set.Add("is_active");
        }

        void Offset(int? value, bool clearIt, string field, string key, Action<int?> assign)
        {
            if (value is { } days)
            {
                if (days is < 0 or > 31)
                {
                    Fail(field, OffsetRange);
                }
                else
                {
                    assign(days);
                    set.Add(key);
                }
            }
            else if (clearIt)
            {
                assign(null);
                set.Add(key);
            }
        }
    }

    private async Task<PeopleResult> SaveAsync(PayGroup row, CancellationToken ct)
    {
        var created = db.Entry(row).State == EntityState.Added;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }

        return new PeopleResult(created ? PeopleOutcome.Created : PeopleOutcome.Done, Body: await ViewAsync(row, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The saved group as the generated read shows it (the engine keeps this row as the form's starting point): every field under the
    /// caller's field rules, so the approval threshold is left out for a caller without access to salary-class data (GHR-06).
    /// </summary>
    private async Task<Dictionary<string, object?>> ViewAsync(PayGroup r, CancellationToken ct)
    {
        var plan = await fields.PlanAsync(PayGroupAccess.Fields, PayGroupAccess.Scope, "read", Source, ct).ConfigureAwait(false);
        var access = (await plan.ResolveAsync([r], Source, ct).ConfigureAwait(false))[0];
        var view = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = r.Id,
            ["companyId"] = r.CompanyId,
            ["code"] = r.Code,
            ["nameI18n"] = JsonSerializer.Deserialize<JsonElement>(r.NameI18n.RootElement.GetRawText()),
            ["frequency"] = r.Frequency,
            ["currencyCode"] = r.CurrencyCode,
            ["taxYearStartMonth"] = r.TaxYearStartMonth,
            ["cutOffDay"] = r.CutOffDay,
            ["paymentDay"] = r.PaymentDay,
            ["companyBankAccountId"] = r.CompanyBankAccountId,
            ["approvalThreshold"] = r.ApprovalThreshold,
            ["isActive"] = r.IsActive,
            ["isDeleted"] = r.IsDeleted,
            ["createdAt"] = r.CreatedAt,
            ["createdBy"] = r.CreatedBy,
            ["updatedAt"] = r.UpdatedAt,
            ["updatedBy"] = r.UpdatedBy,
            ["rowVersion"] = r.RowVersion,
        };
        foreach (var field in PayGroupAccess.Fields.Fields.Where(f => view.ContainsKey(f.JsonName)))
        {
            var level = access.Of(field.Key);
            if (level == FieldAccessLevel.Hidden)
            {
                view.Remove(field.JsonName);
            }
            else if (level == FieldAccessLevel.Masked && view[field.JsonName] is { } value)
            {
                view[field.JsonName] = FieldShaping.Mask(JsonSerializer.SerializeToNode(value))?.GetValue<string>();
            }
        }

        return view;
    }

    internal static PeopleResult Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "uq_pay_groups_company_id_code" => Field("code", CodeTaken),
        "fk_pay_groups_company_id" => Field("companyId", "errors.data.not_found"),
        "fk_pay_groups_currency_code" => Field("currencyCode", CurrencyUnknown),
        "fk_pay_groups_company_bank_account_id" => Field("companyBankAccountId", BankAccountUnknown),
        "ck_pay_groups_rules" => Field("frequency", FrequencyLocked),
        _ => PersonService.Refusal(pg),
    };

    private static PeopleResult Field(string field, string key) => PeopleResult.Fields(new Dictionary<string, string[]> { [field] = [key] });

    private static PeopleResult NotEditable(List<string> denied) =>
        new(PeopleOutcome.Invalid, FieldShaping.NotEditable, FieldErrors: denied.Distinct(StringComparer.Ordinal).ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal));
}
