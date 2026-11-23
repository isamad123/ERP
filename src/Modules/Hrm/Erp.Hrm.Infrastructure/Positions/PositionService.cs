using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Positions;
using Erp.Hrm.Contracts.Positions;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>
/// The position services (slice S1.22a Build items 1 and 3; FR-HRM-POS-01, -02, -03): create a position, change it from a date,
/// freeze or reopen it, close it, and read who holds it. The generated writes of <c>hrm.positions</c> are closed (a change adds a row,
/// so the generated update would rewrite history). A position is its code: its rows are the versions, the one in force on a day is
/// the position on that day. Every write checks the permission and the data scope before and after.
/// </summary>
internal sealed class PositionService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    ISettings settings,
    IFieldAccess fields,
    ICustomFieldValidator customFields,
    Employments.EmploymentApplier applier) : IPositionService
{
    public const string CodeTaken = "errors.hrm.position.code_taken";
    public const string NotLatest = "errors.hrm.position.not_latest";
    public const string IsClosed = "errors.hrm.position.closed";
    public const string HasHolders = "errors.hrm.position.has_holders";
    public const string NothingChanges = "errors.hrm.position.nothing_changes";
    public const string BadStatus = "errors.hrm.position.bad_status";
    public const string DateAfterStart = "errors.hrm.position.date_after_start";
    public const string NotInCompany = "errors.hrm.position.not_in_company";
    public const string ReportingCycle = "errors.hrm.position.reporting_cycle";
    public const string NegativeFte = "errors.hrm.position.negative_fte";

    private const int CodeLength = 40;
    private const int TitleLength = 200;
    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];
    private static readonly string[] Clearable = ["gradeId", "reportsToPositionId", "locationId", "costCenterRef"];

    private DbContextScopeSource Source => new(db);

    // ================================================================== create

    public async Task<PeopleResult> CreateAsync(PositionCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PositionPermissions.PositionsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var code = request.PositionCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            errors["positionCode"] = [PersonService.Required];
        }
        else if (code.Length > CodeLength)
        {
            errors["positionCode"] = [PersonService.TooLong];
        }

        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            errors["title"] = [PersonService.Required];
        }
        else if (title.Length > TitleLength)
        {
            errors["title"] = [PersonService.TooLong];
        }

        if (request.CompanyId is null)
        {
            errors["companyId"] = [PersonService.Required];
        }

        if (request.OrgUnitId is null)
        {
            errors["orgUnitId"] = [PersonService.Required];
        }

        if (request.BudgetedFte is < 0)
        {
            errors["budgetedFte"] = [NegativeFte];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        var company = request.CompanyId!.Value;
        var row = new Position
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = company,
            PositionCode = code!,
            Title = title!,
            OrgUnitId = request.OrgUnitId!.Value,
            GradeId = request.GradeId,
            ReportsToPositionId = request.ReportsToPositionId,
            LocationId = request.LocationId,
            CostCenterRef = request.CostCenterRef,
            BudgetedFte = request.BudgetedFte ?? 1m,
            Status = PositionRules.Open,
            ValidFrom = request.ValidFrom ?? await TodayAsync(company, now, cancellationToken).ConfigureAwait(false),
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, PositionAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        // Custom values (S1.15b, FR-KRN-MET-02): field security first, then the active definitions; the check holds the locks of
        // unique fields until the position is saved.
        await using var customCheck = await CustomAsync(company, null, request.Custom, null, update: false, cancellationToken).ConfigureAwait(false);
        if (customCheck.Refusal is not null)
        {
            return customCheck.Refusal;
        }

        row.Custom = customCheck.Check!.Values;
        return await InTransactionAsync(async () =>
        {
            db.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Created, Body: Body(row));
        }, cancellationToken).ConfigureAwait(false);
    }

    // ================================================================== change from a date

    public async Task<PeopleResult> ChangeAsync(Guid positionId, PositionChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PositionPermissions.PositionsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is null)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        return await InTransactionAsync(async () =>
        {
            var old = await LockedAsync(positionId, cancellationToken).ConfigureAwait(false);
            if (old is null || !await scope.AllowsAsync(old, PositionAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.NotFound;
            }

            if (old.RowVersion != request.RowVersion)
            {
                return PeopleResult.Conflict(PersonService.Concurrency);
            }

            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (request.EffectiveDate is null)
            {
                errors["effectiveDate"] = [PersonService.Required];
            }

            var clear = (request.Clear ?? []).ToHashSet(StringComparer.Ordinal);
            foreach (var name in clear.Where(n => !Clearable.Contains(n)))
            {
                errors[name] = [PersonService.CannotClear];
            }

            var title = request.Title?.Trim();
            if (title is not null && (title.Length == 0 || title.Length > TitleLength))
            {
                errors["title"] = [title.Length == 0 ? PersonService.Required : PersonService.TooLong];
            }

            if (request.BudgetedFte is < 0)
            {
                errors["budgetedFte"] = [NegativeFte];
            }

            if (request.Status is not null && request.Status is not (PositionRules.Open or PositionRules.Frozen))
            {
                errors["status"] = [BadStatus];
            }

            if (errors.Count > 0)
            {
                return PeopleResult.Fields(errors);
            }

            if (old.Status == PositionRules.Closed || old.ValidTo is not null)
            {
                return PeopleResult.Conflict(old.Status == PositionRules.Closed ? IsClosed : NotLatest);
            }

            if (await db.Set<Position>().AnyAsync(p => p.CompanyId == old.CompanyId && p.PositionCodeLower == old.PositionCodeLower && p.ValidFrom > old.ValidFrom, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Conflict(NotLatest);
            }

            var date = request.EffectiveDate!.Value;
            if (date <= old.ValidFrom)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["effectiveDate"] = [DateAfterStart] });
            }

            await using var customCheck = await CustomAsync(old.CompanyId, old.Id, request.Custom, old.Custom, update: true, cancellationToken).ConfigureAwait(false);
            if (customCheck.Refusal is not null)
            {
                return customCheck.Refusal;
            }

            var next = new Position
            {
                Id = Guid.CreateVersion7(clock.UtcNow),
                CompanyId = old.CompanyId,
                PositionCode = old.PositionCode,
                Title = title ?? old.Title,
                OrgUnitId = request.OrgUnitId ?? old.OrgUnitId,
                GradeId = clear.Contains("gradeId") ? null : request.GradeId ?? old.GradeId,
                ReportsToPositionId = clear.Contains("reportsToPositionId") ? null : request.ReportsToPositionId ?? old.ReportsToPositionId,
                LocationId = clear.Contains("locationId") ? null : request.LocationId ?? old.LocationId,
                CostCenterRef = clear.Contains("costCenterRef") ? null : request.CostCenterRef ?? old.CostCenterRef,
                BudgetedFte = request.BudgetedFte ?? old.BudgetedFte,
                Status = request.Status ?? old.Status,
                ValidFrom = date,
                Custom = customCheck.Check!.Values,
                CreatedAt = clock.UtcNow,
                CreatedBy = actor.UserId,
                UpdatedAt = clock.UtcNow,
                UpdatedBy = actor.UserId,
                RowVersion = 1,
            };
            if (Same(old, next))
            {
                return PeopleResult.Invalid(NothingChanges);
            }

            if (!await scope.AllowsAsync(next, PositionAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
            }

            // The old row ends first, so the exclusion constraint never sees two rows on one day.
            PersonMerger.Touch(db, old, request.RowVersion.Value, clock.UtcNow, actor.UserId);
            old.ValidTo = date;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.Add(next);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Created, Body: Body(next));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool Same(Position a, Position b) =>
        (a.Title, a.OrgUnitId, a.GradeId, a.ReportsToPositionId, a.LocationId, a.CostCenterRef, a.BudgetedFte, a.Status)
        == (b.Title, b.OrgUnitId, b.GradeId, b.ReportsToPositionId, b.LocationId, b.CostCenterRef, b.BudgetedFte, b.Status);

    // ================================================================== close

    public async Task<PeopleResult> CloseAsync(Guid positionId, PositionCloseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PositionPermissions.PositionsClose, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is null)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        if (request.EffectiveDate is null)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["effectiveDate"] = [PersonService.Required] });
        }

        return await InTransactionAsync(async () =>
        {
            var row = await LockedAsync(positionId, cancellationToken).ConfigureAwait(false);
            if (row is null || !await scope.AllowsAsync(row, PositionAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.NotFound;
            }

            if (!await scope.AllowsAsync(row, PositionAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
            }

            if (row.RowVersion != request.RowVersion)
            {
                return PeopleResult.Conflict(PersonService.Concurrency);
            }

            if (row.Status == PositionRules.Closed || row.ValidTo is not null)
            {
                return PeopleResult.Conflict(row.Status == PositionRules.Closed ? IsClosed : NotLatest);
            }

            var date = request.EffectiveDate.Value;
            if (date <= row.ValidFrom)
            {
                return PeopleResult.Fields(new Dictionary<string, string[]> { ["effectiveDate"] = [DateAfterStart] });
            }

            if (await db.Set<Position>().AnyAsync(p => p.CompanyId == row.CompanyId && p.PositionCodeLower == row.PositionCodeLower && p.ValidFrom > row.ValidFrom, cancellationToken).ConfigureAwait(false))
            {
                return PeopleResult.Conflict(NotLatest);
            }

            // Nobody may still hold the position from the closing day on (Q6: closing is refused while people are in it).
            var ids = await IdsOfCodeAsync(row.CompanyId, row.PositionCodeLower, cancellationToken).ConfigureAwait(false);
            var holders = await db.Set<EmploymentAssignment>().AsNoTracking()
                .CountAsync(a => a.CompanyId == row.CompanyId && a.PositionId != null && ids.Contains(a.PositionId.Value) && (a.ValidTo == null || a.ValidTo > date), cancellationToken).ConfigureAwait(false);
            if (holders > 0)
            {
                return PeopleResult.Conflict(HasHolders, new Dictionary<string, object?> { ["holders"] = holders });
            }

            PersonMerger.Touch(db, row, request.RowVersion.Value, clock.UtcNow, actor.UserId);
            row.ValidTo = date;
            row.Status = PositionRules.Closed;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Done, Body: Body(row));
        }, cancellationToken).ConfigureAwait(false);
    }

    // ================================================================== holders

    public async Task<PeopleResult> HoldersAsync(Guid positionId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PositionPermissions.PositionsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var chosen = await db.Set<Position>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == positionId, cancellationToken).ConfigureAwait(false);
        if (chosen is null || !await scope.AllowsAsync(chosen, PositionAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var rows = await db.Set<Position>().AsNoTracking().Where(p => p.CompanyId == chosen.CompanyId && p.PositionCodeLower == chosen.PositionCodeLower).OrderBy(p => p.ValidFrom).ToListAsync(cancellationToken).ConfigureAwait(false);
        var day = asOf ?? await TodayAsync(chosen.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var row = PositionRules.InForce(rows, day) ?? (day < rows[0].ValidFrom ? rows[0] : rows[^1]);
        var ids = rows.Select(r => r.Id).ToList();
        var held = await (from a in db.Set<EmploymentAssignment>().AsNoTracking()
                          join e in db.Set<Employment>().AsNoTracking() on a.EmploymentId equals e.Id
                          where a.CompanyId == chosen.CompanyId && a.PositionId != null && ids.Contains(a.PositionId.Value)
                                && a.ValidFrom <= day && (a.ValidTo == null || a.ValidTo > day)
                                && CurrentStatuses.Contains(e.Status) && e.JoiningDate <= day && (e.SeparationDate == null || e.SeparationDate >= day)
                          orderby e.EmployeeNo
                          select new { Assignment = a, e.EmployeeNo }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var filled = held.Sum(h => h.Assignment.Fte);
        var visible = new List<object>();
        foreach (var h in held)
        {
            if (await scope.AllowsAsync(h.Assignment, EmploymentAssignmentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            {
                visible.Add(new { employmentId = h.Assignment.EmploymentId, employeeNo = h.EmployeeNo, fte = h.Assignment.Fte, isActing = h.Assignment.IsActing });
            }
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            position = Body(row),
            asOf = day,
            status = PositionRules.EffectiveStatus(row.Status, row.ValidFrom, day, row.BudgetedFte, filled),
            budgetedFte = row.BudgetedFte,
            filledFte = filled,
            vacantFte = Math.Max(0m, row.BudgetedFte - filled),
            overFte = Math.Max(0m, filled - row.BudgetedFte),
            holders = visible,
        });
    }

    // ================================================================== planning model

    public async Task<PeopleResult> OrgModelsAsync(CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PositionPermissions.PositionsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var companies = await db.Database.SqlQuery<Guid>($"select id as \"Value\" from org.companies where not is_deleted order by created_at, id").ToListAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<object>();
        foreach (var company in companies)
        {
            if (!await scope.AllowsAsync(new Position { CompanyId = company }, PositionAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            list.Add(await OrgModelGuard.DescribeAsync(db, settings, company, await TodayAsync(company, clock.UtcNow, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
        }

        return new PeopleResult(PeopleOutcome.Done, Body: list);
    }

    // ================================================================== helpers

    /// <summary>The custom values of a write checked (S1.15b, R-217): the outcome holds either the refusal or the check to dispose after the save.</summary>
    private async Task<CustomOutcome> CustomAsync(Guid company, Guid? rowId, System.Text.Json.JsonElement? requested, System.Text.Json.JsonDocument? stored, bool update, CancellationToken ct)
    {
        var access = await fields.ForAsync(PositionAccess.Fields, update ? "update" : "create", company, ct).ConfigureAwait(false);
        var denied = new List<string>();
        var merged = FieldShaping.MergeCustom(requested, stored, access, denied);
        if (denied.Count > 0)
        {
            return new(null, new PeopleResult(PeopleOutcome.Invalid, FieldShaping.NotEditable,
                FieldErrors: denied.Distinct(StringComparer.Ordinal).ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal)));
        }

        var check = await customFields.CheckAsync(new CustomWrite("hrm.positions", rowId, company, merged, stored, key => access.CanEdit(FieldAccessMap.CustomPrefix + key), update), ct).ConfigureAwait(false);
        if (check.Errors.Count > 0)
        {
            var refusal = PeopleResult.Fields(check.Errors.ToDictionary(e => e.Key, e => new[] { e.Value }, StringComparer.Ordinal));
            await check.DisposeAsync().ConfigureAwait(false);
            return new(null, refusal);
        }

        return new(check, null);
    }

    private sealed class CustomOutcome(CustomCheck? check, PeopleResult? refusal) : IAsyncDisposable
    {
        public CustomCheck? Check { get; } = check;

        public PeopleResult? Refusal { get; } = refusal;

        public ValueTask DisposeAsync() => Check?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private static object Body(Position p) => new
    {
        id = p.Id,
        companyId = p.CompanyId,
        positionCode = p.PositionCode,
        title = p.Title,
        orgUnitId = p.OrgUnitId,
        gradeId = p.GradeId,
        reportsToPositionId = p.ReportsToPositionId,
        locationId = p.LocationId,
        costCenterRef = p.CostCenterRef,
        budgetedFte = p.BudgetedFte,
        status = p.Status,
        validFrom = p.ValidFrom,
        validTo = p.ValidTo,
        rowVersion = p.RowVersion,
    };

    /// <summary>The row, locked for the rest of the transaction (two changes of one position never plan from the same row).</summary>
    private async Task<Position?> LockedAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"select 1 from hrm.positions where id = {id} for update", ct).ConfigureAwait(false);
        return await db.Set<Position>().FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
    }

    private Task<List<Guid>> IdsOfCodeAsync(Guid company, string codeLower, CancellationToken ct) =>
        db.Set<Position>().AsNoTracking().Where(p => p.CompanyId == company && p.PositionCodeLower == codeLower).Select(p => p.Id).ToListAsync(ct);

    private Task<DateOnly> TodayAsync(Guid companyId, DateTimeOffset now, CancellationToken ct) => applier.TodayAsync(companyId, now, ct);

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
            return Refusal(pg);
        }
    }

    /// <summary>Database refusals of position rows as answers next to the field they concern.</summary>
    internal static PeopleResult Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "ex_positions_company_id_position_code_lower" => Field("positionCode", CodeTaken),
        "ck_positions_reporting_cycle" => Field("reportsToPositionId", ReportingCycle),
        "ck_positions_budgeted_fte" => Field("budgetedFte", NegativeFte),
        "fk_positions_org_unit_id" => Field("orgUnitId", NotInCompany),
        "fk_positions_grade_id" => Field("gradeId", NotInCompany),
        "fk_positions_reports_to_position_id" => Field("reportsToPositionId", NotInCompany),
        "fk_positions_location_id" => Field("locationId", NotInCompany),
        "fk_positions_company_id" => Field("companyId", "errors.data.not_found"),
        _ => PersonService.Refusal(pg),
    };

    private static PeopleResult Field(string field, string key) => PeopleResult.Fields(new Dictionary<string, string[]> { [field] = [key] });
}
