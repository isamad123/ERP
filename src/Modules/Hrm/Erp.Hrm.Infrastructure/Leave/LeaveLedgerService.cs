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
using Erp.Kernel.Contracts.Attendance;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// The leave ledger and balances (slice S1.32 Build items 4 and 5; FR-HRM-LVE-10, FR-XM-05 and -06). HR posts opening balances, grants and
/// adjustments, dated and with a reason, and reverses an entry by a new entry of the opposite quantity; nothing is ever edited. Each entry names
/// its source and the version of the <c>leave</c> policy in force on its date. A negative entry is refused when the balance would fall below
/// zero, or below the negative limit the policy allows, on its date or later in the leave year (one step under the employment and type lock,
/// ruling Q8). A date in a locked attendance period is refused (FR-XM-26). Balances are sums of the leave year's entries up to a date.
/// </summary>
internal sealed class LeaveLedgerService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EmploymentApplier applier,
    LeavePolicyReader policies,
    LeaveYearSetting years,
    IAttendancePeriodLock locks) : ILeaveLedgerService
{
    public const string TypeUnknown = "errors.hrm.leave.type_unknown";
    public const string TypeRetired = "errors.hrm.leave.type_retired";
    public const string KindManual = "errors.hrm.leave.kind_manual";
    public const string QuantityInvalid = "errors.hrm.leave.quantity";
    public const string BeforeJoining = "errors.hrm.leave.before_joining";
    public const string AfterLeaving = "errors.hrm.leave.after_leaving";
    public const string ExpiryOrder = "errors.hrm.leave.expiry_order";
    public const string ReasonRequired = "errors.hrm.leave.reason_required";
    public const string EmploymentCancelled = "errors.hrm.leave.employment_cancelled";
    public const string PeriodLocked = "errors.hrm.leave.period_locked";
    public const string OpeningExists = "errors.hrm.leave.opening_exists";
    public const string BelowAllowed = "errors.hrm.leave.below_allowed";
    public const string NotReversible = "errors.hrm.leave.not_reversible";
    public const string AlreadyReversed = "errors.hrm.leave.already_reversed";
    public const string ReversalDate = "errors.hrm.leave.reversal_date";

    /// <summary>The answer when no leave policy covers a type for the employee (S1.23 ruling): requests are refused with it (S1.32b).</summary>
    public const string NoPolicy = "errors.hrm.leave.no_policy";

    /// <summary>The data-scope action of posting and reversing: the permission's action (<c>hrm.leave_ledger.post</c>).</summary>
    private const string PostAction = "post";

    private const decimal Largest = 9999m;
    private const int ReasonLength = 500;
    private static readonly string[] OwnStatuses = ["pre_joining", "active", "on_notice"];

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> PostAsync(Guid employmentId, LeavePostRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LeavePermissions.LedgerPost, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        // The scope action names the permission (as for pay periods, CR-087 S1.32a detail i): an employment outside the caller's scope for posting does not exist for them.
        var e = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (e is null || !await InScopeAsync(e, PostAction, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (e.Status == "cancelled")
        {
            return PeopleResult.Conflict(EmploymentCancelled);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var type = request.LeaveTypeId is { } typeId
            ? await db.Set<LeaveType>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId && t.CompanyId == e.CompanyId && !t.IsDeleted, cancellationToken).ConfigureAwait(false)
            : null;
        if (request.LeaveTypeId is null)
        {
            errors["leaveTypeId"] = [PersonService.Required];
        }
        else if (type is null)
        {
            errors["leaveTypeId"] = [TypeUnknown];
        }
        else if (!type.IsActive)
        {
            errors["leaveTypeId"] = [TypeRetired];
        }

        if (request.Kind is null || !LeaveLedgerStore.ManualKinds.Contains(request.Kind, StringComparer.Ordinal))
        {
            errors["kind"] = [KindManual];
        }

        if (request.Quantity is not { } quantity || quantity == 0m || decimal.Round(quantity, EntitlementRules.Places) != quantity || Math.Abs(quantity) > Largest)
        {
            errors["quantity"] = [QuantityInvalid];
            quantity = 0m;
        }

        if (request.EntryDate is not { } date)
        {
            errors["entryDate"] = [PersonService.Required];
            date = default;
        }
        else if (date < e.JoiningDate)
        {
            errors["entryDate"] = [BeforeJoining];
        }
        else if (e.SeparationDate is { } last && date > last)
        {
            errors["entryDate"] = [AfterLeaving];
        }

        if (request.ExpiresOn is { } expires && request.EntryDate is { } on && expires <= on)
        {
            errors["expiresOn"] = [ExpiryOrder];
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            errors["reason"] = [ReasonRequired];
        }
        else if (reason.Length > ReasonLength)
        {
            errors["reason"] = [PersonService.TooLong];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        if (await locks.IsLockedAsync(e.Id, date, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["entryDate"] = [PeriodLocked] });
        }

        var policy = await policies.AtAsync(e.Id, date, cancellationToken).ConfigureAwait(false);
        var rules = policy?.For(type!.Code);
        var year = await years.YearAsync(e.CompanyId, rules, date, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var row = new LeaveLedger
        {
            Id = Guid.CreateVersion7(now), CompanyId = e.CompanyId, EmploymentId = e.Id, LeaveTypeId = type!.Id, LeaveYear = year.Year, EntryDate = date, Kind = request.Kind!,
            Quantity = quantity, ExpiresOn = request.ExpiresOn, SourceType = LeaveLedgerStore.Manual, PolicyVersionId = policy?.Resolution.Policy?.VersionId,
            Reason = reason, CreatedAt = now, CreatedBy = actor.UserId,
        };
        return await WriteAsync(row, rules, async ct =>
            request.Kind == "opening"
            && await db.Set<LeaveLedger>().AsNoTracking().AnyAsync(x => x.EmploymentId == e.Id && x.LeaveTypeId == type.Id && x.Kind == "opening" && x.ReversesEntryId == null
                && !db.Set<LeaveLedger>().Any(r => r.ReversesEntryId == x.Id), ct).ConfigureAwait(false)
                ? PeopleResult.Fields(new Dictionary<string, string[]> { ["kind"] = [OpeningExists] })
                : null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> ReverseAsync(Guid entryId, LeaveReverseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(LeavePermissions.LedgerPost, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var entry = await db.Set<LeaveLedger>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == entryId, cancellationToken).ConfigureAwait(false);
        if (entry is null || !await scope.AllowsAsync(entry, LeaveLedgerAccess.Scope, PostAction, Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (entry.ReversesEntryId is not null || !LeaveLedgerStore.ReversibleKinds.Contains(entry.Kind, StringComparer.Ordinal))
        {
            return PeopleResult.Conflict(NotReversible);
        }

        if (await db.Set<LeaveLedger>().AsNoTracking().AnyAsync(x => x.ReversesEntryId == entry.Id, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Conflict(AlreadyReversed);
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > ReasonLength)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["reason"] = [string.IsNullOrEmpty(reason) ? ReasonRequired : PersonService.TooLong] });
        }

        var type = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == entry.LeaveTypeId, cancellationToken).ConfigureAwait(false);
        var rules = (await policies.AtAsync(entry.EmploymentId, entry.EntryDate, cancellationToken).ConfigureAwait(false))?.For(type.Code);
        var span = await years.YearAsync(entry.CompanyId, rules, entry.EntryDate, cancellationToken).ConfigureAwait(false);
        span = span.Year == entry.LeaveYear ? span : LeaveYears.For(entry.LeaveYear, span.Start.Month);
        var date = request.EntryDate ?? entry.EntryDate;
        if (date < entry.EntryDate || date > span.End)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["entryDate"] = [ReversalDate] });
        }

        if (await locks.IsLockedAsync(entry.EmploymentId, date, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["entryDate"] = [PeriodLocked] });
        }

        var dateRules = date == entry.EntryDate ? rules : (await policies.AtAsync(entry.EmploymentId, date, cancellationToken).ConfigureAwait(false))?.For(type.Code);
        var now = clock.UtcNow;
        var row = new LeaveLedger
        {
            Id = Guid.CreateVersion7(now), CompanyId = entry.CompanyId, EmploymentId = entry.EmploymentId, LeaveTypeId = entry.LeaveTypeId, LeaveYear = entry.LeaveYear,
            EntryDate = date, Kind = entry.Kind, Quantity = -entry.Quantity, SourceType = LeaveLedgerStore.Reversal, SourceId = entry.Id, PolicyVersionId = entry.PolicyVersionId,
            ReversesEntryId = entry.Id, Reason = reason, CreatedAt = now, CreatedBy = actor.UserId,
        };
        return await WriteAsync(row, dateRules, async ct =>
            await db.Set<LeaveLedger>().AsNoTracking().AnyAsync(x => x.ReversesEntryId == entry.Id, ct).ConfigureAwait(false) ? PeopleResult.Conflict(AlreadyReversed) : null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> BalancesAsync(Guid employmentId, DateOnly? asOf, Guid? leaveTypeId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LeavePermissions.LedgerRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var e = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (e is null || !await InScopeAsync(e, "read", cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        return await ViewAsync(e, asOf, leaveTypeId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> MyBalancesAsync(Guid? companyId, DateOnly? asOf, Guid? leaveTypeId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(LeavePermissions.LedgerRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        // The employee is the token's user (employments.user_ref), never someone the request names; a request only chooses among the user's own
        // employments by company. As the employee's day of S1.27a: the answer lists them and says when a company must be chosen.
        var own = await db.Set<Employment>().AsNoTracking().Where(x => x.UserRef == actor.UserId && OwnStatuses.Contains(x.Status))
            .OrderBy(x => x.EmployeeNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var chosen = companyId is { } c ? own.FirstOrDefault(x => x.CompanyId == c) : own.Count == 1 ? own[0] : null;
        var employments = own.Select(x => new { employmentId = x.Id, companyId = x.CompanyId, employeeNo = x.EmployeeNo }).ToList();
        if (chosen is null || !await InScopeAsync(chosen, "read", cancellationToken).ConfigureAwait(false))
        {
            return new PeopleResult(PeopleOutcome.Done, Body: new
            {
                employments, employmentId = (Guid?)null, companyId = (Guid?)null, needsCompany = chosen is null && own.Count > 1, asOf = asOf, balances = Array.Empty<object>(),
            });
        }

        var view = await ViewAsync(chosen, asOf, leaveTypeId, cancellationToken).ConfigureAwait(false);
        return view.Body is BalanceView b ? view with { Body = b with { Employments = employments } } : view;
    }

    /// <summary>Saves one entry under the employment and type lock, after the caller's check and the negative-balance check (ruling Q8).</summary>
    private async Task<PeopleResult> WriteAsync(LeaveLedger row, LeaveTypeRules? rules, Func<CancellationToken, Task<PeopleResult?>> check, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LeaveLedgerStore.LockAsync(db, row.EmploymentId, row.LeaveTypeId, ct).ConfigureAwait(false);
        if (await check(ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        if (row.Quantity < 0m)
        {
            var allowance = rules?.NegativeAllowance ?? 0m;
            var lowest = await LeaveLedgerStore.LowestFromAsync(db, row.EmploymentId, row.LeaveTypeId, row.LeaveYear, row.EntryDate, row.Quantity, ct).ConfigureAwait(false);
            if (lowest < -allowance)
            {
                return PeopleResult.Conflict(BelowAllowed, new Dictionary<string, object?> { ["lowest"] = lowest, ["allowed"] = -allowance });
            }
        }

        db.Add(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(AlreadyReversed);
        }

        return new PeopleResult(PeopleOutcome.Created, Body: Entry(row, null));
    }

    private ValueTask<bool> InScopeAsync(Employment e, string action, CancellationToken ct) =>
        scope.AllowsAsync(new LeaveLedger { CompanyId = e.CompanyId, EmploymentId = e.Id }, LeaveLedgerAccess.Scope, action, Source, ct);

    /// <summary>
    /// The balances of an employment as of a date: for each leave type of its company that is in use or has entries, the leave year containing
    /// the date, the balance (the year's entries dated on or before it), the totals by kind, the entries themselves, and the policy that applies
    /// with its negative limit, or why none does.
    /// </summary>
    private async Task<PeopleResult> ViewAsync(Employment e, DateOnly? asOf, Guid? leaveTypeId, CancellationToken ct)
    {
        var day = asOf ?? await applier.TodayAsync(e.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => t.CompanyId == e.CompanyId && !t.IsDeleted && (leaveTypeId == null || t.Id == leaveTypeId))
            .OrderBy(t => t.Code).ToListAsync(ct).ConfigureAwait(false);
        if (leaveTypeId is not null && types.Count == 0)
        {
            return PeopleResult.NotFound;
        }

        var entries = await db.Set<LeaveLedger>().AsNoTracking().Where(x => x.EmploymentId == e.Id && (leaveTypeId == null || x.LeaveTypeId == leaveTypeId))
            .OrderBy(x => x.EntryDate).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        var reversedBy = entries.Where(x => x.ReversesEntryId is not null).ToDictionary(x => x.ReversesEntryId!.Value, x => x.Id);
        var policy = await policies.AtAsync(e.Id, day, ct).ConfigureAwait(false);
        var balances = new List<object>();
        foreach (var type in types)
        {
            var hasEntries = entries.Any(x => x.LeaveTypeId == type.Id);
            if (!type.IsActive && !hasEntries)
            {
                continue;
            }

            var rules = policy?.For(type.Code);
            var year = await years.YearAsync(e.CompanyId, rules, day, ct).ConfigureAwait(false);
            var made = entries.Where(x => x.LeaveTypeId == type.Id && x.LeaveYear == year.Year && x.EntryDate <= day).ToList();
            var match = rules is null ? null : policy!.Resolution.Policy;
            balances.Add(new
            {
                leaveTypeId = type.Id,
                code = type.Code,
                name = type.NameI18n.RootElement.Clone(),
                unit = type.Unit,
                isActive = type.IsActive,
                leaveYear = year.Year,
                yearStart = year.Start,
                yearEnd = year.End,
                balance = made.Sum(x => x.Quantity),
                totals = made.GroupBy(x => x.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity), StringComparer.Ordinal),
                negativeLimit = rules?.NegativeAllowance ?? 0m,
                policy = match is null ? null : new { policyId = match.PolicyId, code = match.PolicyCode, versionId = match.VersionId, version = match.Version, model = rules!.Entitlement.Model },
                noPolicy = rules is null ? new { reason = policy?.NoPolicyReason(type.Code) ?? "no_employment", message = NoPolicy } : null,
                entries = made.Select(x => Entry(x, reversedBy.TryGetValue(x.Id, out var by) ? by : null)).ToList(),
            });
        }

        return new PeopleResult(PeopleOutcome.Done, Body: new BalanceView(e.Id, e.CompanyId, e.EmployeeNo, day, balances, null));
    }

    /// <summary>The balances answer; the employee's own view adds the list of their employments.</summary>
    private sealed record BalanceView(Guid EmploymentId, Guid CompanyId, string EmployeeNo, DateOnly AsOf, IReadOnlyList<object> Balances, object? Employments)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public object? Employments { get; init; } = Employments;
    }

    /// <summary>One entry as the balance view and the post answer show it, with the text key that explains it (and the period of an automatic one).</summary>
    public static Dictionary<string, object?> Entry(LeaveLedger x, Guid? reversedById) => new(StringComparer.Ordinal)
    {
        ["id"] = x.Id,
        ["employmentId"] = x.EmploymentId,
        ["leaveTypeId"] = x.LeaveTypeId,
        ["leaveYear"] = x.LeaveYear,
        ["entryDate"] = x.EntryDate,
        ["kind"] = x.Kind,
        ["quantity"] = x.Quantity,
        ["expiresOn"] = x.ExpiresOn,
        ["sourceType"] = x.SourceType,
        ["sourceId"] = x.SourceId,
        ["policyVersionId"] = x.PolicyVersionId,
        ["reversesEntryId"] = x.ReversesEntryId,
        ["reversedById"] = reversedById,
        ["reason"] = x.SourceType == LeaveLedgerStore.Automatic ? null : x.Reason,
        ["period"] = x.SourceType == LeaveLedgerStore.Automatic ? x.Reason : null,
        ["explanation"] = "hrm.leave.entry." + (x.ReversesEntryId is not null ? "reversal" : x.SourceType == LeaveLedgerStore.Automatic ? "automatic_" + x.Kind : x.Kind),
        ["createdAt"] = x.CreatedAt,
        ["createdBy"] = x.CreatedBy,
    };
}
