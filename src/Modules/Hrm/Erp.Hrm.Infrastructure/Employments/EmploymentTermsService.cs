using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Numbering;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// Contracts and probation of employments (slice S1.20 Build item 13; FR-HRM-EMP-09, FR-HRM-EMP-10, BR-02, ruling Q10). The
/// generated writes of <c>hrm.contracts</c> and <c>hrm.probation_records</c> are closed (R-113): a contract gets its number from the
/// kernel numbering service and is renewed as a new linked contract, never edited; a probation is opened at hire (by the hire
/// service), extended, confirmed through a <c>confirmation</c> event of the change service (so the approval configured for
/// confirmations applies, FR-XM-32) or recorded as not confirmed (the separation itself is S1.25's). Every write checks the
/// permission, the data scope and field security and runs in one transaction holding the employment row's lock; the change log
/// names the acting user (GHR-09). Reminders before the ends go out through the daily job <c>hrm.employment_terms_due</c>.
/// </summary>
internal sealed class EmploymentTermsService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    INumberAllocator numbers,
    EmploymentApplier applier,
    IEmploymentService employments,
    IPolicyResolver policies) : IEmploymentTermsService
{
    public const string ContractBeforeJoining = "errors.hrm.contract.before_joining";
    public const string ContractOverlaps = "errors.hrm.contract.overlaps";
    public const string ContractOpenEnded = "errors.hrm.contract.open_ended";
    public const string ContractNotRenewable = "errors.hrm.contract.not_renewable";
    public const string ContractRenewalStart = "errors.hrm.contract.renewal_start";
    public const string ContractTerms = "errors.hrm.contract.terms";
    public const string NoticeDays = "errors.hrm.contract.notice_days";
    public const string NoOpenProbation = "errors.hrm.probation.none_open";
    public const string ExtendNotLater = "errors.hrm.probation.extend_not_later";
    public const string ExtensionLimit = "errors.hrm.probation.extension_limit";
    public const string DecisionBeforeStart = "errors.hrm.probation.decision_before_start";
    public const string EmploymentWaiting = "errors.hrm.contract.employment_waiting";
    public const string AfterEnd = "errors.hrm.contract.after_end";

    private const int TermsLength = 16_384;

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];
    private static readonly string[] OpenContract = ["draft", "active"];
    private static readonly string[] ExtendFields = ["extended_to", "status"];
    private static readonly string[] DecisionFields = ["decision_date", "status"];

    private DbContextScopeSource Source => new(db);

    // ================================================================== contracts (FR-HRM-EMP-09)

    public async Task<PeopleResult> CreateContractAsync(Guid employmentId, ContractRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.ContractsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (employment, problem) = await CurrentEmploymentAsync(employmentId, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var contract = NewContract(employment!, request, request.StartDate, request.Terms, request.RenewalNoticeDays, errors);
        if (contract.StartDate < employment!.JoiningDate && !errors.ContainsKey("startDate"))
        {
            errors["startDate"] = [ContractBeforeJoining];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        return await SaveContractAsync(employment, contract, renewed: null, expected: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> RenewContractAsync(Guid contractId, ContractRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.ContractsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var old = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken).ConfigureAwait(false);
        if (old is null || !await scope.AllowsAsync(old, ContractAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var (employment, problem) = await CurrentEmploymentAsync(old.EmploymentId, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        // A renewal is a new contract linked to the one it renews, starting after it ends; the renewed one is never edited (FR-XM-05).
        if (old.Status is not ("active" or "expired"))
        {
            return PeopleResult.Conflict(ContractNotRenewable);
        }

        if (old.EndDate is not { } end)
        {
            return PeopleResult.Conflict(ContractOpenEnded);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var terms = request.Terms ?? old.Terms.RootElement.Clone();
        var contract = NewContract(employment!, request, request.StartDate ?? end.AddDays(1), terms, request.RenewalNoticeDays ?? old.RenewalNoticeDays, errors);
        contract.RenewsContractId = old.Id;
        if (contract.StartDate <= end && !errors.ContainsKey("startDate"))
        {
            errors["startDate"] = [ContractRenewalStart];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        return await SaveContractAsync(employment!, contract, old.Id, expected, cancellationToken).ConfigureAwait(false);
    }

    private Contract NewContract(Employment employment, ContractRequest request, DateOnly? start, JsonElement? terms, int? noticeDays, Dictionary<string, string[]> errors)
    {
        var now = clock.UtcNow;
        var contract = new Contract
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = employment.CompanyId,
            EmploymentId = employment.Id,
            ContractNo = string.Empty,
            EndDate = request.EndDate,
            RenewalNoticeDays = noticeDays,
            FileId = request.FileId,
            Status = "active",
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
            Terms = JsonDocument.Parse("{}"),
        };
        if (start is not { } s)
        {
            errors["startDate"] = [PersonService.Required];
        }
        else
        {
            contract.StartDate = s;
            if (request.EndDate is { } e && e < s)
            {
                errors["endDate"] = [PersonService.DateOrder];
            }
        }

        if (noticeDays is { } days && days is < 1 or > 365)
        {
            errors["renewalNoticeDays"] = [NoticeDays];
        }

        // Terms are an object of the contract's own wording (working hours, notice, place of work); at most 16 KB of text.
        if (terms is { } t)
        {
            if (t.ValueKind != JsonValueKind.Object || t.GetRawText().Length > TermsLength)
            {
                errors["terms"] = [ContractTerms];
            }
            else
            {
                contract.Terms = JsonDocument.Parse(t.GetRawText());
            }
        }

        return contract;
    }

    private async Task<PeopleResult> SaveContractAsync(Employment employment, Contract contract, Guid? renewed, long? expected, CancellationToken ct)
    {
        if (await EndProblemAsync(employment, contract, ct).ConfigureAwait(false) is { } ended)
        {
            return ended;
        }

        if (!await scope.AllowsAsync(contract, ContractAccess.Scope, "create", Source, ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var map = await fields.ForAsync(ContractAccess.Fields, "create", contract.CompanyId, ct).ConfigureAwait(false);
        var keys = new List<string> { "start_date", "terms" };
        keys.AddRange(contract.EndDate is null ? [] : ["end_date"]);
        keys.AddRange(contract.RenewalNoticeDays is null ? [] : ["renewal_notice_days"]);
        keys.AddRange(contract.FileId is null ? [] : ["file_id"]);
        if (keys.Where(k => ContractAccess.Fields.Find(k) is not null && !map.CanEdit(k)).ToList() is { Count: > 0 } denied)
        {
            return NotEditable(denied);
        }

        // The number of the company's series, taken on the numbering service's own connection before the transaction (FR-XM-06:
        // never reused; a refused contract leaves a gap).
        try
        {
            contract.ContractNo = await numbers.AssignOnCreateAsync(new NumberRequest("hrm.contract_no", contract.CompanyId, null, contract.StartDate), ct).ConfigureAwait(false);
        }
        catch (NumberingException ex)
        {
            return PeopleResult.Invalid(ex.ErrorKey);
        }

        return await InTransactionAsync(async () =>
        {
            await LockAsync(employment.Id, ct).ConfigureAwait(false);
            if (renewed is { } renewedId)
            {
                var old = await db.Set<Contract>().FirstAsync(c => c.Id == renewedId, ct).ConfigureAwait(false);
                if (old.RowVersion != expected)
                {
                    return PeopleResult.Conflict(PersonService.Concurrency);
                }

                if (old.Status is not ("active" or "expired"))
                {
                    return PeopleResult.Conflict(ContractNotRenewable);
                }

                old.Status = "renewed";
                EmploymentApplier.Bump(old, clock.UtcNow, actor.UserId);
            }

            // One contract in force at a time: no open contract of the employment may overlap (the renewed one is no longer open).
            var others = await db.Set<Contract>().AsNoTracking()
                .Where(c => c.EmploymentId == employment.Id && OpenContract.Contains(c.Status) && c.Id != renewed).ToListAsync(ct).ConfigureAwait(false);
            if (others.Any(o => o.StartDate <= (contract.EndDate ?? DateOnly.MaxValue) && contract.StartDate <= (o.EndDate ?? DateOnly.MaxValue)))
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["startDate"] = [ContractOverlaps] });
            }

            db.Add(contract);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Created, Body: new
            {
                id = contract.Id,
                employmentId = contract.EmploymentId,
                contractNo = contract.ContractNo,
                status = contract.Status,
                renewsContractId = contract.RenewsContractId,
                rowVersion = contract.RowVersion,
            });
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A contract fits the employment's life (review S1.20c finding 8): not on an employment whose start still waits for approval (a
    /// held transfer), and within a recorded end (a transfer's separation; its date is the first day without the employment).
    /// Ending the contracts of an ended employment is S1.25's (R-227).
    /// </summary>
    private async Task<PeopleResult?> EndProblemAsync(Employment employment, Contract contract, CancellationToken ct)
    {
        var events = await db.Set<EmploymentEvent>().AsNoTracking().Where(e => e.EmploymentId == employment.Id).ToListAsync(ct).ConfigureAwait(false);
        if (events.Any(e => e.Kind == "hire" && e.Status == "pending_approval"))
        {
            return PeopleResult.Conflict(EmploymentWaiting);
        }

        var end = events.Where(e => e.Kind == EmploymentApplier.SeparationKind && e.Status is "pending_approval" or "approved" or "effective").Select(e => (DateOnly?)e.EffectiveDate).Min();
        return end is { } last && (contract.StartDate >= last || contract.EndDate is not { } finish || finish >= last)
            ? PeopleResult.Fields(new Dictionary<string, string[]> { [contract.StartDate >= last ? "startDate" : "endDate"] = [AfterEnd] })
            : null;
    }

    // ================================================================== probation (FR-HRM-EMP-10, ruling Q10)

    public Task<PeopleResult> ExtendProbationAsync(Guid employmentId, ProbationDecisionRequest request, CancellationToken cancellationToken) =>
        ProbationAsync(employmentId, request, extend: true, cancellationToken);

    public Task<PeopleResult> NotConfirmProbationAsync(Guid employmentId, ProbationDecisionRequest request, CancellationToken cancellationToken) =>
        ProbationAsync(employmentId, request, extend: false, cancellationToken);

    private async Task<PeopleResult> ProbationAsync(Guid employmentId, ProbationDecisionRequest r, bool extend, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.ProbationUpdate, ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (r.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        if (extend && r.ExtendedTo is null)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["extendedTo"] = [PersonService.Required] });
        }

        return await InTransactionAsync(async () =>
        {
            await LockAsync(employmentId, ct).ConfigureAwait(false);
            var (employment, problem) = await CurrentEmploymentAsync(employmentId, ct).ConfigureAwait(false);
            if (problem is not null)
            {
                return problem;
            }

            var (record, missing) = await OpenProbationAsync(employment!, ct).ConfigureAwait(false);
            if (missing is not null)
            {
                return missing;
            }

            if (record!.RowVersion != expected)
            {
                return PeopleResult.Conflict(PersonService.Concurrency);
            }

            var changed = extend ? ExtendFields : DecisionFields;
            if (await NotEditableAsync(record, changed, ct).ConfigureAwait(false) is { Count: > 0 } denied)
            {
                return NotEditable(denied);
            }

            if (extend)
            {
                // An extension moves the last day later (BR-02 style ordering); the review outcome comes with S1.25's form.
                if (r.ExtendedTo!.Value <= (record.ExtendedTo ?? record.EndDate))
                {
                    return PeopleResult.Fields(new Dictionary<string, string[]> { ["extendedTo"] = [ExtendNotLater] });
                }

                // The probation policy in force when the probation started may limit extensions (S1.23 ruling Q3): at most that many
                // months after the original last day (0: none). Without a policy, or without a limit, any later date is accepted.
                var policy = (await policies.ResolveAsync(employment!.Id, ProbationPolicy.Type, record.StartDate, ct).ConfigureAwait(false))?.Policy;
                if (ProbationPolicy.ExtensionMonthsMax(policy) is { } limit && r.ExtendedTo.Value > record.EndDate.AddDays(1).AddMonths(limit).AddDays(-1))
                {
                    return new PeopleResult(PeopleOutcome.Invalid, "errors.data.invalid_request", FieldErrors: new Dictionary<string, string[]> { ["extendedTo"] = [ExtensionLimit] },
                        Extensions: new Dictionary<string, object?> { ["months"] = limit, ["latest"] = record.EndDate.AddDays(1).AddMonths(limit).AddDays(-1) });
                }

                record.ExtendedTo = r.ExtendedTo;
                record.Status = "extended";
            }
            else
            {
                var date = r.Date ?? await applier.TodayAsync(employment!.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
                if (date < record.StartDate)
                {
                    return PeopleResult.Fields(new Dictionary<string, string[]> { ["date"] = [DecisionBeforeStart] });
                }

                // Recorded only: the separation that follows is started by the separation services (S1.25).
                record.Status = "not_confirmed";
                record.DecisionDate = date;
            }

            EmploymentApplier.Bump(record, clock.UtcNow, actor.UserId);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: new { id = record.Id, status = record.Status, extendedTo = record.ExtendedTo, decisionDate = record.DecisionDate, rowVersion = record.RowVersion });
        }, ct).ConfigureAwait(false);
    }

    public async Task<PeopleResult> ConfirmProbationAsync(Guid employmentId, ProbationDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(EmploymentPermissions.ProbationUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var (employment, problem) = await CurrentEmploymentAsync(employmentId, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var (record, missing) = await OpenProbationAsync(employment!, cancellationToken).ConfigureAwait(false);
        if (missing is not null)
        {
            return missing;
        }

        if (await NotEditableAsync(record!, DecisionFields, cancellationToken).ConfigureAwait(false) is { Count: > 0 } denied)
        {
            return NotEditable(denied);
        }

        // The confirmation is an employment event (BR-02 checked there): it applies at once, waits for the approval route when the
        // company lists confirmations (FR-XM-32), or takes effect on its date; the probation closes when it applies (EmploymentApplier).
        var date = request.Date ?? await applier.TodayAsync(employment!.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return await employments.RecordChangeAsync(employmentId, new EmploymentChangeRequest
        {
            Kind = "confirmation",
            ReasonCode = request.ReasonCode ?? (date < (record!.ExtendedTo ?? record.EndDate) ? "early_confirmation" : "probation_completed"),
            EffectiveDate = date,
            Notes = request.Notes,
            RowVersion = request.RowVersion,
        }, cancellationToken).ConfigureAwait(false);
    }

    // ================================================================== helpers

    private async Task<(Employment? Employment, PeopleResult? Problem)> CurrentEmploymentAsync(Guid id, CancellationToken ct)
    {
        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.NotFound);
        }

        return CurrentStatuses.Contains(employment.Status) ? (employment, null) : (null, PeopleResult.Conflict(EmploymentService.NotCurrent));
    }

    /// <summary>The employment's open probation, tracked, when the caller may change it; else the answer.</summary>
    private async Task<(ProbationRecord? Record, PeopleResult? Problem)> OpenProbationAsync(Employment employment, CancellationToken ct)
    {
        var record = await db.Set<ProbationRecord>().FirstOrDefaultAsync(p => p.EmploymentId == employment.Id && (p.Status == "in_progress" || p.Status == "extended"), ct).ConfigureAwait(false);
        if (record is null)
        {
            return (null, PeopleResult.Conflict(NoOpenProbation));
        }

        if (!await scope.AllowsAsync(record, ProbationRecordAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            return (null, PeopleResult.Conflict(NoOpenProbation));
        }

        return await scope.AllowsAsync(record, ProbationRecordAccess.Scope, "update", Source, ct).ConfigureAwait(false)
            ? (record, null)
            : (null, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
    }

    private async Task<List<string>> NotEditableAsync(ProbationRecord record, IEnumerable<string> keys, CancellationToken ct)
    {
        var plan = await fields.PlanAsync(ProbationRecordAccess.Fields, ProbationRecordAccess.Scope, "update", Source, ct).ConfigureAwait(false);
        var map = (await plan.ResolveAsync([record], Source, ct).ConfigureAwait(false))[0];
        return keys.Where(k => ProbationRecordAccess.Fields.Find(k) is not null && !map.CanEdit(k)).ToList();
    }

    private static PeopleResult NotEditable(List<string> denied) =>
        new(PeopleOutcome.Invalid, FieldShaping.NotEditable, FieldErrors: denied.Distinct(StringComparer.Ordinal).ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal));

    private Task<int> LockAsync(Guid employmentId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.employments where id = {employmentId} for update", ct);

    private async Task<PeopleResult> InTransactionAsync(Func<Task<PeopleResult>> work, CancellationToken ct)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var result = await work().ConfigureAwait(false);
            if (result.Outcome is PeopleOutcome.Created or PeopleOutcome.Done)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
            }

            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return pg.ConstraintName switch
            {
                "fk_contracts_file_id" => PeopleResult.Fields(new Dictionary<string, string[]> { ["fileId"] = ["errors.data.not_found"] }),
                "uq_probation_records_employment_id" => PeopleResult.Conflict(NoOpenProbation),
                _ => PersonService.Refusal(pg),
            };
        }
    }
}
