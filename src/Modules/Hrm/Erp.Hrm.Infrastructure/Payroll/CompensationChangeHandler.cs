using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Kernel.Contracts.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// Applies a held salary arrangement or revision when a second person approves it (slice S1.34b, FR-HRM-PAY-04, -06, FR-XM-32: the
/// kernel refuses the requester; the employee never approves their own pay). The approver needs <c>hrm.pay.change.approve</c> and the
/// employment inside their data scope (read and create). Everything is checked again on the approval's transaction (employment
/// still current, the structure and its components, the date still follows the last arrangement), so a change that went stale is
/// void and leaves everything as it was.
/// </summary>
internal sealed class CompensationChangeHandler(IServiceProvider services, IDataScopeFilter scope, IClock clock, ICurrentActor actor, IFieldAccess fields, IFieldEncryptorProvider keys) : IHeldChangeHandler
{
    public const string OwnPay = "errors.hrm.pay.compensation_own";

    public string Kind => PayHeldChanges.CompensationKind;

    public string ApprovePermission => PayPermissions.ChangeApprove;

    public async Task<bool> VisibleAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await ReadAsync(change, cancellationToken).ConfigureAwait(false) is not { } plan)
        {
            return false;
        }

        await using var db = await PayHeldChanges.OnAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var source = new DbContextScopeSource(db);
        var probe = new CompensationAssignment { CompanyId = plan.CompanyId, EmploymentId = plan.EmploymentId, CurrencyCode = string.Empty };
        return await scope.AllowsAsync(probe, CompensationAssignmentAccess.Scope, "read", source, cancellationToken).ConfigureAwait(false);
    }

    public Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) =>
        RunAsync(change, transaction, apply: false, cancellationToken);

    public Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) =>
        RunAsync(change, transaction, apply: true, cancellationToken);

    private async Task<HeldChangeProblem?> RunAsync(HeldChange change, DbTransaction transaction, bool apply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(transaction);
        if (await ReadAsync(change, ct).ConfigureAwait(false) is not { } plan)
        {
            return new HeldChangeProblem("errors.data.invalid_request");
        }

        await using var db = await PayHeldChanges.OnAsync(services, transaction, ct).ConfigureAwait(false);
        var source = new DbContextScopeSource(db);

        // The second person decides on the amounts, so they must be able to read them on this employee's lines, row by row
        // (GHR-06, FR-XM-32; review S1.34b M3).
        if (!await CanSeeAmountsAsync(source, plan, ct).ConfigureAwait(false))
        {
            return new HeldChangeProblem(PayHeldChanges.ApproverCannotSee, Forbidden: true);
        }

        if (apply)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {plan.EmploymentId} for update", ct).ConfigureAwait(false);
        }

        var employment = await db.Set<Employment>().FirstOrDefaultAsync(e => e.Id == plan.EmploymentId && e.CompanyId == plan.CompanyId, ct).ConfigureAwait(false);
        if (employment is null)
        {
            return new HeldChangeProblem("errors.data.not_found");
        }

        // Nobody approves a change of their own pay (FR-XM-32, as for bank accounts: the employee never approves).
        if (employment.UserRef is { } user && user == actor.UserId)
        {
            return new HeldChangeProblem(OwnPay, Forbidden: true);
        }

        var (problems, problem) = await CompensationPlanner.CheckAsync(db, employment, plan, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return new HeldChangeProblem(problem);
        }

        if (problems.Count > 0)
        {
            return new HeldChangeProblem(problems.First().Value[0]);
        }

        if (!apply)
        {
            return null;
        }

        var applier = ActivatorUtilities.CreateInstance<EmploymentApplier>(services, db);
        var now = clock.UtcNow;
        var today = await applier.TodayAsync(plan.CompanyId, now, ct).ConfigureAwait(false);
        await CompensationPlanner.ApplyAsync(db, applier, employment, plan, today, now, actor.UserId, ct).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            return new HeldChangeProblem(CompensationService.Refusal(pg).FieldErrors?.Values.First()[0] ?? "errors.data.invalid_request");
        }

        return null;
    }

    public async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await ReadAsync(change, cancellationToken).ConfigureAwait(false) is not { } plan)
        {
            return new HeldChangeDescription(null, []);
        }

        await using var db = await PayHeldChanges.OnAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var who = await (from e in db.Set<Employment>().AsNoTracking()
                         join p in db.Set<Person>().AsNoTracking() on e.PersonId equals p.Id
                         where e.Id == plan.EmploymentId
                         select new { e.EmployeeNo, Person = p }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var subject = who is null ? null : new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = $"{who.EmployeeNo} {Erp.Hrm.Infrastructure.People.PersonService.DisplayName(who.Person)}",
            ["ur"] = $"{who.EmployeeNo} {who.Person.NameLocal ?? Erp.Hrm.Infrastructure.People.PersonService.DisplayName(who.Person)}",
        };
        var structure = await db.Set<SalaryStructure>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == plan.StructureId, cancellationToken).ConfigureAwait(false);
        var ids = plan.Lines.Select(l => l.ComponentId).ToList();
        var codes = await db.Set<PayComponent>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken).ConfigureAwait(false);
        // The amounts are shown only to an approver who may read salary-class data (GHR-06); otherwise the components only.
        var showAmounts = await CanSeeAmountsAsync(new DbContextScopeSource(db), plan, cancellationToken).ConfigureAwait(false);
        var details = new List<HeldChangeDetail>
        {
            new("changes.detail.from", "date", Date: plan.ValidFrom),
            new("hrm.pay.change.detail.structure", "text", Text: structure?.Code ?? "?"),
            new("hrm.pay.change.detail.reason", "key", Text: "hrm.pay.reason." + plan.ReasonCode),
            new("hrm.pay.change.detail.lines", "items", Items: [.. plan.Lines.Select(l => showAmounts ? $"{codes.GetValueOrDefault(l.ComponentId, "–")}: " + Value(l) : codes.GetValueOrDefault(l.ComponentId, "–"))]),
        };
        return new HeldChangeDescription(subject, details);
    }

    private static string Value(CompensationLineDto l) =>
        l.Amount is { } a ? a.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
        : l.Percent is { } p ? p.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "%"
        : (l.Units ?? 0).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " ×";

    private async Task<CompensationPlan?> ReadAsync(HeldChange change, CancellationToken ct) =>
        PayHeldChanges.Open<CompensationPlan>(await keys.GetAsync(ct).ConfigureAwait(false), change.Payload);

    /// <summary>True when the signed-in approver may read amount and percent on this employment's lines (the row-by-row plan, not a company-wide merge).</summary>
    private async Task<bool> CanSeeAmountsAsync(DbContextScopeSource source, CompensationPlan plan, CancellationToken ct)
    {
        var probe = new CompensationLine { CompanyId = plan.CompanyId, AssignmentId = Guid.Empty, ComponentId = Guid.Empty };
        var filled = await fields.PlanAsync(CompensationLineAccess.Fields, CompensationLineAccess.Scope, "read", source, ct).ConfigureAwait(false);
        var access = (await filled.ResolveAsync([probe], source, ct).ConfigureAwait(false))[0];
        return access.Of("amount") != FieldAccessLevel.Hidden && access.Of("percent") != FieldAccessLevel.Hidden;
    }
}
