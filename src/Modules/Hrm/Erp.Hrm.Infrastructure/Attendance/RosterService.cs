using System.Globalization;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Org;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// Rosters (slice S1.28a Build items 4 and 5; FR-HRM-ATT-07). A roster is a draft for a unit (or the company) and a period. Shifts are
/// assigned to named people or to a group, which is resolved to one line per person and day when saved (ruling Q1) and stays editable;
/// the days a person's <c>shift</c> policy makes weekly off become weekly-off lines. A roster is copied forward, edited day by day,
/// checked (every finding together, <see cref="RosterChecks"/>) and published: the lines are locked for normal editing, every person on
/// it and their manager is told once, and a change after publication needs a reason and tells the people it touches again. No
/// statutory working-time limit is coded: the minimum rest is the customer's own number on the policy and is checked only when set (AS-4).
/// </summary>
internal sealed partial class RosterService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    ShiftPolicyReader policies,
    IOrgStructure orgs,
    EmploymentApplier applier) : IRosterService
{
    public const string NameRequired = "errors.hrm.roster.name_required";
    public const string PeriodRequired = "errors.hrm.roster.period_required";
    public const string PeriodOrder = "errors.hrm.roster.period_order";
    public const string PeriodTooLong = "errors.hrm.roster.period_too_long";
    public const string UnitUnknown = "errors.hrm.roster.unit_unknown";
    public const string NotDraft = "errors.hrm.roster.not_draft";
    public const string Archived = "errors.hrm.roster.archived";
    public const string ShiftRequired = "errors.hrm.roster.shift_required";
    public const string ShiftUnknown = "errors.hrm.roster.shift_unknown";
    public const string TargetRequired = "errors.hrm.roster.target_required";
    public const string PeopleUnknown = "errors.hrm.roster.people_unknown";
    public const string OutsidePeriod = "errors.hrm.roster.outside_period";
    public const string ReasonRequired = "errors.hrm.roster.reason_required";
    public const string ChangesRequired = "errors.hrm.roster.changes_required";
    public const string TooManyChanges = "errors.hrm.roster.too_many_changes";
    public const string DayTypeInvalid = "errors.hrm.roster.day_type_invalid";
    public const string ShiftNotInForce = "errors.hrm.roster.shift_not_in_force";
    public const string RemovePublished = "errors.hrm.roster.remove_published";
    public const string NothingToPublish = "errors.hrm.roster.nothing_to_publish";
    public const string ChecksFailed = "errors.hrm.roster.checks_failed";
    public const string DoubleBooked = "errors.hrm.roster.double_booked";
    public const string CustomFieldInvalid = "errors.hrm.roster.custom_field_invalid";

    /// <summary>A technical limit on one roster's length (not a working-time rule): the grid and the checks load a whole roster.</summary>
    public const int MaxPeriodDays = 100;
    public const int MaxChanges = 2000;

    private const int NameLength = 200;
    private const int ReasonLength = 500;
    private static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> CreateAsync(RosterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(ShiftPermissions.RostersCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.CompanyId is not { } company)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["companyId"] = [PersonService.Required] });
        }

        var now = clock.UtcNow;
        var row = new Roster
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = company,
            OrgUnitId = request.OrgUnitId,
            Status = "draft",
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Fill(row, request, errors);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        if (!await scope.AllowsAsync(row, RosterAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.OrgUnitId is { } unit && (await orgs.FindAsync(OrgRecordKind.OrgUnit, [unit], cancellationToken).ConfigureAwait(false)).All(r => r.CompanyId != company))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["orgUnitId"] = [UnitUnknown] });
        }

        db.Add(row);
        return await SaveAsync(row, created: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid rosterId, RosterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(ShiftPermissions.RostersUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var (row, refusal) = await LoadAsync(rosterId, "update", cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return refusal!;
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        if (row.Status != "draft")
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(NotDraft);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CompanyId is { } other && other != row.CompanyId)
        {
            errors["companyId"] = [PersonService.CannotClear];
        }

        var lines = await db.Set<RosterLine>().AsNoTracking().AnyAsync(l => l.RosterId == row.Id, cancellationToken).ConfigureAwait(false);
        if (lines && (request.PeriodStart is { } s && s != row.PeriodStart || request.PeriodEnd is { } e && e != row.PeriodEnd || request.OrgUnitId is { } u && u != row.OrgUnitId))
        {
            errors["periodStart"] = [PersonService.CannotClear]; // the period and the unit are fixed once the roster has days
        }

        Fill(row, request, errors, updating: true);
        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
        return await SaveAsync(row, created: false, cancellationToken).ConfigureAwait(false);
    }

    private static void Fill(Roster row, RosterRequest r, Dictionary<string, string[]> errors, bool updating = false)
    {
        if (!updating || r.Name is not null)
        {
            var name = r.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                errors["name"] = [NameRequired];
            }
            else if (name.Length > NameLength)
            {
                errors["name"] = [PersonService.TooLong];
            }
            else
            {
                row.Name = name;
            }
        }

        if (!updating || r.PeriodStart is not null || r.PeriodEnd is not null)
        {
            if ((r.PeriodStart ?? (updating ? row.PeriodStart : null)) is not { } start || (r.PeriodEnd ?? (updating ? row.PeriodEnd : null)) is not { } end)
            {
                errors["periodStart"] = [PeriodRequired];
            }
            else if (end < start)
            {
                errors["periodEnd"] = [PeriodOrder];
            }
            else if (end.DayNumber - start.DayNumber >= MaxPeriodDays)
            {
                errors["periodEnd"] = [PeriodTooLong];
            }
            else
            {
                row.PeriodStart = start;
                row.PeriodEnd = end;
            }
        }
    }

    /// <summary>The roster if the caller may reach it (data scope); otherwise the refusal, which is "not found" for a roster out of scope.</summary>
    private async Task<(Roster? Row, PeopleResult? Refusal)> LoadAsync(Guid id, string action, CancellationToken ct)
    {
        var row = await db.Set<Roster>().FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, RosterAccess.Scope, "read", Source, ct).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return (null, PeopleResult.NotFound);
        }

        if (action != "read" && !await scope.AllowsAsync(row, RosterAccess.Scope, action, Source, ct).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return (null, PeopleResult.Forbidden(AccessDeniedException.OutOfScope));
        }

        return (row, null);
    }

    private async Task<PeopleResult> SaveAsync(Roster row, bool created, CancellationToken ct)
    {
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
            return Refusal(pg);
        }

        return new PeopleResult(created ? PeopleOutcome.Created : PeopleOutcome.Done, Body: View(row));
    }

    internal static Dictionary<string, object?> View(Roster r) => new(StringComparer.Ordinal)
    {
        ["id"] = r.Id,
        ["companyId"] = r.CompanyId,
        ["name"] = r.Name,
        ["orgUnitId"] = r.OrgUnitId,
        ["periodStart"] = r.PeriodStart,
        ["periodEnd"] = r.PeriodEnd,
        ["status"] = r.Status,
        ["publishedAt"] = r.PublishedAt,
        ["publishedBy"] = r.PublishedBy,
        ["copiedFromRosterId"] = r.CopiedFromRosterId,
        ["rowVersion"] = r.RowVersion,
    };

    internal static PeopleResult Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "fk_rosters_company_id" => Field("companyId", "errors.data.not_found"),
        "fk_rosters_org_unit_id" => Field("orgUnitId", UnitUnknown),
        "ck_rosters_period" => Field("periodEnd", PeriodOrder),
        "uq_roster_lines_employment_id_work_date" => PeopleResult.Conflict(DoubleBooked),
        "ck_roster_lines_published" => PeopleResult.Invalid(ReasonRequired),
        _ => PersonService.Refusal(pg),
    };

    private static PeopleResult Field(string field, string key) => PeopleResult.Fields(new Dictionary<string, string[]> { [field] = [key] });

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The version of a shift (named by any of its versions) that is in force on a date, or null.</summary>
    private sealed class ShiftVersions(IReadOnlyList<Shift> versions)
    {
        public Shift? On(DateOnly date) => versions.FirstOrDefault(v => (v.ValidFrom ?? DateOnly.MinValue) <= date && (v.ValidTo ?? DateOnly.MaxValue) >= date);

        public IReadOnlyList<Shift> All => versions;
    }

    private async Task<ShiftVersions?> VersionsOfAsync(Guid shiftId, Guid company, CancellationToken ct)
    {
        var shift = await db.Set<Shift>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == shiftId && s.CompanyId == company && !s.IsDeleted, ct).ConfigureAwait(false);
        if (shift is null)
        {
            return null;
        }

        // Codes are unique case-insensitively (index on lower(code)); LIKE wildcards in the code (the underscore) are escaped.
        var pattern = shift.Code.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal);
        var all = await db.Set<Shift>().AsNoTracking().Where(s => s.CompanyId == company && !s.IsDeleted && EF.Functions.ILike(s.Code, pattern, "\\")).ToListAsync(ct).ConfigureAwait(false);
        return new ShiftVersions(all);
    }
}
