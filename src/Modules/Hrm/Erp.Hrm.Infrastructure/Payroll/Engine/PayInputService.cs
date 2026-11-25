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

namespace Erp.Hrm.Infrastructure.Payroll.Engine;

/// <summary>
/// One-time payroll inputs (slice S1.35a Build item 1, ruling Q5; FR-HRM-PAY-07, -08): the validated bulk insert of <c>hrm.pay_inputs</c> (all rows or
/// none) and the cancellation of an input no run has used. An input names an employment, one of its pay periods (of the pay group the employment is
/// in during it), a component that takes one-time amounts (or whose method is manual; never a statutory one) and an amount or units paid at the
/// component's rate, in the pay group's currency. Amounts are salary-class data: entering one needs edit access to the class (GHR-06). Nobody enters
/// inputs to their own pay (FR-XM-32). A period whose run is locked takes no new input (corrections go to a later period, FR-XM-05).
/// </summary>
internal sealed class PayInputService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    IFormulaValidator formulas) : IPayInputService
{
    public const int MaxItems = 1000;
    public const string TooMany = "errors.hrm.pay.input.too_many";
    public const string AmountOrUnits = "errors.hrm.pay.input.amount_or_units";
    public const string Negative = "errors.hrm.pay.input.negative";
    public const string SourceTypeInvalid = "errors.hrm.pay.input.source_type";
    public const string NotInGroup = "errors.hrm.pay.input.not_in_group";
    public const string PeriodClosed = "errors.hrm.pay.input.period_closed";
    public const string RunLocked = "errors.hrm.pay.input.run_locked";
    public const string ComponentNotAllowed = "errors.hrm.pay.input.component_not_allowed";
    public const string Statutory = "errors.hrm.pay.input.statutory";
    public const string OwnPay = "errors.hrm.pay.input.own_pay";
    public const string Duplicate = "errors.hrm.pay.input.duplicate";
    public const string Consumed = "errors.hrm.pay.input.consumed";
    public const string Cancelled = "errors.hrm.pay.input.cancelled";

    /// <summary>The source types a person may enter (the import centre writes <c>import</c>; providers of later slices use their own sources).</summary>
    public static readonly string[] EnteredSources = ["manual", "bonus"];

