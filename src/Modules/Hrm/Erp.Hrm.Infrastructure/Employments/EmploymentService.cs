using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Employments;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Contracts.Identity;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// The employment services (slice S1.20b Build items 5 to 10; FR-HRM-EMP-02, -03, -04, -07, -11, -17, BR-02, GHR-03, GHR-04,
/// FR-KRN-ENT-05, FR-XM-05, FR-XM-32). Every write goes through here (the generated writes are closed, ruling Q1): permission,
/// data scope before and after, field security, validation, one transaction holding the employment row's lock, and the change
/// log written by the audit interceptor with the acting user (GHR-09). The events are the record; the assignment rows follow from
/// them (<see cref="EmploymentTimeline"/>, <see cref="EmploymentApplier"/>).
/// </summary>
internal sealed partial class EmploymentService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ISensitiveAccessLog accessLog,
    INumberAllocator numbers,
    ISettings settings,
    ILimitGuard limits,
    IWorkflowService workflow,
    IUserDirectory users,
    EmploymentApplier applier,
    EmploymentChangeCheck impact,
    PersonService persons) : IEmploymentService
{
    public const string DocumentType = "hrm.employment_event";
    public const string ApprovalKindsSetting = "hrm.employment_events.approval_kinds";

    // Localization keys (employment.en.json; generic ones from the person and kernel catalogues).
    public const string Required = PersonService.Required;
    public const string TooLong = PersonService.TooLong;
    public const string UnknownCode = PersonService.UnknownCode;
    public const string CannotClear = PersonService.CannotClear;
    public const string DateOrder = PersonService.DateOrder;
    public const string RowVersionRequired = PersonService.RowVersionRequired;
    public const string Concurrency = PersonService.Concurrency;
    public const string PersonOrNew = "errors.hrm.employment.person_or_new";
    public const string AlreadyEmployed = "errors.hrm.employment.already_employed";
    public const string PersonRetired = PersonService.Retired;
    public const string NotCurrent = "errors.hrm.employment.not_current";
    public const string BeforeJoining = "errors.hrm.employment.before_joining";
    public const string JoiningAfterToday = "errors.hrm.employment.joining_after_today";
    public const string KindNotRecorded = "errors.hrm.employment_event.kind_not_recorded";
    public const string NothingChanges = "errors.hrm.employment_event.nothing_changes";
    public const string NoAssignmentChanges = "errors.hrm.employment_event.no_assignment_changes";
    public const string EmploymentFieldsCorrection = "errors.hrm.employment_event.employment_fields_by_correction";
    public const string CorrectionTarget = "errors.hrm.employment_event.correction_target";
    public const string CorrectionNeedsTarget = "errors.hrm.employment_event.correction_needs_target";
    public const string AlreadyCorrected = "errors.hrm.employment_event.already_corrected";
    public const string CorrectionPending = "errors.hrm.employment_event.correction_pending";
    public const string AlreadyConfirmed = "errors.hrm.employment.already_confirmed";
    public const string ConfirmationBeforeJoining = "errors.hrm.employment.confirmation_before_joining";
    public const string CostShares = "errors.hrm.assignment.cost_shares";
    public const string CostSum = "errors.hrm.assignment.cost_sum";
    public const string FteRange = "errors.hrm.assignment.fte_range";
    public const string OwnManager = "errors.hrm.assignment.own_manager";
    public const string ManagerCycle = "errors.hrm.assignment.manager_cycle";
    public const string ManagerNotCurrent = "errors.hrm.assignment.manager_not_current";
    public const string NotInCompany = "errors.hrm.assignment.not_in_company";
    public const string UserNotLinkable = "errors.hrm.user_link.user_not_found";
    public const string UserLinkedElsewhere = "errors.hrm.user_link.user_linked";
    public const string UserOtherPerson = "errors.hrm.user_link.other_person";
    public const string NotLinked = "errors.hrm.user_link.not_linked";
    public const string OwnUser = "errors.hrm.user_link.own_user";

    /// <summary>The kinds recorded through <c>POST /api/hrm/employments/{id}/events</c> (hire, rehire, transfer between companies, probation, pay and separation have their own services).</summary>
    public static readonly IReadOnlyList<string> ChangeKinds = EmploymentCatalogue.ChangeKinds;

    /// <summary>Kinds that change the assignment (a confirmation changes none; a correction may).</summary>
    private static readonly string[] AssignmentKinds = ["promotion", "demotion", "transfer", "grade_change", "acting", "secondment"];

    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    private const int TitleLength = 200;
    private const int NotesLength = 2000;

    private static EmploymentCatalogue Catalogue => EmploymentCatalogue.Default;

    private DbContextScopeSource Source => new(db);

    private DbTransaction Transaction => db.Database.CurrentTransaction?.GetDbTransaction() ?? throw new InvalidOperationException("No open transaction.");

    // ================================================================== hire

    public Task<PeopleResult> HireAsync(HireRequest request, CancellationToken cancellationToken) => HireAsync(request, previous: null, cancellationToken);

    /// <summary>A hire, or a rehire linked to the person's former employment <paramref name="previous"/> (S1.20c, BR-01).</summary>
    private async Task<PeopleResult> HireAsync(HireRequest request, Guid? previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await HasAsync(EmploymentPermissions.EmploymentsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.EmployeeNo is not null && !await HasAsync(EmploymentPermissions.NumberOverride, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Errors();
        if ((request.PersonId is null) == (request.Person is null))
        {
            errors.Add("personId", PersonOrNew);
        }

        if (request.CompanyId is not { } company)
        {
            errors.Add("companyId", Required);
            return PeopleResult.Fields(errors.ToDictionary());
        }

        var today = await applier.TodayAsync(company, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var employment = new Employment
        {
            CompanyId = company,
            EmployeeNo = string.Empty,
            EmployeeType = string.Empty,
            Status = "pre_joining",
            IsPrimary = true,
            PreviousEmploymentId = previous,
        };
        if (!Catalogue.IsEmployeeType(request.EmployeeType))
        {
            errors.Add("employeeType", request.EmployeeType is null ? Required : UnknownCode);
        }
        else
        {
            employment.EmployeeType = request.EmployeeType!;
        }

        if (request.JoiningDate is not { } joining)
        {
            errors.Add("joiningDate", Required);
        }
        else
        {
            employment.JoiningDate = joining;
            employment.ServiceStartDate = request.ServiceStartDate ?? joining;
            employment.ExpectedEndDate = request.ExpectedEndDate;
            if (request.ExpectedEndDate is { } end && end < joining)
            {
                errors.Add("expectedEndDate", DateOrder);
            }

            // An earlier service start is carried over (S1.20c); a later one is a mistake (review S1.20b finding 10).
            if (request.ServiceStartDate is { } service && service > joining)
            {
                errors.Add("serviceStartDate", ServiceStartAfterJoining);
            }

            employment.Status = joining <= today ? "active" : "pre_joining";
        }

        // A rehire has its own kind and reasons; the transfer-in reason belongs to transfers only (S1.20c).
        var kind = previous is null ? "hire" : "rehire";
        var reason = request.ReasonCode ?? "new_hire";
        if (!Catalogue.IsReason(kind, reason) || reason == TransferApplier.TransferInReason)
        {
            errors.Add("reasonCode", UnknownCode);
        }

        var notes = Text(request.Notes, "notes", NotesLength, errors);
        var delta = AssignmentDelta(request.Assignment ?? new AssignmentInput(), errors, hire: true);
        if (errors.Any)
        {
            return PeopleResult.Fields(errors.ToDictionary());
        }

        Stamp(employment);
        var hire = Stamp(new EmploymentEvent
        {
            CompanyId = company,
            EmploymentId = employment.Id,
            Kind = kind,
            ReasonCode = reason,
            EffectiveDate = employment.JoiningDate,
            Notes = notes,
            Status = employment.JoiningDate <= today ? "effective" : "approved",
            Changes = JsonSerializer.SerializeToDocument(new JsonObject { ["assignment"] = delta }),
        });

        // Data scope of the new rows (before the save; the rows carry their own company, branch and unit) and field security of
        // the company (FR-KRN-IAM-13).
        employment.PersonId = request.PersonId ?? Guid.Empty;
        if (!await AllowsAsync(employment, EmploymentAccess.Scope, "create", cancellationToken).ConfigureAwait(false)
            || !await AllowsAsync(FirstRow(employment, hire, delta), EmploymentAssignmentAccess.Scope, "read", cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var denied = await NotEditableInCompanyAsync(EmploymentAccess.Fields, "create", company, ["employee_no", "employee_type", "joining_date", "service_start_date", "expected_end_date"], cancellationToken).ConfigureAwait(false);
        denied.AddRange(await NotEditableInCompanyAsync(EmploymentAssignmentAccess.Fields, "read", company, [.. delta.Select(d => d.Key)], cancellationToken).ConfigureAwait(false));
        if (denied.Count > 0)
        {
            return NotEditable(denied);
        }

        // An existing person must be one the caller may read and not retired by a merge (FR-HRM-EMP-18).
        if (request.PersonId is not null)
        {
            var existing = await db.Set<Person>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PersonId && !p.IsDeleted, cancellationToken).ConfigureAwait(false);
            if (existing is null || !await AllowsAsync(existing, PersonAccess.Scope, "read", cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["personId"] = ["errors.data.not_found"] });
            }

            if (existing.MergedIntoPersonId is not null)
            {
                return PeopleResult.Conflict(PersonRetired);
            }
        }

        // Employee number: typed by a holder of the override permission (checked against the series), else the next of the series
        // (FR-HRM-EMP-04; never reused: the company's unique key and employments are never deleted).
        try
        {
            if (request.EmployeeNo is { } typed)
            {
                var check = await numbers.CheckManualAsync(new NumberRequest("hrm.employee_no", company, null, employment.JoiningDate), typed, cancellationToken).ConfigureAwait(false);
                if (!check.Accepted)
                {
                    return PeopleResult.Fields(new Dictionary<string, string[]> { ["employeeNo"] = [check.ErrorKey!] });
                }

                employment.EmployeeNo = typed.Trim();
            }
            else
            {
                // Taken on the numbering service's own short connection before the transaction opens (gap-allowed series: a
                // refused hire skips the number), so it never waits for the locks the hire's own writes take.
                employment.EmployeeNo = await numbers.AssignOnCreateAsync(new NumberRequest("hrm.employee_no", company, null, employment.JoiningDate), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (NumberingException ex)
        {
            return PeopleResult.Invalid(ex.ErrorKey);
        }

        if (request.Person is { } newPerson)
        {
            return await persons.CreateWithEmploymentAsync(newPerson, company, async person =>
            {
                employment.PersonId = person.Id;
                return await HireCoreAsync(employment, hire, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        return await InTransactionAsync(() => HireCoreAsync(employment, hire, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves a hire inside the open transaction: the active employee limit (FR-KRN-ENT-05: a seat is taken before the employment is
    /// written, under the guard's lock), the employment with its number, the hire event, the first assignment row and the event
    /// <c>hrm.employee_hired</c>. Anything but <see cref="PeopleOutcome.Created"/> means the caller rolls back.
    /// </summary>
    private async Task<PeopleResult> HireCoreAsync(Employment employment, EmploymentEvent hire, CancellationToken ct)
    {
        try
        {
            return await HireRowsAsync(employment, hire, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }
    }

    private async Task<PeopleResult> HireRowsAsync(Employment employment, EmploymentEvent hire, CancellationToken ct)
    {
        if (await db.Set<Employment>().AnyAsync(e => e.PersonId == employment.PersonId && e.IsPrimary && CurrentStatuses.Contains(e.Status), ct).ConfigureAwait(false))
        {
            return PeopleResult.Conflict(AlreadyEmployed);
        }

        try
        {
            await limits.TakeAsync(LimitKeys.ActiveEmployees, Transaction, null, ct).ConfigureAwait(false);
        }
        catch (LimitReachedException ex)
        {
            return PeopleResult.Conflict(LimitReachedException.ErrorKey, new Dictionary<string, object?> { ["limit"] = ex.Decision.LimitKey, ["max"] = ex.Decision.Max, ["used"] = ex.Decision.Used });
        }

        db.Add(employment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Add(hire);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (await applier.SyncRowsAsync(employment, [hire], clock.UtcNow, actor.UserId, ct).ConfigureAwait(false) is { } problem)
        {
            return PeopleResult.Invalid(problem);
        }

        // The probation of the employee types the company lists (S1.20c, ruling Q10).
        await applier.OpenProbationAsync(employment, clock.UtcNow, actor.UserId, ct).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["employmentId"] = employment.Id,
            ["personId"] = employment.PersonId,
            ["companyId"] = employment.CompanyId,
            ["eventId"] = hire.Id,
            ["joiningDate"] = employment.JoiningDate,
            ["status"] = employment.Status,
        };
        if (employment.PreviousEmploymentId is { } earlier)
        {
            payload["previousEmploymentId"] = earlier; // a rehire (S1.20c)
        }

        applier.Raise(EmploymentEvents.EmployeeHired, employment, clock.UtcNow, actor.UserId, payload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Data scope after the save (the employment now locates its person).
        if (!await AllowsAsync(employment, EmploymentAccess.Scope, "read", ct).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        return new PeopleResult(PeopleOutcome.Created, Body: new
        {
            id = employment.Id,
            personId = employment.PersonId,
            companyId = employment.CompanyId,
            employeeNo = employment.EmployeeNo,
            status = employment.Status,
            eventId = hire.Id,
            rowVersion = employment.RowVersion,
        });
    }

    /// <summary>The first assignment row of a hire, for the data-scope check before anything is saved.</summary>
    private static EmploymentAssignment FirstRow(Employment employment, EmploymentEvent hire, JsonObject delta) => new()
    {
        CompanyId = employment.CompanyId,
        EmploymentId = employment.Id,
        EventId = hire.Id,
        OrgUnitId = Guid.Parse(delta[AssignmentKeys.OrgUnit]!.GetValue<string>()),
        BranchId = Guid.Parse(delta[AssignmentKeys.Branch]!.GetValue<string>()),
        Title = delta[AssignmentKeys.Title]!.GetValue<string>(),
        CostAllocation = JsonDocument.Parse("[]"),
        ValidFrom = hire.EffectiveDate,
    };

    // ================================================================== assignment input

    /// <summary>
    /// The assignment fields of a request as stored on the event (snake_case keys; a null value empties an optional field). A hire
    /// gets every required field, with defaults: cost to the org unit, FTE 1, not acting.
    /// </summary>
    private static JsonObject AssignmentDelta(AssignmentInput a, Errors errors, bool hire)
    {
        var d = new JsonObject();
        var clear = (a.Clear ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var name in clear)
        {
            if (hire || !AssignmentKeys.Clearable.Contains(Snake(name), StringComparer.Ordinal))
            {
                errors.Add("assignment." + name, CannotClear);
            }
            else
            {
                d[Snake(name)] = null;
            }
        }

        void Id(Guid? value, string key, bool required)
        {
            if (value is { } v)
            {
                d[key] = v.ToString();
            }
            else if (required && hire)
            {
                errors.Add("assignment." + Camel(key), Required);
            }
        }

        Id(a.OrgUnitId, AssignmentKeys.OrgUnit, required: true);
        Id(a.BranchId, AssignmentKeys.Branch, required: true);
        Id(a.LocationId, AssignmentKeys.Location, required: false);
        Id(a.PositionId, AssignmentKeys.Position, required: false);
        Id(a.GradeId, AssignmentKeys.Grade, required: false);
        Id(a.ManagerEmploymentId, AssignmentKeys.Manager, required: false);
        if (a.Title is not null || hire)
        {
            if (Text(a.Title, "assignment.title", TitleLength, errors) is { } title)
            {
                d[AssignmentKeys.Title] = title;
            }
            else if (!errors.Has("assignment.title"))
            {
                errors.Add("assignment.title", Required);
            }
        }

        if (a.WorkSchedule is not null)
        {
            var schedule = a.WorkSchedule.Trim();
            if (schedule.Length is 0 or > 40)
            {
                errors.Add("assignment.workSchedule", schedule.Length == 0 ? Required : TooLong);
            }

            d[AssignmentKeys.WorkSchedule] = schedule;
        }

        if (a.Fte is { } fte)
        {
            if (fte <= 0 || fte > 1)
            {
                errors.Add("assignment.fte", FteRange);
            }

            d[AssignmentKeys.Fte] = fte;
        }
        else if (hire)
        {
            d[AssignmentKeys.Fte] = 1m;
        }

        if (a.IsActing is { } acting)
        {
            d[AssignmentKeys.IsActing] = acting;
        }
        else if (hire)
        {
            d[AssignmentKeys.IsActing] = false;
        }

        if (a.CostAllocation is { } shares)
        {
            d[AssignmentKeys.CostAllocation] = CostAllocation(shares, errors);
        }
        else if (hire && a.OrgUnitId is { } unit)
        {
            d[AssignmentKeys.CostAllocation] = new JsonArray(new JsonObject { ["department"] = unit.ToString(), ["pct"] = 1m });
        }

        return d;
    }

    /// <summary>Shares in percent summing to exactly 100, each to exactly one cost centre or department; stored as fractions of 1.</summary>
    private static JsonArray CostAllocation(IReadOnlyList<CostShareInput> shares, Errors errors)
    {
        var list = new JsonArray();
        var total = 0m;
        if (shares.Count == 0)
        {
            errors.Add("assignment.costAllocation", CostShares);
        }

        foreach (var (share, i) in shares.Select((s, i) => (s, i)))
        {
            var reference = share.CostCenterRef?.Trim();
            if (string.IsNullOrEmpty(reference) == (share.Department is null) || share.Percent is not { } pct || pct <= 0 || pct > 100 || (reference?.Length ?? 0) > 100)
            {
                errors.Add($"assignment.costAllocation[{i}]", CostShares);
                continue;
            }

            total += pct;
            var item = new JsonObject();
            if (share.Department is { } unit)
            {
                item["department"] = unit.ToString();
            }
            else
            {
                item["cost_center_ref"] = reference;
            }

            item["pct"] = pct / 100m;
            list.Add(item);
        }

        if (shares.Count > 0 && total != 100m && !errors.Has("assignment.costAllocation"))
        {
            errors.Add("assignment.costAllocation", CostSum);
        }

        return list;
    }

    // ================================================================== shared helpers

    private async Task<bool> HasAsync(string permission, CancellationToken ct) => await permissions.HasPermissionAsync(permission, ct).ConfigureAwait(false);

    private async Task<bool> AllowsAsync<T>(T row, ScopeTarget<T> target, string action, CancellationToken ct)
        where T : class =>
        await scope.AllowsAsync(row, target, action, Source, ct).ConfigureAwait(false);

    /// <summary>The fields among <paramref name="keys"/> the caller may not edit on a new row of the company (FR-KRN-IAM-13).</summary>
    private async Task<List<string>> NotEditableInCompanyAsync(FieldCatalog catalog, string action, Guid companyId, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var map = await fields.ForAsync(catalog, action, companyId, ct).ConfigureAwait(false);
        return keys.Where(k => catalog.Find(k) is not null && !map.CanEdit(k)).ToList();
    }

    /// <summary>The fields among <paramref name="keys"/> the caller may not change on a stored row (roles covering the row decide).</summary>
    private async Task<List<string>> NotEditableAsync<T>(FieldCatalog catalog, ScopeTarget<T> target, T row, IEnumerable<string> keys, CancellationToken ct)
        where T : class, Erp.BuildingBlocks.Domain.Entities.IEntity
    {
        var plan = await fields.PlanAsync(catalog, target, "read", Source, ct).ConfigureAwait(false);
        var map = (await plan.ResolveAsync([row], Source, ct).ConfigureAwait(false))[0];
        return keys.Where(k => catalog.Find(k) is not null && !map.CanEdit(k)).ToList();
    }

    private static PeopleResult NotEditable(List<string> denied) =>
        new(PeopleOutcome.Invalid, FieldShaping.NotEditable, FieldErrors: denied.Distinct(StringComparer.Ordinal).ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal));

    private T Stamp<T>(T entity)
        where T : class, Erp.BuildingBlocks.Domain.Entities.IEntity
    {
        var now = clock.UtcNow;
        if (entity.Id == Guid.Empty)
        {
            entity.Id = Guid.CreateVersion7(now);
        }

        if (entity is Erp.BuildingBlocks.Domain.Entities.ICreationAudited created)
        {
            created.CreatedAt = now;
            created.CreatedBy = actor.UserId;
        }

        if (entity is Erp.BuildingBlocks.Domain.Entities.IVersioned versioned)
        {
            versioned.UpdatedAt = now;
            versioned.UpdatedBy = actor.UserId;
            versioned.RowVersion = 1;
        }

        return entity;
    }

    /// <summary>Marks the employment changed: <c>where row_version = expected</c> on save, next version, stamp.</summary>
    private void Touch(Employment employment, long expected) => PersonMerger.Touch(db, employment, expected, clock.UtcNow, actor.UserId);

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction, committing only on success; constraint refusals become answers. A refused
    /// or failed answer rolls everything back.
    /// </summary>
    private async Task<PeopleResult> InTransactionAsync(Func<Task<PeopleResult>> work, CancellationToken ct)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var result = await work().ConfigureAwait(false);
            if (result.Outcome is PeopleOutcome.Created or PeopleOutcome.Done or PeopleOutcome.Pending or PeopleOutcome.NoContent)
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
            return PeopleResult.Conflict(Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(ex.ErrorKey, ex.Details.Count == 0 ? null : new Dictionary<string, object?> { ["details"] = ex.Details });
        }
    }

    /// <summary>Database refusals of employment rows as answers next to the field they concern.</summary>
    internal static PeopleResult Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "ck_employment_assignments_manager_cycle" => Field("assignment.managerEmploymentId", ManagerCycle),
        "ck_employment_assignments_cost_allocation_sum" => Field("assignment.costAllocation", CostSum),
        "ck_employment_assignments_cost_allocation_department" => Field("assignment.costAllocation", NotInCompany),
        "fk_employment_assignments_org_unit_id" => Field("assignment.orgUnitId", NotInCompany),
        "fk_employment_assignments_branch_id" => Field("assignment.branchId", NotInCompany),
        "fk_employment_assignments_location_id" => Field("assignment.locationId", NotInCompany),
        "fk_employment_assignments_position_id" => Field("assignment.positionId", NotInCompany),
        "fk_employment_assignments_grade_id" => Field("assignment.gradeId", NotInCompany),
        "fk_employment_assignments_manager_employment_id" => Field("assignment.managerEmploymentId", NotInCompany),
        "fk_employments_company_id" => Field("companyId", "errors.data.not_found"),
        "uq_employments_company_id_employee_no" => Field("employeeNo", "errors.hrm.employment.employee_no_taken"),
        "uq_employments_person_id" => PeopleResult.Conflict(AlreadyEmployed),
        "uq_employments_company_id_user_ref" => PeopleResult.Conflict(UserLinkedElsewhere),
        _ => PersonService.Refusal(pg),
    };

    private static PeopleResult Field(string field, string key) => PeopleResult.Fields(new Dictionary<string, string[]> { [field] = [key] });

    private static string? Text(string? value, string field, int max, Errors errors)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > max)
        {
            errors.Add(field, TooLong);
        }

        return text;
    }

    private static string Snake(string camel) => string.Concat(camel.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : string.Empty) + char.ToLowerInvariant(c) : c.ToString()));

    private static string Camel(string snake)
    {
        var parts = snake.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Field errors of a request: field name (camelCase) to localization keys.</summary>
    private sealed class Errors
    {
        private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

        public bool Any => _errors.Count > 0;

        public bool Has(string field) => _errors.ContainsKey(field);

        public void Add(string field, string key)
        {
            if (!_errors.TryGetValue(field, out var list))
            {
                _errors[field] = list = [];
            }

            if (!list.Contains(key, StringComparer.Ordinal))
            {
                list.Add(key);
            }
        }

        public Dictionary<string, string[]> ToDictionary() => _errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
    }
}
