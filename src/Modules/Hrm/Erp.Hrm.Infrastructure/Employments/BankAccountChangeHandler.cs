using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// Applies a held change of payment accounts when a second person approves it (FR-HRM-EMP-06, FR-KRN-IAM-14, FR-XM-32: the kernel
/// refuses the requester). The approver needs <c>hrm.employee_bank_accounts.approve</c> and the employment's accounts inside their data
/// scope (read and update). Everything is checked again on the approval's transaction: the employment is still current, no account
/// changed since the request (else the request is void: "changed meanwhile"), and the split rule holds on every date.
/// </summary>
internal sealed class BankAccountChangeHandler(IServiceProvider services, IDataScopeFilter scope, IClock clock, ICurrentActor actor) : IHeldChangeHandler
{
    public const string OwnAccount = "errors.hrm.bank_account.own_account";

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    public string Kind => BankAccountPlanner.Kind;

    public string ApprovePermission => EmploymentPermissions.BankAccountsApprove;

    public async Task<bool> VisibleAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read(change) is not { } plan)
        {
            return false;
        }

        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        var source = new DbContextScopeSource(db);
        var probe = new EmployeeBankAccount { CompanyId = plan.CompanyId, EmploymentId = plan.EmploymentId, PaymentMode = string.Empty, SplitKind = string.Empty };
        return await scope.AllowsAsync(probe, EmployeeBankAccountAccess.Scope, "read", source, cancellationToken).ConfigureAwait(false)
            && await scope.AllowsAsync(probe, EmployeeBankAccountAccess.Scope, "update", source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The dry run of the approval (the inbox's "can approve"): every check of <see cref="ApplyAsync"/>, nothing written.</summary>
    public Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) =>
        RunAsync(change, transaction, apply: false, cancellationToken);

    public Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) =>
        RunAsync(change, transaction, apply: true, cancellationToken);

    private async Task<HeldChangeProblem?> RunAsync(HeldChange change, DbTransaction transaction, bool apply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(transaction);
        if (Read(change) is not { } plan)
        {
            return new HeldChangeProblem("errors.data.invalid_request");
        }

        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (apply)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {plan.EmploymentId} for update", cancellationToken).ConfigureAwait(false);
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == plan.EmploymentId && e.CompanyId == plan.CompanyId, cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return new HeldChangeProblem("errors.data.not_found");
        }

        if (!CurrentStatuses.Contains(employment.Status))
        {
            return new HeldChangeProblem(EmploymentService.NotCurrent);
        }

        // Nobody approves a change of their own pay accounts (FR-XM-32, as for employment changes: the employee never approves).
        if (employment.UserRef is { } user && user == actor.UserId)
        {
            return new HeldChangeProblem(OwnAccount, Forbidden: true);
        }

        var tracked = await db.Set<EmployeeBankAccount>().Where(a => a.EmploymentId == plan.EmploymentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var (after, stale) = BankAccountPlanner.Simulate(tracked, plan.Ops);
        if (stale is not null)
        {
            return new HeldChangeProblem(stale);
        }

        if (PaySplit.Check(BankAccountPlanner.Split(after!)) is { } split)
        {
            return new HeldChangeProblem(split.ErrorKey);
        }

        if (!apply)
        {
            return null;
        }

        return await BankAccountPlanner.ApplyAsync(db, plan.CompanyId, plan.EmploymentId, tracked, plan.Ops, clock.UtcNow, actor.UserId, cancellationToken).ConfigureAwait(false) is { } refused
            ? new HeldChangeProblem(refused)
            : null;
    }

    public async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read(change) is not { } plan)
        {
            return new HeldChangeDescription(null, []);
        }

        await using var db = await OnAsync(transaction, cancellationToken).ConfigureAwait(false);
        var who = await (from e in db.Set<Employment>().AsNoTracking()
                         join p in db.Set<Person>().AsNoTracking() on e.PersonId equals p.Id
                         where e.Id == plan.EmploymentId
                         select new { e.EmployeeNo, Person = p }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var subject = who is null ? null : new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = $"{who.EmployeeNo} {PersonService.DisplayName(who.Person)}",
            ["ur"] = $"{who.EmployeeNo} {who.Person.NameLocal ?? PersonService.DisplayName(who.Person)}",
        };
        var details = new List<HeldChangeDetail>();
        foreach (var op in plan.Ops)
        {
            details.Add(new HeldChangeDetail("changes.detail.operation", "key", Text: op.Action switch
            {
                "add" => "changes.operation.create",
                "remove" => "changes.operation.delete",
                _ => "changes.operation.update",
            }));
            details.Add(new HeldChangeDetail("changes.detail.bankAccount", "text", Text: op.Action is "add" or "change"
                ? $"{op.Priority} · {op.PaymentMode} · {PaySplit.Describe(op.SplitKind!, op.SplitValue)}"
                : op.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (op.AccountTitle is not null)
            {
                details.Add(new HeldChangeDetail("changes.detail.accountTitle", "text", Text: op.AccountTitle));
            }

            if (op.AccountNumberLast4 is not null)
            {
                details.Add(new HeldChangeDetail("changes.detail.accountNumber", "text", Text: op.AccountNumberLast4));
            }

            if (op.IbanLast4 is not null)
            {
                details.Add(new HeldChangeDetail("changes.detail.iban", "text", Text: "****" + op.IbanLast4));
            }

            if (op.From is { } from)
            {
                details.Add(new HeldChangeDetail("changes.detail.from", "date", Date: from));
            }

            if (op.ValidTo is { } to)
            {
                details.Add(new HeldChangeDetail(op.Action == "end" ? "changes.detail.endDate" : "changes.detail.to", "date", Date: to));
            }
        }

        return new HeldChangeDescription(subject, details);
    }

    /// <summary>An HR context on the approval's connection and transaction, so the rows and their audit entries commit with the decision.</summary>
    private async Task<HrmDbContext> OnAsync(DbTransaction transaction, CancellationToken ct)
    {
        var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        return db;
    }

    private static BankPlan? Read(HeldChange change)
    {
        try
        {
            return JsonSerializer.Deserialize<BankPlan>(change.Payload, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
