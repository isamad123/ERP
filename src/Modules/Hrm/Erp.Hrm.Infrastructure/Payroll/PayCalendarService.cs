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
/// Pay periods and the payroll calendar (slice S1.34a Build items 2 and 3; FR-HRM-PAY-01, FR-HRM-PAY-38). Periods of a tax year are
/// generated from the group's frequency, tax-year start and cut-off and payment offsets (<see cref="PayPeriodRules"/>); a year that
/// already has periods is refused, periods never overlap (exclusion constraint), the cut-off and payment dates change only while a
/// period is <c>future</c>, and a closed period never changes (trigger). The calendar lists periods with their deadlines in the
/// company's business date; the readiness checklist runs the registered checks (<see cref="PayReadinessRegistry"/>). Opening and
/// closing a period belong to the pay run (S1.38). The amounts of a group (its approval threshold) are never part of an answer here.
/// </summary>
internal sealed class PayCalendarService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier,
    PayReadinessRegistry registry,
    IEnumerable<IPayReadinessCheck> checks) : IPayCalendarService
{
    public const string TaxYearRequired = "errors.hrm.pay.tax_year_required";
    public const string TaxYearRange = "errors.hrm.pay.tax_year_range";
    public const string PeriodsExist = "errors.hrm.pay.periods_exist";
    public const string PeriodsOverlap = "errors.hrm.pay.periods_overlap";
    public const string GroupInactive = "errors.hrm.pay.group_inactive";
    public const string WeekdayRequired = "errors.hrm.pay.weekday_required";
    public const string WeekdayUnknown = "errors.hrm.pay.weekday_unknown";
    public const string CutoffBeforeStart = "errors.hrm.pay.cutoff_before_start";
    public const string NotFuture = "errors.hrm.pay.period_not_future";
    public const string DatesOrder = "errors.hrm.pay.period_dates";
    public const string NothingToChange = "errors.hrm.pay.period_nothing";
    public const int CalendarLimit = 400;

    private DbContextScopeSource Source => new(db);

    // ================================================================== generate

    public async Task<PeopleResult> GenerateAsync(Guid groupId, GeneratePeriodsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.PeriodsGenerate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var group = await db.Set<PayGroup>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (group is null || !await scope.AllowsAsync(group, PayGroupAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.TaxYear is not { } year)
        {
            errors["taxYear"] = [TaxYearRequired];
        }
        else if (year is < 2000 or > 2100)
        {
            errors["taxYear"] = [TaxYearRange];
        }

        if (!group.IsActive)
        {
            errors["payGroupId"] = [GroupInactive];
        }

        DayOfWeek? weekday = null;
        if (request.WeekStartsOn is not null)
        {
            weekday = PayPeriodRules.ParseWeekday(request.WeekStartsOn);
            if (weekday is null)
            {
                errors["weekStartsOn"] = [WeekdayUnknown];
            }
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var taxYear = request.TaxYear!.Value;
        var probe = new PayPeriod { CompanyId = group.CompanyId, PayGroupId = group.Id };
        if (!await scope.AllowsAsync(probe, PayPeriodAccess.Scope, "generate", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (await db.Set<PayPeriod>().AsNoTracking().AnyAsync(p => p.PayGroupId == group.Id && p.TaxYear == taxYear, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Conflict(PeriodsExist);
        }

        var last = await db.Set<PayPeriod>().AsNoTracking().Where(p => p.PayGroupId == group.Id && p.TaxYear < taxYear).MaxAsync(p => (DateOnly?)p.EndDate, cancellationToken).ConfigureAwait(false);
        var periods = PayPeriodRules.Generate(group.Frequency, taxYear, group.TaxYearStartMonth, group.CutOffDay, group.PaymentDay, weekday, last);
        if (periods.Count == 0)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["weekStartsOn"] = [WeekdayRequired] });
        }

        if (periods.Any(p => p.CutOff < p.Start))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["payGroupId"] = [CutoffBeforeStart] });
        }

        var now = clock.UtcNow;
        var rows = periods.Select(p => new PayPeriod
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = group.CompanyId,
            PayGroupId = group.Id,
            PeriodNo = p.PeriodNo,
            TaxYear = taxYear,
            StartDate = p.Start,
            EndDate = p.End,
            CutOffDate = p.CutOff,
            PaymentDate = p.Payment,
            Status = "future",
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        }).ToList();
        db.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }

        return new PeopleResult(PeopleOutcome.Created, Body: new { payGroupId = group.Id, taxYear, count = rows.Count, items = rows.Select(View) });
    }

    // ================================================================== change

    public async Task<PeopleResult> ChangeAsync(Guid periodId, ChangePeriodRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.PeriodsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<PayPeriod>().FirstOrDefaultAsync(p => p.Id == periodId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, PayPeriodAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, PayPeriodAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        if (request.CutOffDate is null && request.PaymentDate is null)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Invalid(NothingToChange);
        }

        if (row.Status != "future")
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(NotFuture);
        }

        row.CutOffDate = request.CutOffDate ?? row.CutOffDate;
        row.PaymentDate = request.PaymentDate ?? row.PaymentDate;
        if (!PayPeriodRules.DatesInOrder(row.StartDate, row.CutOffDate, row.PaymentDate))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(new Dictionary<string, string[]> { [request.CutOffDate is null ? "paymentDate" : "cutOffDate"] = [DatesOrder] });
        }

        EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

        return new PeopleResult(PeopleOutcome.Done, Body: View(row));
    }

    // ================================================================== calendar

    public async Task<PeopleResult> CalendarAsync(Guid? groupId, int? taxYear, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayPermissions.PeriodsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var query = (await scope.ApplyAsync(db.Set<PayPeriod>().AsNoTracking(), PayPeriodAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false));
        if (groupId is { } g)
        {
            query = query.Where(p => p.PayGroupId == g);
        }

        if (taxYear is { } y)
        {
            query = query.Where(p => p.TaxYear == y);
        }

        var rows = await query.OrderBy(p => p.StartDate).ThenBy(p => p.PayGroupId).Take(CalendarLimit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var more = rows.Count > CalendarLimit;
        rows = [.. rows.Take(CalendarLimit)];
        var groupIds = rows.Select(r => r.PayGroupId).Distinct().ToList();
        var codes = await db.Set<PayGroup>().AsNoTracking().Where(x => groupIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, cancellationToken).ConfigureAwait(false);
        var todays = new Dictionary<Guid, DateOnly>();
        var items = new List<object>();
        foreach (var p in rows)
        {
            if (!todays.TryGetValue(p.CompanyId, out var today))
            {
                todays[p.CompanyId] = today = await applier.TodayAsync(p.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            }

            items.Add(new
            {
                id = p.Id,
                companyId = p.CompanyId,
                payGroupId = p.PayGroupId,
                payGroupCode = codes.GetValueOrDefault(p.PayGroupId),
                periodNo = p.PeriodNo,
                taxYear = p.TaxYear,
                startDate = p.StartDate,
                endDate = p.EndDate,
                cutOffDate = p.CutOffDate,
                paymentDate = p.PaymentDate,
                status = p.Status,
                daysToCutOff = p.CutOffDate.DayNumber - today.DayNumber,
                daysToPayment = p.PaymentDate.DayNumber - today.DayNumber,
                rowVersion = p.RowVersion,
            });
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { items, more });
    }

    // ================================================================== readiness

    public async Task<PeopleResult> ReadinessAsync(Guid periodId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayPermissions.ReadinessRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var period = await db.Set<PayPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == periodId, cancellationToken).ConfigureAwait(false);
        if (period is null || !await scope.AllowsAsync(period, PayPeriodAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var context = new ReadinessContext(period.CompanyId, period.PayGroupId, period.Id, period.StartDate, period.EndDate);
        var items = new List<object>();
        int passed = 0, failed = 0, unavailable = 0;
        foreach (var item in registry.Items)
        {
            var check = checks.FirstOrDefault(c => string.Equals(c.Code, item.Code, StringComparison.Ordinal));
            var outcome = check is null ? new ReadinessOutcome(ReadinessStatus.NotAvailable) : await check.CheckAsync(context, cancellationToken).ConfigureAwait(false);
            _ = outcome.Status switch
            {
                ReadinessStatus.Passed => passed++,
                ReadinessStatus.Failed => failed++,
                _ => unavailable++,
            };
            items.Add(new
            {
                code = item.Code,
                labelKey = item.LabelKey,
                slice = item.Slice,
                requirement = item.Requirement,
                status = outcome.Status switch { ReadinessStatus.Passed => "passed", ReadinessStatus.Failed => "failed", _ => "not_available" },
                count = outcome.Count,
                employees = outcome.Employees?.Select(e => new { employmentId = e.EmploymentId, employeeNo = e.EmployeeNo }) ?? [],
            });
        }

        // Ready only when every registered item passed: an item that cannot be checked yet is never counted as passed.
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            periodId = period.Id,
            payGroupId = period.PayGroupId,
            ready = failed == 0 && unavailable == 0 && passed > 0,
            summary = new { passed, failed, notAvailable = unavailable },
            items,
        });
    }

    // ================================================================== helpers

    private static object View(PayPeriod p) => new
    {
        id = p.Id,
        payGroupId = p.PayGroupId,
        periodNo = p.PeriodNo,
        taxYear = p.TaxYear,
        startDate = p.StartDate,
        endDate = p.EndDate,
        cutOffDate = p.CutOffDate,
        paymentDate = p.PaymentDate,
        status = p.Status,
        rowVersion = p.RowVersion,
    };

    internal static PeopleResult Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "ex_pay_periods_pay_group_id_dates" or "uq_pay_periods_pay_group_id_start_date" => PeopleResult.Conflict(PeriodsOverlap),
        "uq_pay_periods_pay_group_id_tax_year_period_no" => PeopleResult.Conflict(PeriodsExist),
        "ck_pay_periods_dates" => PeopleResult.Fields(new Dictionary<string, string[]> { ["cutOffDate"] = [DatesOrder] }),
        "ck_pay_periods_fixed" => PeopleResult.Conflict(NotFuture),
        _ => PersonService.Refusal(pg),
    };
}
