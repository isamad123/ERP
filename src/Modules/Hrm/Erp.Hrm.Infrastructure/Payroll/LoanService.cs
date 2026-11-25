using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.Leave;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// Applying for a loan or advance, seeing its schedule, cancelling, recording its payout and holding a repayment received outside payroll (slice S1.41 Build items 2, 3 and 5;
/// FR-HRM-LON-02, LON-03, LON-07, GHR-06, FR-XM-32). The employee applies for their own loan (the employment is the token user's); HR applies for an employee in their data scope
/// and is recorded as the one who applied. The application is checked against the loan type's policy (service, grade, loans open, the most that may be lent, the longest repayment,
/// the guarantor), shown with its schedule first, numbered by the kernel numbering service when saved (gap-free, in its transaction) and sent for approval on the route of
/// <c>hrm.loan_application</c>: the line manager (by the employee's own manager, R-305), the guarantor when the type requires one, then finance. The kernel never lets the requester or
/// the employee approve beyond their own step. Nobody pays out or books a repayment on their own loan. Amounts are salary-class data: left out of every answer without the field class.
/// </summary>
internal sealed class LoanService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ISettings settings,
    INumberAllocator numbers,
    IWorkflowService workflow,
    IHeldChanges held,
    IFieldEncryptorProvider keys,
    LoanFacts facts,
    EmploymentApplier applier) : ILoanService
{
    public const string NumberType = "hrm.loan_no";
    public const string NotEligible = "errors.hrm.loan.not_eligible";
    public const string TypeRequired = "errors.hrm.loan.type_required";
    public const string TypeUnknown = "errors.hrm.loan.type_unknown";
    public const string TypeOff = "errors.hrm.loan.type_off";
    public const string NoPolicy = "errors.hrm.loan.no_policy";
    public const string NoPayGroup = "errors.hrm.loan.no_pay_group";
    public const string CalendarShort = "errors.hrm.loan.calendar_short";
    public const string AmountRequired = "errors.hrm.loan.amount_required";
    public const string AmountDecimals = "errors.hrm.loan.amount_decimals";
    public const string InstallmentsRequired = "errors.hrm.loan.installments_required";
    public const string NotCurrent = "errors.hrm.employment.not_current";
    public const string NoEmployment = "errors.hrm.loan.no_employment";
    public const string RecoveryAbove = "errors.hrm.loan.recovery_above_share";
    public const string GuarantorUnknown = "errors.hrm.loan.guarantor_unknown";
    public const string NotApproved = "errors.hrm.loan.not_approved";
    public const string NotActive = "errors.hrm.loan.not_active";
    public const string OwnLoan = "errors.hrm.loan.own_loan";
    public const string DateRequired = "errors.hrm.loan.date_required";
    public const string DateFuture = "errors.hrm.loan.date_future";
    public const string ReferenceRequired = "errors.hrm.loan.reference_required";
    public const string ReceiptAmount = "errors.hrm.loan.receipt_amount";
    public const string ReceiptTooMuch = "errors.hrm.loan.receipt_too_much";
    public const string ReceiptRunOpen = "errors.hrm.loan.receipt_run_open";
    public const string ReceiptWaiting = "errors.hrm.loan.receipt_waiting";
    public const string ReceiptNothing = "errors.hrm.loan.receipt_nothing";
    public const string NotCancellable = "errors.hrm.loan.not_cancellable";
    public const string LedgerMismatch = "errors.hrm.loan.ledger_mismatch";
    public const string ReasonLong = "errors.hrm.loan.reason_long";

    private const int ReasonLength = 500;
    private const int ReferenceLength = 100;
    private static readonly string[] Current = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    // ------------------------------------------------------------------ preview and apply

    public async Task<PeopleResult> PreviewAsync(LoanApplicationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LoanPermissions.Apply, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (plan, refusal) = await PlanAsync(request, cancellationToken).ConfigureAwait(false);
        return refusal ?? new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(plan!, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PeopleResult> ApplyAsync(LoanApplicationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LoanPermissions.Apply, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var reason = request.Reason?.Trim();
        if (reason is { Length: > ReasonLength })
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["reason"] = [ReasonLong] });
        }

        try
        {
            PeopleResult? result = null;
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var (found, refusal) = await PlanAsync(request, cancellationToken).ConfigureAwait(false);
                if (refusal is not null)
                {
                    result = refusal;
                    return;
                }

                var plan = found!;
                // One application at a time per employee, so "loans already open" counts the same loans the checks saw.
                await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.loan_apply:" + plan.Context.Employment.Id.ToString("D")}, 0))", cancellationToken).ConfigureAwait(false);
                var (again, second) = await PlanAsync(request, cancellationToken).ConfigureAwait(false);
                if (second is not null)
                {
                    result = second;
                    return;
                }

                plan = again!;
                if (plan.Problems.Count > 0)
                {
                    result = PeopleResult.Conflict(NotEligible, new Dictionary<string, object?> { ["problems"] = plan.Problems });
                    return;
                }

                if (!plan.Periods.Enough)
                {
                    result = PeopleResult.Conflict(CalendarShort);
                    return;
                }

                var e = plan.Context.Employment;
                var now = clock.UtcNow;
                var today = await applier.TodayAsync(e.CompanyId, now, cancellationToken).ConfigureAwait(false);
                var branch = await db.Set<EmploymentAssignment>().AsNoTracking()
                    .Where(a => a.EmploymentId == e.Id && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo > today))
                    .OrderByDescending(a => a.ValidFrom).Select(a => (Guid?)a.BranchId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                var loan = new Loan
                {
                    Id = Guid.CreateVersion7(now), CompanyId = e.CompanyId, EmploymentId = e.Id, LoanTypeId = plan.Type.Id, Principal = plan.Principal,
                    CurrencyCode = plan.Context.CurrencyCode!, InterestRate = plan.Rules.InterestMethod == LoanMethods.None ? 0m : plan.Rules.AnnualInterestPercent,
                    InterestMethod = plan.Rules.InterestMethod, Installments = plan.Installments, StartPeriodId = plan.Periods.Periods[0].Id,
                    GuarantorEmploymentId = plan.Guarantor?.Id, Reason = reason, LoanNo = string.Empty, Status = "applied",
                    CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId, RowVersion = 1,
                };
                if (!await scope.AllowsAsync(loan, LoanAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
                {
                    result = PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
                    return;
                }

                loan.LoanNo = await numbers.AssignAsync(new NumberRequest(NumberType, loan.CompanyId, branch, today), tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                db.Add(loan);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var started = await workflow.StartAsync(new WorkflowStartRequest(LoanEvents.ApplicationDocumentType, loan.Id, loan.CompanyId, e.UserRef is { } subject ? [subject] : [], await ValuesAsync(plan, loan, today, cancellationToken).ConfigureAwait(false)),
                    tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                loan.WorkflowInstanceId = started.InstanceId;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new PeopleResult(PeopleOutcome.Pending, Body: new { id = loan.Id, loanNo = loan.LoanNo, status = "applied", instanceId = started.InstanceId, employmentId = e.Id, onBehalf = e.UserRef != actor.UserId });
            }).ConfigureAwait(false);
            return result!;
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(ex.ErrorKey, ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return PersonService.Refusal(pg);
        }
    }

    private async Task<Dictionary<string, JsonElement>> ValuesAsync(LoanPlan plan, Loan loan, DateOnly today, CancellationToken ct)
    {
        var e = plan.Context.Employment;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["company"] = Json(e.CompanyId.ToString()),
            ["employee_no"] = Json(e.EmployeeNo),
            ["loan_no"] = Json(loan.LoanNo),
            ["loan_type"] = Json(plan.Type.Code),
            ["principal"] = Json(loan.Principal),
            ["installments"] = Json(loan.Installments),
            ["guarantor_required"] = Json(plan.Rules.RequiresGuarantor),
            ["on_behalf"] = Json(e.UserRef != actor.UserId),
        };
        if (e.UserRef is { } user)
        {
            values["employee"] = Json(user.ToString());
        }

        if (await LeaveWorkflowValues.LineManagerAsync(db, e.Id, today, ct).ConfigureAwait(false) is { } manager)
        {
            values["line_manager"] = Json(manager.ToString());
        }

        if (plan.Guarantor?.UserRef is { } guarantor)
        {
            values["guarantor"] = Json(guarantor.ToString());
        }

        return values;
    }

    /// <summary>The checks and the schedule of an application. A field error or a refusal ends it; an ineligible applicant is not an error here: the reasons are in the plan.</summary>
    private async Task<(LoanPlan? Plan, PeopleResult? Refusal)> PlanAsync(LoanApplicationRequest r, CancellationToken ct)
    {
        var (employment, failure) = await EmploymentAsync(r.EmploymentId, ct).ConfigureAwait(false);
        if (employment is null)
        {
            return (null, failure);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (r.LoanTypeId is not { } typeId)
        {
            errors["loanTypeId"] = [TypeRequired];
        }

        if (r.Principal is not { } principal || principal <= 0m)
        {
            errors["principal"] = [AmountRequired];
        }

        if (r.Installments is not { } installments || installments < 1)
        {
            errors["installments"] = [InstallmentsRequired];
        }

        if (errors.Count > 0)
        {
            return (null, PeopleResult.Fields(errors));
        }

        var type = await db.Set<LoanType>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == r.LoanTypeId && t.CompanyId == employment.CompanyId && !t.IsDeleted, ct).ConfigureAwait(false);
        if (type is null)
        {
            return (null, PeopleResult.Fields(new Dictionary<string, string[]> { ["loanTypeId"] = [TypeUnknown] }));
        }

        if (!type.IsActive)
        {
            return (null, PeopleResult.Fields(new Dictionary<string, string[]> { ["loanTypeId"] = [TypeOff] }));
        }

        var now = clock.UtcNow;
        var today = await applier.TodayAsync(employment.CompanyId, now, ct).ConfigureAwait(false);
        var rules = await facts.RulesAsync(type, today, ct).ConfigureAwait(false);
        if (rules is null)
        {
            return (null, PeopleResult.Conflict(NoPolicy));
        }

        var context = await facts.ReadAsync(employment, today, ct).ConfigureAwait(false);
        if (context.PayGroupId is not { } payGroup)
        {
            return (null, PeopleResult.Conflict(NoPayGroup));
        }

        var amount = r.Principal!.Value;
        if (decimal.Round(amount, context.Decimals, MidpointRounding.AwayFromZero) != amount)
        {
            return (null, PeopleResult.Fields(new Dictionary<string, string[]> { ["principal"] = [AmountDecimals] }));
        }

        var problems = new List<LoanProblem>(LoanEligibility.Check(rules, context.ServiceMonths, context.GradeCode, context.OpenLoans, amount, r.Installments!.Value, context.GrossPay));
        Employment? guarantor = null;
        if (r.GuarantorEmploymentId is { } gid)
        {
            guarantor = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == gid && e.CompanyId == employment.CompanyId && Current.Contains(e.Status), ct).ConfigureAwait(false);
            if (guarantor is null)
            {
                return (null, PeopleResult.Fields(new Dictionary<string, string[]> { ["guarantorEmploymentId"] = [GuarantorUnknown] }));
            }

            if (guarantor.Id == employment.Id || guarantor.PersonId == employment.PersonId)
            {
                problems.Add(new LoanProblem(LoanEligibility.GuarantorSelf));
            }
            else if (guarantor.UserRef is null)
            {
                problems.Add(new LoanProblem(LoanEligibility.GuarantorNoSignIn));
            }
        }
        else if (rules.RequiresGuarantor)
        {
            problems.Add(new LoanProblem(LoanEligibility.GuarantorRequired));
        }

        var periods = await facts.PeriodsAsync(payGroup, r.StartPeriodId, today, r.Installments.Value, ct).ConfigureAwait(false);
        if (!periods.Enough)
        {
            problems.Add(new LoanProblem(CalendarShort, periods.Periods.Count.ToString(CultureInfo.InvariantCulture)));
        }

        IReadOnlyList<LoanInstallmentPlan> schedule = [];
        if (amount > 0m)
        {
            schedule = LoanSchedule.Build(amount, rules.InterestMethod, rules.AnnualInterestPercent, r.Installments.Value, LoanSchedule.PeriodsPerYear(context.Frequency!), context.Decimals, context.Mode);
        }

        var warnings = await WarningsAsync(context, periods, schedule, ct).ConfigureAwait(false);
        return (new LoanPlan(context, type, rules, amount, r.Installments.Value, schedule, periods, guarantor, problems, warnings, today), null);
    }

    /// <summary>The warning of the customer's share setting (R-181): installments above it, with the pay of the compensation in force as the base. A warning only.</summary>
    private async Task<List<LoanProblem>> WarningsAsync(LoanContext context, LoanPeriods periods, IReadOnlyList<LoanInstallmentPlan> schedule, CancellationToken ct)
    {
        var warnings = new List<LoanProblem>();
        var setting = (await settings.GetAsync(HrmSettingKeys.HrmLoanMaxRecoveryPercent, new SettingContext(context.Employment.CompanyId, null), ct).ConfigureAwait(false)).Value;
        var percent = setting.ValueKind == JsonValueKind.Number ? setting.GetDecimal() : 0m;
        if (percent <= 0m || context.GrossPay is not { } pay || pay <= 0m || schedule.Count == 0)
        {
            return warnings;
        }

        var ids = periods.Periods.Select(p => p.Id).ToList();
        var existing = await (from i in db.Set<LoanInstallment>().AsNoTracking()
                              join l in db.Set<Loan>().AsNoTracking() on i.LoanId equals l.Id
                              where l.EmploymentId == context.Employment.Id && l.Status != "cancelled" && l.Status != "rejected" && (i.Status == "scheduled" || i.Status == "skipped") && ids.Contains(i.PayPeriodId)
                              select new { i.PayPeriodId, Total = i.PrincipalAmount + i.InterestAmount }).ToListAsync(ct).ConfigureAwait(false);
        var worst = 0m;
        for (var i = 0; i < schedule.Count && i < periods.Periods.Count; i++)
        {
            var due = schedule[i].Total + existing.Where(x => x.PayPeriodId == periods.Periods[i].Id).Sum(x => x.Total);
            worst = Math.Max(worst, due / pay * 100m);
        }

        if (worst > percent)
        {
            warnings.Add(new LoanProblem(RecoveryAbove, decimal.Round(worst, 2, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)));
        }

        return warnings;
    }

    private async Task<Dictionary<string, object?>> ViewAsync(LoanPlan p, CancellationToken ct)
    {
        // The amounts are salary-class data (GHR-06): shown only to a caller who may read the field class on loans of this company.
        var map = await fields.ForAsync(LoanAccess.Fields, "read", p.Context.Employment.CompanyId, ct).ConfigureAwait(false);
        var canSee = map.Of("principal") >= FieldAccessLevel.ReadOnly;
        var view = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["employmentId"] = p.Context.Employment.Id,
            ["employeeNo"] = p.Context.Employment.EmployeeNo,
            ["loanType"] = new { id = p.Type.Id, code = p.Type.Code, kind = p.Type.Kind },
            ["currencyCode"] = p.Context.CurrencyCode,
            ["interestMethod"] = p.Rules.InterestMethod,
            ["annualInterestPercent"] = p.Rules.InterestMethod == LoanMethods.None ? 0m : p.Rules.AnnualInterestPercent,
            ["installments"] = p.Installments,
            ["maxInstallments"] = p.Rules.MaxInstallments,
            ["requiresGuarantor"] = p.Rules.RequiresGuarantor,
            ["eligible"] = p.Problems.Count == 0,
            ["problems"] = p.Problems,
            ["warnings"] = p.Warnings,
            ["startPeriodId"] = p.Periods.Periods.Count > 0 ? p.Periods.Periods[0].Id : (Guid?)null,
        };
        if (canSee)
        {
            view["principal"] = p.Principal;
            view["maxAmount"] = LoanEligibility.MaxAmount(p.Rules, p.Context.GrossPay);
            view["totalInterest"] = p.Schedule.Sum(s => s.Interest);
            view["totalRepayable"] = p.Schedule.Sum(s => s.Total);
            view["schedule"] = p.Schedule.Select((s, i) => new
            {
                seq = s.Seq,
                payPeriodId = i < p.Periods.Periods.Count ? p.Periods.Periods[i].Id : (Guid?)null,
                periodStart = i < p.Periods.Periods.Count ? p.Periods.Periods[i].StartDate : (DateOnly?)null,
                periodEnd = i < p.Periods.Periods.Count ? p.Periods.Periods[i].EndDate : (DateOnly?)null,
                principalAmount = s.Principal,
                interestAmount = s.Interest,
                total = s.Total,
            }).ToList();
        }

        return view;
    }

    /// <summary>The employment: the one named, else the caller's own (exactly one current), inside the caller's data scope to create loans.</summary>
    private async Task<(Employment? Employment, PeopleResult? Failure)> EmploymentAsync(Guid? employmentId, CancellationToken ct)
    {
        Employment? employment;
        if (employmentId is { } id && id != Guid.Empty)
        {
            employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        }
        else
        {
            var own = await db.Set<Employment>().AsNoTracking().Where(e => e.UserRef == actor.UserId && Current.Contains(e.Status)).OrderBy(e => e.EmployeeNo).Take(2).ToListAsync(ct).ConfigureAwait(false);
            if (own.Count != 1)
            {
                return (null, PeopleResult.Fields(new Dictionary<string, string[]> { ["employmentId"] = [NoEmployment] }));
            }

            employment = own[0];
        }

        if (employment is null || !await scope.AllowsAsync(new Loan { CompanyId = employment.CompanyId, EmploymentId = employment.Id }, LoanAccess.Scope, "create", Source, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        return Current.Contains(employment.Status) ? (employment, null) : (null, PeopleResult.Conflict(NotCurrent));
    }

    // ------------------------------------------------------------------ detail

    public async Task<PeopleResult> DetailAsync(Guid loanId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LoanPermissions.LoansRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var loan = await db.Set<Loan>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken).ConfigureAwait(false);
        if (loan is null || !await scope.AllowsAsync(loan, LoanAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var type = await db.Set<LoanType>().AsNoTracking().FirstAsync(t => t.Id == loan.LoanTypeId, cancellationToken).ConfigureAwait(false);
        var employment = await db.Set<Employment>().AsNoTracking().FirstAsync(e => e.Id == loan.EmploymentId, cancellationToken).ConfigureAwait(false);
        var installments = await (from i in db.Set<LoanInstallment>().AsNoTracking()
                                  join p in db.Set<PayPeriod>().AsNoTracking() on i.PayPeriodId equals p.Id
                                  where i.LoanId == loanId
                                  orderby i.Seq
                                  select new { Row = i, p.StartDate, p.EndDate }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ledger = await db.Set<LoanTransaction>().AsNoTracking().Where(t => t.LoanId == loanId).OrderBy(t => t.TxnDate).ThenBy(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var loanAccess = (await (await fields.PlanAsync(LoanAccess.Fields, LoanAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false)).ResolveAsync([loan], Source, cancellationToken).ConfigureAwait(false))[0];
        var instAccess = await (await fields.PlanAsync(LoanInstallmentAccess.Fields, LoanInstallmentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            .ResolveAsync([.. installments.Select(i => i.Row)], Source, cancellationToken).ConfigureAwait(false);
        var txAccess = await (await fields.PlanAsync(LoanTransactionAccess.Fields, LoanTransactionAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            .ResolveAsync([.. ledger], Source, cancellationToken).ConfigureAwait(false);

        var view = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = loan.Id,
            ["loanNo"] = loan.LoanNo,
            ["status"] = loan.Status,
            ["employmentId"] = loan.EmploymentId,
            ["employeeNo"] = employment.EmployeeNo,
            ["loanType"] = new { id = type.Id, code = type.Code, kind = type.Kind, nameI18n = JsonSerializer.Deserialize<JsonElement>(type.NameI18n.RootElement.GetRawText()) },
            ["currencyCode"] = loan.CurrencyCode,
            ["interestMethod"] = loan.InterestMethod,
            ["interestRate"] = loan.InterestRate,
            ["installments"] = loan.Installments,
            ["startPeriodId"] = loan.StartPeriodId,
            ["guarantorEmploymentId"] = loan.GuarantorEmploymentId,
            ["disbursedOn"] = loan.DisbursedOn,
            ["workflowInstanceId"] = loan.WorkflowInstanceId,
            ["reason"] = loan.Reason,
            ["rowVersion"] = loan.RowVersion,
        };
        Show(view, loanAccess, "principal", "principal", loan.Principal);
        var balance = ledger.Sum(t => t.Amount);
        if (txAccess.Count == 0 || txAccess.All(a => a.Of("amount") >= FieldAccessLevel.ReadOnly))
        {
            view["balance"] = balance;
        }

        view["schedule"] = installments.Select((x, i) =>
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = x.Row.Id, ["seq"] = x.Row.Seq, ["payPeriodId"] = x.Row.PayPeriodId, ["periodStart"] = x.StartDate, ["periodEnd"] = x.EndDate, ["status"] = x.Row.Status,
                ["recovered"] = x.Row.Status is "deducted" or "settled_early",
            };
            Show(row, instAccess[i], "principal_amount", "principalAmount", x.Row.PrincipalAmount);
            Show(row, instAccess[i], "interest_amount", "interestAmount", x.Row.InterestAmount);
            return row;
        }).ToList();
        view["ledger"] = ledger.Select((t, i) =>
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = t.Id, ["kind"] = t.Kind, ["txnDate"] = t.TxnDate, ["reference"] = t.Reference, ["sourceType"] = t.SourceType };
            Show(row, txAccess[i], "amount", "amount", t.Amount);
            return row;
        }).ToList();
        view["pendingReceipts"] = (await held.PendingAsync(LoanEvents.ReceiptKind, loanId, cancellationToken).ConfigureAwait(false)).Count;
        return new PeopleResult(PeopleOutcome.Done, Body: view);
    }

    private static void Show(Dictionary<string, object?> view, FieldAccessMap access, string column, string name, object? value)
    {
        if (access.Of(column) >= FieldAccessLevel.ReadOnly)
        {
            view[name] = value;
        }
    }

    // ------------------------------------------------------------------ cancel and disburse

    public async Task<PeopleResult> CancelAsync(Guid loanId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LoanPermissions.Cancel, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        try
        {
            PeopleResult? result = null;
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await LoanLedger.LockAsync(db, loanId, cancellationToken).ConfigureAwait(false);
                var loan = await db.Set<Loan>().FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken).ConfigureAwait(false);
                if (loan is null || !await scope.AllowsAsync(loan, LoanAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
                {
                    result = PeopleResult.NotFound;
                    return;
                }

                if (!await scope.AllowsAsync(loan, LoanAccess.Scope, "cancel", Source, cancellationToken).ConfigureAwait(false))
                {
                    result = PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
                    return;
                }

                // An application still waiting is withdrawn through the approval (its requester); an approved loan is cancelled here while nothing has been paid out.
                if (loan.Status != "approved" || await db.Set<LoanTransaction>().AsNoTracking().AnyAsync(t => t.LoanId == loanId, cancellationToken).ConfigureAwait(false))
                {
                    result = PeopleResult.Conflict(NotCancellable);
                    return;
                }

                db.RemoveRange(await db.Set<LoanInstallment>().Where(i => i.LoanId == loanId).ToListAsync(cancellationToken).ConfigureAwait(false));
                loan.Status = "cancelled";
                EmploymentApplier.Bump(loan, clock.UtcNow, actor.UserId);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new PeopleResult(PeopleOutcome.Done, Body: new { id = loan.Id, status = loan.Status });
            }).ConfigureAwait(false);
            return result!;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return PersonService.Refusal(pg);
        }
    }

    public async Task<PeopleResult> DisburseAsync(Guid loanId, LoanDisbursementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LoanPermissions.Disburse, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var reference = request.Reference?.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            errors["reference"] = [ReferenceRequired];
        }
        else if (reference.Length > ReferenceLength)
        {
            errors["reference"] = [PersonService.TooLong];
        }

        if (request.Date is null)
        {
            errors["date"] = [DateRequired];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        try
        {
            PeopleResult? result = null;
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await LoanLedger.LockAsync(db, loanId, cancellationToken).ConfigureAwait(false);
                var loan = await db.Set<Loan>().FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken).ConfigureAwait(false);
                if (loan is null || !await scope.AllowsAsync(loan, LoanAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
                {
                    result = PeopleResult.NotFound;
                    return;
                }

                if (!await scope.AllowsAsync(loan, LoanAccess.Scope, "disburse", Source, cancellationToken).ConfigureAwait(false))
                {
                    result = PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
                    return;
                }

                var employment = await db.Set<Employment>().AsNoTracking().FirstAsync(e => e.Id == loan.EmploymentId, cancellationToken).ConfigureAwait(false);
                if (employment.UserRef == actor.UserId)
                {
                    result = PeopleResult.Conflict(OwnLoan); // nobody pays out their own loan (FR-XM-32)
                    return;
                }

                if (loan.Status != "approved")
                {
                    result = PeopleResult.Conflict(NotApproved);
                    return;
                }

                var now = clock.UtcNow;
                var today = await applier.TodayAsync(loan.CompanyId, now, cancellationToken).ConfigureAwait(false);
                if (request.Date!.Value > today)
                {
                    result = PeopleResult.Fields(new Dictionary<string, string[]> { ["date"] = [DateFuture] });
                    return;
                }

                var date = request.Date.Value;
                db.Add(LoanLedger.Entry(loan, "disbursement", loan.Principal, date, LoanLedger.SourceLoan, loan.Id, reference, now, actor.UserId));
                loan.Status = "active";
                loan.DisbursedOn = date;
                EmploymentApplier.Bump(loan, now, actor.UserId);
                db.Add(LoanLedger.Event(LoanEvents.Disbursed, loan, new
                {
                    loanId = loan.Id, loanNo = loan.LoanNo, companyId = loan.CompanyId, employmentId = loan.EmploymentId, loanTypeId = loan.LoanTypeId,
                    kind = await db.Set<LoanType>().AsNoTracking().Where(t => t.Id == loan.LoanTypeId).Select(t => t.Kind).FirstAsync(cancellationToken).ConfigureAwait(false),
                    principal = loan.Principal, currencyCode = loan.CurrencyCode, disbursedOn = LoanLedger.Iso(date), reference, installments = loan.Installments,
                }, now, actor.UserId));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new PeopleResult(PeopleOutcome.Done, Body: new { id = loan.Id, status = loan.Status, disbursedOn = date });
            }).ConfigureAwait(false);
            return result!;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return PersonService.Refusal(pg);
        }
    }

    // ------------------------------------------------------------------ receipts

    public async Task<PeopleResult> ReceiptAsync(Guid loanId, LoanReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LoanPermissions.Receipt, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var reference = request.Reference?.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            errors["reference"] = [ReferenceRequired];
        }
        else if (reference.Length > ReferenceLength)
        {
            errors["reference"] = [PersonService.TooLong];
        }

        if (request.Date is null)
        {
            errors["date"] = [DateRequired];
        }

        var full = request.FullSettlement == true;
        if (!full && request.Amount is not > 0m)
        {
            errors["amount"] = [ReceiptAmount];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        try
        {
            PeopleResult? result = null;
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await LoanLedger.LockAsync(db, loanId, cancellationToken).ConfigureAwait(false);
                var loan = await db.Set<Loan>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken).ConfigureAwait(false);
                if (loan is null || !await scope.AllowsAsync(loan, LoanAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
                {
                    result = PeopleResult.NotFound;
                    return;
                }

                if (!await scope.AllowsAsync(loan, LoanAccess.Scope, "receipt", Source, cancellationToken).ConfigureAwait(false))
                {
                    result = PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
                    return;
                }

                var employment = await db.Set<Employment>().AsNoTracking().FirstAsync(e => e.Id == loan.EmploymentId, cancellationToken).ConfigureAwait(false);
                if (employment.UserRef == actor.UserId)
                {
                    result = PeopleResult.Conflict(OwnLoan);
                    return;
                }

                var today = await applier.TodayAsync(loan.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
                if (request.Date!.Value > today)
                {
                    result = PeopleResult.Fields(new Dictionary<string, string[]> { ["date"] = [DateFuture] });
                    return;
                }

                var check = await LoanReceipts.CheckAsync(db, loan, full ? null : request.Amount, full, cancellationToken).ConfigureAwait(false);
                if (check.Problem is not null)
                {
                    result = PeopleResult.Conflict(check.Problem);
                    return;
                }

                if ((await held.PendingAsync(LoanEvents.ReceiptKind, loanId, cancellationToken).ConfigureAwait(false)).Count > 0)
                {
                    result = PeopleResult.Conflict(ReceiptWaiting);
                    return;
                }

                var plan = new LoanReceiptPlan(loan.Id, loan.CompanyId, check.Amount, request.Date.Value, reference!, full, string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim());
                var payload = PayHeldChanges.Seal(await keys.GetAsync(cancellationToken).ConfigureAwait(false), plan);
                var requestId = await held.SubmitAsync(new HeldChangeRequest(LoanEvents.ReceiptKind, LoanAccess.Scope.Resource, loan.Id, payload, plan.Reason), tx.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new PeopleResult(PeopleOutcome.Pending, Body: new { status = "pending", requestId });
            }).ConfigureAwait(false);
            return result!;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return PersonService.Refusal(pg);
        }
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
}

/// <summary>An application worked out: the facts, the type's rules, the schedule against the periods it falls in, and what the applicant has to be told.</summary>
internal sealed record LoanPlan(
    LoanContext Context,
    LoanType Type,
    LoanPolicyRules Rules,
    decimal Principal,
    int Installments,
    IReadOnlyList<LoanInstallmentPlan> Schedule,
    LoanPeriods Periods,
    Employment? Guarantor,
    IReadOnlyList<LoanProblem> Problems,
    IReadOnlyList<LoanProblem> Warnings,
    DateOnly Today);
