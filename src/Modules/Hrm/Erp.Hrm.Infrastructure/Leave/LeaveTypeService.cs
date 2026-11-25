using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Leave;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// Leave types (slice S1.32 Build item 1, FR-HRM-LVE-01): code, names in English and Urdu, paid or unpaid, counting (calendar days, working
/// days or hours), unit (day, half day, hour), document requirement, comp-off and unpaid-deduction flags; source <c>tenant</c> (pack types
/// arrive through the pack installer, S1.32b). Retired, never deleted. Counting in hours goes with the unit hour, a paid type is never a pay
/// deduction, and code, counting and unit are fixed once the type has ledger entries (the database repeats these rules). How a type is
/// granted and every limit live in the <c>leave</c> policy, never here, and no type or number is shipped (AS-4, R-145).
/// </summary>
internal sealed partial class LeaveTypeService(HrmDbContext db, IClock clock, ICurrentActor actor, IPermissionChecker permissions, IDataScopeFilter scope) : ILeaveTypeService
{
    public const string CodeRequired = "errors.hrm.leave.code_required";
    public const string CodeTaken = "errors.hrm.leave.code_taken";
    public const string NameRequired = "errors.hrm.leave.name_required";
    public const string CountingInvalid = "errors.hrm.leave.counting";
    public const string UnitInvalid = "errors.hrm.leave.unit";
    public const string ColorInvalid = "errors.hrm.leave.color_token";
    public static readonly string[] ColorTokens = ["primary", "success", "warning", "danger", "info", "neutral"];
    public const string HoursGoTogether = "errors.hrm.leave.hours_unit";
    public const string PaidDeduction = "errors.hrm.leave.paid_deduction";
    public const string FixedOnceUsed = "errors.hrm.leave.type_fixed";

    public static readonly string[] Countings = ["calendar_days", "working_days", "hours"];
    public static readonly string[] Units = ["day", "half_day", "hour"];

    private const int CodeLength = 40;
    private const int NameLength = 200;

    private DbContextScopeSource Source => new(db);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex CodePattern();

