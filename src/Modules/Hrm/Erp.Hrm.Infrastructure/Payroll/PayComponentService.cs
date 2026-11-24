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
/// Pay components and their effective-dated versions (slice S1.34b Build items 4 to 6; FR-HRM-PAY-02, -03, -05, FR-HRM-CMP-03). A
/// component is the stable record (code, kind, payslip order, recurring or one-time); a version is its calculation definition from a
/// date: the method with what it needs, the pro-rata rule and parameters, the statutory-base flags and the tax-treatment label. A
/// new version ends the old one on its start date, the old row stays. The definition is stored and checked, never evaluated
/// (S1.35): the names a formula or a percentage uses must exist and must not lead back to the component (<see cref="IFormulaValidator"/>;
/// the database repeats the base-list check). No statutory rate, slab, threshold or flag value is coded (AS-4): flag codes and table
/// codes are the pack's; a component whose statutory table does not exist yet is "incomplete" and is not usable by a run (Q4).
/// </summary>
internal sealed class PayComponentService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFormulaValidator formulas,
    EmploymentApplier applier) : IPayComponentService
{
    public const string CodeTaken = "errors.hrm.pay.component_code_taken";
    public const string NameRequired = "errors.hrm.pay.name_required";
    public const string ValidFromRequired = "errors.hrm.pay.valid_from_required";
    public const string VersionNotLater = "errors.hrm.pay.version_not_later";
    public const string BaseUnknown = "errors.hrm.pay.base_unknown";
    public const string BaseSelf = "errors.hrm.pay.base_self";
    public const string FlagsInvalid = "errors.hrm.pay.flags_invalid";
    public const string OrderRange = "errors.hrm.pay.payslip_order";

    private const int NameLength = 200;
    private const int MaxFlags = 30;

    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> CreateAsync(PayComponentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.ComponentsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CompanyId is not { } company)
        {
            errors["companyId"] = [PersonService.Required];
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        var row = new PayComponent
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = company,
            NameI18n = JsonDocument.Parse("{}"),
            Source = "tenant",
            IsActive = request.IsActive ?? true,
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, PayComponentAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        Apply(row, request, creating: true, errors);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            db.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (request.Version is { } first)
            {
                var added = await AddVersionCoreAsync(row, first, cancellationToken).ConfigureAwait(false);
                if (added.Outcome is not PeopleOutcome.Created)
                {
                    await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    db.ChangeTracker.Clear();
                    return added;
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }

        return new PeopleResult(PeopleOutcome.Created, Body: await ViewAsync(row, null, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PeopleResult> UpdateAsync(Guid componentId, PayComponentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.ComponentsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<PayComponent>().FirstOrDefaultAsync(c => c.Id == componentId && !c.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, PayComponentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, PayComponentAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
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

        if (request.Code is not null && !string.Equals(request.Code.Trim(), row.Code, StringComparison.Ordinal))
        {
            errors["code"] = [PersonService.CannotClear]; // formulas name the code: it never changes
        }

        Apply(row, request, creating: false, errors);
        if (request.Kind is not null && request.Kind != row.Kind)
        {
            errors["kind"] = [PersonService.CannotClear]; // payslips and results already carry the kind
        }

        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        EmploymentApplier.Bump(row, clock.UtcNow, actor.UserId);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

        return new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(row, null, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PeopleResult> AddVersionAsync(Guid componentId, PayComponentVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.ComponentsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var component = await db.Set<PayComponent>().FirstOrDefaultAsync(c => c.Id == componentId && !c.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (component is null || !await scope.AllowsAsync(component, PayComponentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(component, PayComponentAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await AddVersionCoreAsync(component, request, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is not PeopleOutcome.Created)
            {
                db.ChangeTracker.Clear();
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency); // two people added a version to the component at once (review S1.34b L3)
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }
    }

    public async Task<PeopleResult> GetAsync(Guid componentId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayPermissions.ComponentsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await db.Set<PayComponent>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == componentId && !c.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, PayComponentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        return new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(row, asOf, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PeopleResult> CheckFormulaAsync(Guid companyId, string? formula, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayPermissions.ComponentsRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var probe = new PayComponent { CompanyId = companyId, Code = string.Empty, Kind = string.Empty, Source = "tenant", NameI18n = JsonDocument.Parse("{}") };
        if (!await scope.AllowsAsync(probe, PayComponentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var codes = await CodesAsync(companyId, cancellationToken).ConfigureAwait(false);
        var check = formulas.Check(formula, codes.Keys.ToHashSet(StringComparer.Ordinal));
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            valid = check.IsValid,
            components = check.Components.Order(StringComparer.Ordinal).ToList(),
            inputs = check.Inputs.Order(StringComparer.Ordinal).ToList(),
            problems = check.Problems.Select(p => new { error = p.ErrorKey, name = p.Name }).ToList(),
        });
    }

    /// <summary>Adds a version to a component the caller may change; everything is checked before anything is written.</summary>
    private async Task<PeopleResult> AddVersionCoreAsync(PayComponent component, PayComponentVersionRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string field, string key) => errors[field] = [.. errors.GetValueOrDefault(field, []).Append(key).Distinct(StringComparer.Ordinal)];
        if (r.ValidFrom is not { } from)
        {
            Fail("validFrom", ValidFromRequired);
            return PeopleResult.Fields(errors);
        }

        // One writer of versions per company at a time, so two requests cannot each pass the loop check and together make a loop
        // (review S1.34b H3). The lock ends with the transaction.
        await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({component.CompanyId.ToString()}, 0))", ct).ConfigureAwait(false);

        var versions = await db.Set<PayComponentVersion>().Where(v => v.ComponentId == component.Id).OrderBy(v => v.ValidFrom).ToListAsync(ct).ConfigureAwait(false);
        if (versions.Count > 0 && from <= versions[^1].ValidFrom)
        {
            Fail("validFrom", VersionNotLater);
        }

        var bases = (r.BaseComponentIds ?? []).Distinct().ToList();
        foreach (var (field, key) in ComponentRules.CheckMethod(r.Method, r.Formula, bases, r.StatutoryTableCode))
        {
            Fail(field, key);
        }

        var (proRataParams, proRataError) = ComponentRules.CheckProRata(r.ProRataRule, r.ProRataParams);
        if (proRataError is not null)
        {
            Fail(r.ProRataRule is null || !ComponentRules.ProRataRules.Contains(r.ProRataRule, StringComparer.Ordinal) ? "proRataRule" : "proRataParams", proRataError);
        }

        var flags = r.StatutoryFlags ?? new Dictionary<string, bool>();
        if (flags.Count > MaxFlags || flags.Keys.Any(k => !ComponentRules.IsFlagCode(k)))
        {
            Fail("statutoryFlags", ComponentRules.FlagCodeInvalid);
        }

        if (r.TaxTreatment is not null && !ComponentRules.IsLabel(r.TaxTreatment))
        {
            Fail("taxTreatment", ComponentRules.CodeLabelInvalid);
        }

        if (r.StatutoryTableCode is not null && !ComponentRules.IsLabel(r.StatutoryTableCode))
        {
            Fail("statutoryTableCode", ComponentRules.CodeLabelInvalid);
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        // Names: percentages and formulas may only use components of this company, never this one, and never in a loop.
        var known = await CodesAsync(component.CompanyId, ct).ConfigureAwait(false);
        var uses = new HashSet<string>(StringComparer.Ordinal);
        if (bases.Contains(component.Id))
        {
            Fail("baseComponentIds", BaseSelf);
        }
        else
        {
            foreach (var b in bases)
            {
                if (known.FirstOrDefault(k => k.Value == b) is { Key: not null } hit)
                {
                    uses.Add(hit.Key);
                }
                else
                {
                    Fail("baseComponentIds", BaseUnknown);
                }
            }
        }

        if (r.Method == "formula")
        {
            var check = formulas.Check(r.Formula, known.Keys.ToHashSet(StringComparer.Ordinal));
            foreach (var p in check.Problems)
            {
                Fail("formula", p.ErrorKey);
            }

            uses.UnionWith(check.Components);
        }

        if (errors.Count == 0)
        {
            var graph = await GraphAsync(component.CompanyId, from, known, ct).ConfigureAwait(false);
            graph[component.Code] = uses;
            if (formulas.FindCycle(graph) is not null)
            {
                Fail(r.Method == "formula" ? "formula" : "baseComponentIds", FormulaValidator.Cycle);
            }
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        if (versions.Count > 0 && (versions[^1].ValidTo is null || versions[^1].ValidTo > from))
        {
            versions[^1].ValidTo = from; // the old version ends where the new one starts; its row stays
            EmploymentApplier.Bump(versions[^1], now, actor.UserId);
        }

        var version = new PayComponentVersion
        {
            Id = Guid.CreateVersion7(now),
            ComponentId = component.Id,
            Method = r.Method!,
            Formula = r.Method == "formula" ? r.Formula!.Trim() : null,
            BaseComponentIds = [.. bases],
            StatutoryTableCode = r.StatutoryTableCode?.Trim(),
            ProRataRule = r.ProRataRule!,
            ProRataParams = proRataParams is { } pp ? JsonDocument.Parse(pp.GetRawText()) : null,
            StatutoryFlags = JsonSerializer.SerializeToDocument(flags.OrderBy(f => f.Key, StringComparer.Ordinal).ToDictionary(f => f.Key, f => f.Value)),
            TaxTreatment = r.TaxTreatment?.Trim(),
            ValidFrom = from,
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        db.Add(version);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PeopleResult(PeopleOutcome.Created, Body: await ViewAsync(component, null, ct).ConfigureAwait(false));
    }

    /// <summary>Code to id of the live components of a company.</summary>
    private async Task<Dictionary<string, Guid>> CodesAsync(Guid companyId, CancellationToken ct) =>
        await db.Set<PayComponent>().AsNoTracking().Where(c => c.CompanyId == companyId && !c.IsDeleted).ToDictionaryAsync(c => c.Code, c => c.Id, StringComparer.Ordinal, ct).ConfigureAwait(false);

    /// <summary>
    /// "Component uses components" from a date onward: every version that is in force on that date or starts later (bases and the names
    /// in formulas). A loop that starts at a later date is a loop too (review S1.34b H3).
    /// </summary>
    private async Task<Dictionary<string, IReadOnlySet<string>>> GraphAsync(Guid companyId, DateOnly day, Dictionary<string, Guid> known, CancellationToken ct)
    {
        var byId = known.ToDictionary(k => k.Value, k => k.Key);
        var inForce = await (from v in db.Set<PayComponentVersion>().AsNoTracking()
                             join c in db.Set<PayComponent>().AsNoTracking() on v.ComponentId equals c.Id
                             where c.CompanyId == companyId && !c.IsDeleted && (v.ValidTo == null || v.ValidTo > day)
                             select new { c.Code, v.BaseComponentIds, v.Formula }).ToListAsync(ct).ConfigureAwait(false);
        var codes = known.Keys.ToHashSet(StringComparer.Ordinal);
        var graph = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var v in inForce)
        {
            var uses = new HashSet<string>(v.BaseComponentIds.Where(byId.ContainsKey).Select(b => byId[b]), StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(v.Formula))
            {
                uses.UnionWith(formulas.Check(v.Formula, codes).Components);
            }

            graph[v.Code] = graph.TryGetValue(v.Code, out var earlier) ? new HashSet<string>(earlier.Concat(uses), StringComparer.Ordinal) : uses;
        }

        return graph;
    }

    private async Task<object> ViewAsync(PayComponent c, DateOnly? asOf, CancellationToken ct)
    {
        var versions = await db.Set<PayComponentVersion>().AsNoTracking().Where(v => v.ComponentId == c.Id).OrderBy(v => v.ValidFrom).ToListAsync(ct).ConfigureAwait(false);
        var day = asOf ?? await applier.TodayAsync(c.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
        var current = versions.FirstOrDefault(v => v.ValidFrom <= day && (v.ValidTo == null || v.ValidTo > day));
        var incomplete = new List<string>();
        if (current is null)
        {
            incomplete.Add("no_version");
        }
        else if (current.StatutoryTableCode is { } table
            && !await db.Set<StatutoryTable>().AsNoTracking().AnyAsync(t => t.Code == table, ct).ConfigureAwait(false))
        {
            incomplete.Add("statutory_table_missing:" + table); // a missing pack table: the component cannot be used by a run (Q4)
        }

        return new
        {
            id = c.Id,
            companyId = c.CompanyId,
            code = c.Code,
            nameI18n = JsonSerializer.Deserialize<JsonElement>(c.NameI18n.RootElement.GetRawText()),
            kind = c.Kind,
            payslipOrder = c.PayslipOrder,
            isRecurring = c.IsRecurring,
            isOneTimeAllowed = c.IsOneTimeAllowed,
            source = c.Source,
            isActive = c.IsActive,
            rowVersion = c.RowVersion,
            incomplete,
            currentVersionId = current?.Id,
            versions = versions.Select(v => new
            {
                id = v.Id,
                validFrom = v.ValidFrom,
                validTo = v.ValidTo,
                method = v.Method,
                formula = v.Formula,
                baseComponentIds = v.BaseComponentIds,
                statutoryTableCode = v.StatutoryTableCode,
                proRataRule = v.ProRataRule,
                proRataParams = v.ProRataParams is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(v.ProRataParams.RootElement.GetRawText()),
                statutoryFlags = JsonSerializer.Deserialize<JsonElement>(v.StatutoryFlags.RootElement.GetRawText()),
                taxTreatment = v.TaxTreatment,
            }).ToList(),
        };
    }

    private static void Apply(PayComponent row, PayComponentRequest r, bool creating, Dictionary<string, string[]> errors)
    {
        void Fail(string field, string key) => errors[field] = [.. errors.GetValueOrDefault(field, []).Append(key).Distinct(StringComparer.Ordinal)];
        if (creating)
        {
            var code = r.Code?.Trim();
            if (!ComponentRules.IsComponentCode(code))
            {
                Fail("code", ComponentRules.CodeInvalid);
            }
            else
            {
                row.Code = code!;
            }

            if (r.Kind is null || !ComponentRules.Kinds.Contains(r.Kind, StringComparer.Ordinal))
            {
                Fail("kind", ComponentRules.KindInvalid);
            }
            else
            {
                row.Kind = r.Kind;
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

        if (creating || r.PayslipOrder is not null)
        {
            if (r.PayslipOrder is not { } order)
            {
                Fail("payslipOrder", PersonService.Required);
            }
            else if (order is < 0 or > 9999)
            {
                Fail("payslipOrder", OrderRange);
            }
            else
            {
                row.PayslipOrder = order;
            }
        }

        row.IsRecurring = r.IsRecurring ?? (creating ? true : row.IsRecurring);
        row.IsOneTimeAllowed = r.IsOneTimeAllowed ?? row.IsOneTimeAllowed;
        row.IsActive = r.IsActive ?? row.IsActive;
    }

    internal static PeopleResult Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "uq_pay_components_company_id_code" => Field("code", CodeTaken),
        "ck_pay_component_versions_bases" => Field("baseComponentIds", pg.MessageText.Contains("lead back", StringComparison.Ordinal) ? FormulaValidator.Cycle : pg.MessageText.Contains("itself", StringComparison.Ordinal) ? BaseSelf : BaseUnknown),
        "ex_pay_component_versions_component_id" => Field("validFrom", VersionNotLater),
        _ => PersonService.Refusal(pg),
    };

    private static PeopleResult Field(string field, string key) => PeopleResult.Fields(new Dictionary<string, string[]> { [field] = [key] });
}
