using System.Text.Json;
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
/// Loan and advance types (slice S1.41 Build item 1; FR-HRM-LON-01). A type names the deduction component payroll takes the installments into and the
/// <c>loan</c> policy that holds its rules (eligibility, the most that may be lent, the interest, the longest repayment); the type holds no number itself
/// (AS-4). The generated writes of <c>hrm.loan_types</c> are closed: this service checks that the policy is a <c>loan</c> policy of the company and that the
/// component is a deduction. A type is switched off, never deleted: loans refer to it. Loans against the provident fund (kind <c>pf_loan</c>) are S1.44's.
/// </summary>
internal sealed class LoanTypeService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope) : ILoanTypeService
{
    public const string CodeRequired = "errors.hrm.loan.type.code_required";
    public const string NameRequired = "errors.hrm.loan.type.name_required";
    public const string KindInvalid = "errors.hrm.loan.type.kind";
    public const string KindLater = "errors.hrm.loan.type.kind_later";
    public const string PolicyRequired = "errors.hrm.loan.type.policy_required";
    public const string PolicyNotLoan = "errors.hrm.loan.type.policy_not_loan";
    public const string ComponentRequired = "errors.hrm.loan.type.component_required";
    public const string ComponentNotDeduction = "errors.hrm.loan.type.component_not_deduction";
    public const string CodeTaken = "errors.hrm.loan.type.code_taken";

    private const int CodeLength = 40;
    private const int NameLength = 200;

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> CreateAsync(LoanTypeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LoanPermissions.TypesCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.CompanyId is not { } company)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["companyId"] = [PersonService.Required] });
        }

        var now = clock.UtcNow;
        var row = new LoanType
        {
            Id = Guid.CreateVersion7(now), CompanyId = company, NameI18n = JsonDocument.Parse("{}"), IsActive = request.IsActive ?? true,
            CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId, RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, LoanTypeAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var errors = await ApplyAsync(row, request, creating: true, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        db.Add(row);
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid typeId, LoanTypeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LoanPermissions.TypesUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<LoanType>().FirstOrDefaultAsync(t => t.Id == typeId && !t.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, LoanTypeAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, LoanTypeAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        if (request.CompanyId is { } other && other != row.CompanyId)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["companyId"] = [PersonService.CannotClear] });
        }

        var errors = await ApplyAsync(row, request, creating: false, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string[]>> ApplyAsync(LoanType row, LoanTypeRequest r, bool creating, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string field, string key) => errors[field] = [.. errors.GetValueOrDefault(field, []).Append(key).Distinct(StringComparer.Ordinal)];

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
            else if ((await db.Set<LoanType>().AsNoTracking().Where(t => t.CompanyId == row.CompanyId && t.Id != row.Id && !t.IsDeleted).Select(t => t.Code).ToListAsync(ct).ConfigureAwait(false))
                .Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
            {
                Fail("code", CodeTaken);
            }
            else
            {
                row.Code = code;
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
            }
        }

        if (creating || r.Kind is not null)
        {
            if (r.Kind is "pf_loan")
            {
                Fail("kind", KindLater);
            }
            else if (r.Kind is not ("loan" or "advance"))
            {
                Fail("kind", KindInvalid);
            }
            else
            {
                row.Kind = r.Kind;
            }
        }

        if (creating || r.PolicyId is not null)
        {
            if (r.PolicyId is not { } policyId)
            {
                Fail("policyId", PolicyRequired);
            }
            else if (!await db.Set<Policy>().AsNoTracking().AnyAsync(p => p.Id == policyId && p.CompanyId == row.CompanyId && !p.IsDeleted && p.PolicyType == LoanPolicy.PolicyType, ct).ConfigureAwait(false))
            {
                Fail("policyId", PolicyNotLoan);
            }
            else
            {
                row.PolicyId = policyId;
            }
        }

        if (creating || r.DeductionComponentId is not null)
        {
            if (r.DeductionComponentId is not { } componentId)
            {
                Fail("deductionComponentId", ComponentRequired);
            }
            else if (await db.Set<PayComponent>().AsNoTracking().Where(c => c.Id == componentId && c.CompanyId == row.CompanyId && !c.IsDeleted).Select(c => c.Kind).FirstOrDefaultAsync(ct).ConfigureAwait(false) is not { } kind)
            {
                Fail("deductionComponentId", ComponentRequired);
            }
            else if (kind != "deduction")
            {
                Fail("deductionComponentId", ComponentNotDeduction);
            }
            else
            {
                row.DeductionComponentId = componentId;
            }
        }

        if (r.IsActive is { } active)
        {
            row.IsActive = active;
        }

        return errors;
    }

    private async Task<PeopleResult> SaveAsync(LoanType row, CancellationToken ct)
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
            return PersonService.Refusal(pg);
        }

        return new PeopleResult(created ? PeopleOutcome.Created : PeopleOutcome.Done, Body: new
        {
            id = row.Id, companyId = row.CompanyId, code = row.Code, kind = row.Kind, policyId = row.PolicyId, deductionComponentId = row.DeductionComponentId,
            isActive = row.IsActive, rowVersion = row.RowVersion,
        });
    }
}