    public async Task<PeopleResult> CreateAsync(LeaveTypeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LeavePermissions.TypesCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.CompanyId is not { } company)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["companyId"] = [PersonService.Required] });
        }

        var now = clock.UtcNow;
        var row = new LeaveType
        {
            Id = Guid.CreateVersion7(now), CompanyId = company, NameI18n = JsonDocument.Parse("{}"), Source = "tenant", IsActive = request.IsActive ?? true,
            CreatedAt = now, CreatedBy = actor.UserId, UpdatedAt = now, UpdatedBy = actor.UserId, RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, LeaveTypeAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (!await db.Database.SqlQuery<bool>($"select exists (select 1 from org.companies where id = {company} and not is_deleted) as \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["companyId"] = ["errors.data.not_found"] });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Apply(row, request, creating: true, errors);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        db.Add(row);
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid leaveTypeId, LeaveTypeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LeavePermissions.TypesUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<LeaveType>().FirstOrDefaultAsync(t => t.Id == leaveTypeId && !t.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, LeaveTypeAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, LeaveTypeAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CompanyId is { } other && other != row.CompanyId)
        {
            errors["companyId"] = [PersonService.CannotClear];
        }

        var (code, counting, unit) = (row.Code, row.Counting, row.Unit);
        Apply(row, request, creating: false, errors);
        if ((!string.Equals(row.Code, code, StringComparison.OrdinalIgnoreCase) || row.Counting != counting || row.Unit != unit)
            && await db.Set<LeaveLedger>().AsNoTracking().AnyAsync(l => l.LeaveTypeId == row.Id, cancellationToken).ConfigureAwait(false))
        {
            errors[!string.Equals(row.Code, code, StringComparison.OrdinalIgnoreCase) ? "code" : row.Counting != counting ? "counting" : "unit"] = [FixedOnceUsed];
        }

        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies the request into the row, collecting field errors (the rules between fields are checked on the result).</summary>
    private static void Apply(LeaveType row, LeaveTypeRequest r, bool creating, Dictionary<string, string[]> errors)
    {
        void Fail(string field, string key) => errors[field] = [.. errors.GetValueOrDefault(field, []).Append(key).Distinct(StringComparer.Ordinal)];
        if (creating || r.Code is not null)
        {
            var code = r.Code?.Trim();
            if (string.IsNullOrEmpty(code) || !CodePattern().IsMatch(code))
            {
                Fail("code", CodeRequired);
            }
            else if (code.Length > CodeLength)
            {
                Fail("code", PersonService.TooLong);
            }
            else
            {
                row.Code = code;
            }
        }

        if (creating || r.NameI18n is not null)
        {
            var names = (r.NameI18n ?? []).Select(n => (Key: n.Key.Trim().ToLowerInvariant(), Value: n.Value?.Trim() ?? string.Empty)).Where(n => n.Value.Length > 0)
                .DistinctBy(n => n.Key).ToDictionary(n => n.Key, n => n.Value, StringComparer.Ordinal);
            if (!names.ContainsKey("en") || !names.ContainsKey("ur") || names.Keys.Any(k => k.Length is < 2 or > 5))
            {
                Fail("nameI18n", NameRequired);
            }
            else if (names.Values.Any(v => v.Length > NameLength))
            {
                Fail("nameI18n", PersonService.TooLong);
            }
            else
            {
                row.NameI18n = JsonSerializer.SerializeToDocument(names);
            }
        }

        Choice(r.Counting, Countings, "counting", CountingInvalid, v => row.Counting = v);
        Choice(r.Unit, Units, "unit", UnitInvalid, v => row.Unit = v);
        if (r.ColorToken is { } colour)
        {
            if (colour.Length == 0)
            {
                row.ColorToken = null;
            }
            else if (ColorTokens.Contains(colour, StringComparer.Ordinal))
            {
                row.ColorToken = colour;
            }
            else
            {
                Fail("colorToken", ColorInvalid);
            }
        }

        Flag(r.IsPaid, "isPaid", v => row.IsPaid = v);
        Flag(r.RequiresDocument, "requiresDocument", v => row.RequiresDocument = v);
        Flag(r.IsCompOff, "isCompOff", v => row.IsCompOff = v);
        Flag(r.IsUnpaidDeduction, "isUnpaidDeduction", v => row.IsUnpaidDeduction = v);
        if (r.IsActive is { } active)
        {
            row.IsActive = active;
        }

        if (errors.Count == 0)
        {
            if ((row.Counting == "hours") != (row.Unit == "hour"))
            {
                Fail(row.Counting == "hours" ? "unit" : "counting", HoursGoTogether);
            }

            if (row.IsPaid && row.IsUnpaidDeduction)
            {
                Fail("isUnpaidDeduction", PaidDeduction);
            }
        }

        void Choice(string? value, string[] allowed, string field, string key, Action<string> assign)
        {
            if (creating || value is not null)
            {
                if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
                {
                    Fail(field, key);
                }
                else
                {
                    assign(value);
                }
            }
        }

        void Flag(bool? value, string field, Action<bool> assign)
        {
            if (value is { } v)
            {
                assign(v);
            }
            else if (creating)
            {
                Fail(field, PersonService.Required);
            }
        }
    }

    private async Task<PeopleResult> SaveAsync(LeaveType row, CancellationToken ct)
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["code"] = [CodeTaken] });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "ck_leave_types_rules" })
        {
            // A ledger entry arrived between the check and the save (the database repeats the rule).
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["code"] = [FixedOnceUsed] });
        }

        return new PeopleResult(created ? PeopleOutcome.Created : PeopleOutcome.Done, Body: View(row));
    }

    /// <summary>The saved type as the generated read shows it (the engine keeps this row as the form's starting point).</summary>
    public static Dictionary<string, object?> View(LeaveType r) => new(StringComparer.Ordinal)
    {
        ["id"] = r.Id,
        ["companyId"] = r.CompanyId,
        ["code"] = r.Code,
        ["nameI18n"] = r.NameI18n.RootElement.Clone(),
        ["isPaid"] = r.IsPaid,
        ["counting"] = r.Counting,
        ["unit"] = r.Unit,
        ["requiresDocument"] = r.RequiresDocument,
        ["isCompOff"] = r.IsCompOff,
        ["isUnpaidDeduction"] = r.IsUnpaidDeduction,
        ["source"] = r.Source,
        ["colorToken"] = r.ColorToken,
        ["isActive"] = r.IsActive,
        ["createdAt"] = r.CreatedAt,
        ["updatedAt"] = r.UpdatedAt,
        ["rowVersion"] = r.RowVersion,
    };
}