    private static readonly string[] Final = ["locked", "paid", "posted", "closed"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> AddAsync(PayInputsRequest request, Guid? importBatchId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayRunPermissions.InputsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.Items is not { Count: > 0 } items)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["items"] = [PersonService.Required] });
        }

        if (items.Count > MaxItems)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["items"] = [TooMany] });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var now = clock.UtcNow;
        var user = actor.UserId;
        var rows = new List<PayInput>();
        var employmentIds = items.Where(i => i.EmploymentId is not null).Select(i => i.EmploymentId!.Value).Distinct().ToList();
        var periodIds = items.Where(i => i.PayPeriodId is not null).Select(i => i.PayPeriodId!.Value).Distinct().ToList();
        var componentIds = items.Where(i => i.ComponentId is not null).Select(i => i.ComponentId!.Value).Distinct().ToList();
        var employments = await db.Set<Employment>().AsNoTracking().Where(e => employmentIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        var periods = await db.Set<PayPeriod>().AsNoTracking().Where(p => periodIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken).ConfigureAwait(false);
        var groupIds = periods.Values.Select(p => p.PayGroupId).Distinct().ToList();
        var groups = await db.Set<PayGroup>().AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, cancellationToken).ConfigureAwait(false);
        var components = await db.Set<PayComponent>().AsNoTracking().Where(c => componentIds.Contains(c.Id) && !c.IsDeleted).ToDictionaryAsync(c => c.Id, cancellationToken).ConfigureAwait(false);
        var versions = await db.Set<PayComponentVersion>().AsNoTracking().Where(v => componentIds.Contains(v.ComponentId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lockedRuns = (await db.Set<PayRun>().AsNoTracking().Where(r => periodIds.Contains(r.PayPeriodId) && Final.Contains(r.Status)).Select(r => r.PayPeriodId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        for (var n = 0; n < items.Count; n++)
        {
            var item = items[n];
            var at = $"items[{n}]";
            void Fail(string field, string key) => errors[$"{at}.{field}"] = [key];
            if (item.EmploymentId is not { } employmentId || !employments.TryGetValue(employmentId, out var employment))
            {
                Fail("employmentId", item.EmploymentId is null ? PersonService.Required : "errors.data.not_found");
                continue;
            }

            var probe = new PayInput { CompanyId = employment.CompanyId, EmploymentId = employment.Id, CurrencyCode = string.Empty, SourceType = string.Empty, Status = "pending" };
            if (!await scope.AllowsAsync(probe, PayInputAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            {
                Fail("employmentId", "errors.data.not_found");
                continue;
            }

            if (employment.UserRef is { } own && own == user)
            {
                Fail("employmentId", OwnPay);
                continue;
            }

            if (item.PayPeriodId is not { } periodId || !periods.TryGetValue(periodId, out var period) || period.CompanyId != employment.CompanyId)
            {
                Fail("payPeriodId", item.PayPeriodId is null ? PersonService.Required : "errors.data.not_found");
                continue;
            }

            if (period.Status == "closed")
            {
                Fail("payPeriodId", PeriodClosed);
                continue;
            }

            if (lockedRuns.Contains(period.Id))
            {
                Fail("payPeriodId", RunLocked);
                continue;
            }

            var (from, to) = (period.StartDate, period.EndDate);
            var inGroup = await db.Set<EmploymentAssignment>().AsNoTracking()
                .AnyAsync(a => a.EmploymentId == employment.Id && a.PayGroupId == period.PayGroupId && a.ValidFrom <= to && (a.ValidTo == null || a.ValidTo > from), cancellationToken).ConfigureAwait(false);
            if (!inGroup)
            {
                Fail("payPeriodId", NotInGroup);
                continue;
            }

            if (item.ComponentId is not { } componentId || !components.TryGetValue(componentId, out var component) || component.CompanyId != employment.CompanyId || !component.IsActive)
            {
                Fail("componentId", item.ComponentId is null ? PersonService.Required : "errors.data.not_found");
                continue;
            }

            var version = versions.Where(v => v.ComponentId == component.Id && v.ValidFrom <= to && (v.ValidTo == null || v.ValidTo > to)).OrderByDescending(v => v.ValidFrom).FirstOrDefault();
            if (version is null)
            {
                Fail("componentId", PayEngine.ComponentIncomplete);
                continue;
            }

            if (version.StatutoryFlags.RootElement.TryGetProperty(SnapshotVersion.StatutoryFlag, out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                Fail("componentId", Statutory);
                continue;
            }

            if (!component.IsOneTimeAllowed && version.Method != "manual")
            {
                Fail("componentId", ComponentNotAllowed);
                continue;
            }

            if (item.Amount is null && item.Units is null)
            {
                Fail("amount", AmountOrUnits);
                continue;
            }

            if (item.Amount is < 0 || item.Units is < 0)
            {
                Fail(item.Amount is < 0 ? "amount" : "units", Negative);
                continue;
            }

            var sourceType = importBatchId is null ? item.SourceType ?? "manual" : "import";
            if (importBatchId is null && !EnteredSources.Contains(sourceType, StringComparer.Ordinal))
            {
                Fail("sourceType", SourceTypeInvalid);
                continue;
            }

            if (item.Amount is not null)
            {
                // An amount is salary-class data: entering it needs edit access to the class (GHR-06).
                var map = await fields.ForAsync(PayInputAccess.Fields, "create", employment.CompanyId, cancellationToken).ConfigureAwait(false);
                if (!map.CanEdit("amount"))
                {
                    Fail("amount", FieldShaping.NotEditable);
                    continue;
                }
            }

            rows.Add(new PayInput
            {
                Id = Guid.CreateVersion7(now),
                CompanyId = employment.CompanyId,
                EmploymentId = employment.Id,
                PayPeriodId = period.Id,
                ComponentId = component.Id,
                Amount = item.Amount is { } a ? PayRounding.Internal(a) : null,
                CurrencyCode = groups[period.PayGroupId].CurrencyCode,
                Units = item.Units,
                SourceType = sourceType,
                SourceId = item.SourceId,
                ImportBatchId = importBatchId,
                Status = "pending",
                CreatedAt = now,
                CreatedBy = user,
                UpdatedAt = now,
                UpdatedBy = user,
            });
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        try
        {
            db.AddRange(rows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(Duplicate);
        }

        return new PeopleResult(PeopleOutcome.Created, Body: new { items = rows.Select(r => new { id = r.Id, r.EmploymentId, r.PayPeriodId, r.ComponentId, r.Status }).ToList() });
    }

    public async Task<PeopleResult> CancelAsync(Guid inputId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayRunPermissions.InputsCancel, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await db.Set<PayInput>().FirstOrDefaultAsync(i => i.Id == inputId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, PayInputAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        switch (row.Status)
        {
            case "consumed":
                return PeopleResult.Conflict(Consumed, new Dictionary<string, object?> { ["runId"] = row.ConsumedByRunId });
            case "cancelled":
                return PeopleResult.Conflict(Cancelled);
        }

        row.Status = "cancelled";
        EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Done, Body: new { id = row.Id, row.Status, row.RowVersion });
    }

    public async Task<PeopleResult> InputNamesAsync(CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayPermissions.ComponentsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new { items = formulas.KnownInputs.Select(n => new { name = n, formula = "$" + n, labelKey = "hrm.pay.input." + n }).ToList() });
    }
}
