using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Kernel.Contracts.Access;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// Applies a held change of a salary structure when a second person approves it (slice S1.34b, FR-HRM-PAY-04, FR-XM-32: the kernel
/// refuses the requester). The approver needs <c>hrm.pay.change.approve</c> and the structure inside their data scope. Everything is
/// checked again on the approval's transaction (components still exist and are active, the start date still follows the last
/// one), and a refusal leaves everything as it was.
/// </summary>
internal sealed class StructureChangeHandler(IServiceProvider services, IDataScopeFilter scope, IClock clock, ICurrentActor actor, IFormulaValidator formulas) : IHeldChangeHandler
{
    public string Kind => PayHeldChanges.StructureKind;

    public string ApprovePermission => PayPermissions.ChangeApprove;

    public async Task<bool> VisibleAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read(change) is not { } plan)
        {
            return false;
        }

        await using var db = await PayHeldChanges.OnAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var source = new DbContextScopeSource(db);
        var probe = new SalaryStructure { CompanyId = plan.CompanyId, Code = string.Empty, NameI18n = JsonDocument.Parse("{}") };
        return await scope.AllowsAsync(probe, SalaryStructureAccess.Scope, "read", source, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(probe, SalaryStructureAccess.Scope, "update", source, cancellationToken).ConfigureAwait(false);
    }

    public Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) =>
        RunAsync(change, transaction, apply: false, cancellationToken);

    public Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) =>
        RunAsync(change, transaction, apply: true, cancellationToken);

    private async Task<HeldChangeProblem?> RunAsync(HeldChange change, DbTransaction transaction, bool apply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(transaction);
        if (Read(change) is not { } plan)
        {
            return new HeldChangeProblem("errors.data.invalid_request");
        }

        await using var db = await PayHeldChanges.OnAsync(services, transaction, ct).ConfigureAwait(false);
        if (apply)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.salary_structures where id = {plan.StructureId} for update", ct).ConfigureAwait(false);
        }

        var structure = await db.Set<SalaryStructure>().FirstOrDefaultAsync(s => s.Id == plan.StructureId && s.CompanyId == plan.CompanyId && !s.IsDeleted, ct).ConfigureAwait(false);
        if (structure is null)
        {
            return new HeldChangeProblem("errors.data.not_found");
        }

        var problems = await StructurePlanner.CheckAsync(db, plan, formulas, ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            return new HeldChangeProblem(problems.First().Value[0]);
        }

        if (!apply)
        {
            return null;
        }

        await StructurePlanner.ApplyAsync(db, structure, plan, clock.UtcNow, actor.UserId, ct).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            return new HeldChangeProblem(SalaryStructureService.Refusal(pg).Error ?? "errors.data.invalid_request");
        }

        return null;
    }

    public async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read(change) is not { } plan)
        {
            return new HeldChangeDescription(null, []);
        }

        await using var db = await PayHeldChanges.OnAsync(services, transaction, cancellationToken).ConfigureAwait(false);
        var structure = await db.Set<SalaryStructure>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == plan.StructureId, cancellationToken).ConfigureAwait(false);
        var subject = structure is null ? null : structure.NameI18n.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => $"{structure.Code} {p.Value.GetString()}", StringComparer.Ordinal);
        var details = new List<HeldChangeDetail>();
        if (plan.AssignRuleJson is not null)
        {
            details.Add(new HeldChangeDetail("hrm.pay.change.detail.assign_rule", "text", Text: plan.AssignRuleJson));
        }

        if (plan.Lines is not null)
        {
            if (plan.From is { } from)
            {
                details.Add(new HeldChangeDetail("changes.detail.from", "date", Date: from));
            }

            var ids = plan.Lines.Select(l => l.ComponentId).ToList();
            var codes = await db.Set<PayComponent>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken).ConfigureAwait(false);
            details.Add(new HeldChangeDetail("hrm.pay.change.detail.components", "items", Items: [.. plan.Lines.OrderBy(l => l.EvaluationOrder).Select(l => $"{l.EvaluationOrder}. {codes.GetValueOrDefault(l.ComponentId, "?")}")]));
        }

        return new HeldChangeDescription(subject, details);
    }

    private static StructurePlan? Read(HeldChange change)
    {
        try
        {
            return JsonSerializer.Deserialize<StructurePlan>(change.Payload, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
