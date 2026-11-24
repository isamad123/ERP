using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Payroll;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>
/// Salary structures (slice S1.34b Build item 7; FR-HRM-PAY-04): a named bundle of components with their order of evaluation,
/// defaults and mandatory flags, effective-dated, and a default assign rule (by grade, employee type or individually). Creating
/// an empty structure and changing its name or switching it off are direct; a change of its components or of its assign rule is
/// held for a second person while <c>hrm.pay.change_approval</c> is on (slice S1.34 Q5, FR-XM-32: the kernel refuses the
/// requester as approver). Default amounts and percentages are salary-class data (GHR-06): hidden without the field class and
/// not settable without edit access to it.
/// </summary>
internal sealed partial class SalaryStructureService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IFieldAccess fields,
    ISettings settings,
    IHeldChanges held,
    IFormulaValidator formulas,
    IFieldEncryptorProvider keys,
    EmploymentApplier applier) : ISalaryStructureService
{
    public const string CodeInvalid = "errors.hrm.pay.structure_code";
    public const string CodeTaken = "errors.hrm.pay.structure_code_taken";
    public const string GradeUnknown = "errors.hrm.pay.assign_rule_grade_unknown";
    public const string TypeUnknown = "errors.hrm.pay.assign_rule_type_unknown";
    private const int NameLength = 200;

    private DbContextScopeSource Source => new(db);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$")]
    private static partial Regex CodePattern();

    public async Task<PeopleResult> CreateAsync(SalaryStructureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.StructuresCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.CompanyId is not { } company)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["companyId"] = [PersonService.Required] });
        }

        var now = clock.UtcNow;
        var row = new SalaryStructure
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = company,
            NameI18n = JsonDocument.Parse("{}"),
            IsActive = request.IsActive ?? true,
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, SalaryStructureAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var code = request.Code?.Trim();
        if (code is null || !CodePattern().IsMatch(code))
        {
            errors["code"] = [CodeInvalid];
        }
        else
        {
            row.Code = code;
        }

        Names(row, request, creating: true, errors);
        if (request.Components is not null)
        {
            errors["components"] = [PersonService.CannotClear]; // components are set on an existing structure, through its change
        }

        if (request.AssignRule is { } rule)
        {
            await CheckRuleAsync(rule, company, errors, cancellationToken).ConfigureAwait(false);
            if (errors.Count == 0)
            {
                row.AssignRule = JsonDocument.Parse(rule.GetRawText());
            }
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        db.Add(row);
        return await SaveAsync(row, created: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid structureId, SalaryStructureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(PayPermissions.StructuresUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<SalaryStructure>().FirstOrDefaultAsync(s => s.Id == structureId && !s.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, SalaryStructureAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, SalaryStructureAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
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
            errors["code"] = [PersonService.CannotClear];
        }

        Names(row, request, creating: false, errors);
        row.IsActive = request.IsActive ?? row.IsActive;

        // The part that needs a second person: the assign rule when it changes, and the components.
        string? ruleJson = null;
        if (request.AssignRule is { } rule)
        {
            await CheckRuleAsync(rule, row.CompanyId, errors, cancellationToken).ConfigureAwait(false);
            var text = Canonical(rule);
            if (errors.Count == 0 && text != (row.AssignRule is null ? null : Canonical(row.AssignRule.RootElement)))
            {
                ruleJson = text;
            }
        }

        StructurePlan plan;
        if (request.Components is { } set)
        {
            var lines = new List<StructureLine>();
            var currency = await CompanyCurrencyAsync(row.CompanyId, cancellationToken).ConfigureAwait(false);

            // Defaults are salary-class data (GHR-06): a caller without edit access keeps what is in force (it cannot see it, so
            // sending nothing never wipes it) and cannot set a different value (review S1.34b M1).
            var fieldMap = await fields.ForAsync(SalaryStructureComponentAccess.Fields, "update", row.CompanyId, cancellationToken).ConfigureAwait(false);
            var inForce = set.ValidFrom is { } start
                ? await db.Set<SalaryStructureComponent>().AsNoTracking().Where(r => r.StructureId == row.Id && r.ValidFrom <= start && (r.ValidTo == null || r.ValidTo > start)).ToListAsync(cancellationToken).ConfigureAwait(false)
                : [];
            var denied = new List<string>();
            foreach (var item in set.Items ?? [])
            {
                if (item.ComponentId is not { } componentId)
                {
                    errors["components.items"] = [PersonService.Required];
                    continue;
                }

                var current = inForce.FirstOrDefault(r => r.ComponentId == componentId);
                var amount = item.DefaultAmount is { } a ? ComponentRules.Money(a) : (decimal?)null;
                var percent = item.DefaultPercent;
                if (!fieldMap.CanEdit("default_amount"))
                {
                    if (amount is not null && amount != current?.DefaultAmount)
                    {
                        denied.Add("default_amount");
                    }

                    amount = current?.DefaultAmount;
                }

                if (!fieldMap.CanEdit("default_percent"))
                {
                    if (percent is not null && percent != current?.DefaultPercent)
                    {
                        denied.Add("default_percent");
                    }

                    percent = current?.DefaultPercent;
                }

                lines.Add(new StructureLine(
                    componentId,
                    item.EvaluationOrder ?? 0,
                    amount,
                    percent,
                    item.IsMandatory ?? false,
                    (item.CurrencyCode ?? currency).Trim().ToUpperInvariant()));
            }

            if (denied.Count > 0)
            {
                db.ChangeTracker.Clear();
                return new PeopleResult(PeopleOutcome.Invalid, FieldShaping.NotEditable, FieldErrors: denied.Distinct(StringComparer.Ordinal).ToDictionary(f => f, _ => new[] { FieldShaping.NotEditable }, StringComparer.Ordinal));
            }

            plan = new StructurePlan(row.Id, row.CompanyId, ruleJson, set.ValidFrom, lines);
        }
        else
        {
            plan = new StructurePlan(row.Id, row.CompanyId, ruleJson, null, null);
        }

        foreach (var (field, keys) in await StructurePlanner.CheckAsync(db, plan, formulas, await applier.TodayAsync(row.CompanyId, clock.UtcNow, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
        {
            errors[field] = keys;
        }

        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        var changesComponents = plan.Lines is not null;
        if (changesComponents)
        {
            // Defaults are salary-class data: the caller needs edit access to them (GHR-06).
            if (!await permissions.HasPermissionAsync(PayPermissions.StructureComponentsUpdate, cancellationToken).ConfigureAwait(false))
            {
                db.ChangeTracker.Clear();
                return PeopleResult.Forbidden();
            }
        }

        var needsApproval = (changesComponents || ruleJson is not null)
            && (await settings.GetAsync(HrmSettingKeys.HrmPayChangeApproval, (Guid?)row.CompanyId, cancellationToken).ConfigureAwait(false)).Value.ValueKind != JsonValueKind.False;
        var now = clock.UtcNow;
        if (!needsApproval)
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StructurePlanner.ApplyAsync(db, row, plan, now, actor.UserId, cancellationToken).ConfigureAwait(false);
                EmploymentApplier.Bump(row, now, actor.UserId);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
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

        // Name and switch-off changes are direct; the components and the rule are held for a second person (nothing else is changed yet).
        await using var held_ = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EmploymentApplier.Bump(row, now, actor.UserId);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var payload = PayHeldChanges.Seal(await keys.GetAsync(cancellationToken).ConfigureAwait(false), plan);
            var requestId = await held.SubmitAsync(
                new HeldChangeRequest(PayHeldChanges.StructureKind, SalaryStructureAccess.Scope.Resource, row.Id, payload, string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim()),
                held_.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            await held_.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PeopleResult(PeopleOutcome.Pending, Body: new { status = "pending", requestId });
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

    public async Task<PeopleResult> GetAsync(Guid structureId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(PayPermissions.StructuresRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var row = await db.Set<SalaryStructure>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == structureId && !s.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, SalaryStructureAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        return new PeopleResult(PeopleOutcome.Done, Body: await ViewAsync(row, asOf, cancellationToken).ConfigureAwait(false));
    }

    private async Task<object> ViewAsync(SalaryStructure s, DateOnly? asOf, CancellationToken ct)
    {
        var day = asOf ?? await applier.TodayAsync(s.CompanyId, clock.UtcNow, ct).ConfigureAwait(false);
        var rows = await (from r in db.Set<SalaryStructureComponent>().AsNoTracking()
                          join c in db.Set<PayComponent>().AsNoTracking() on r.ComponentId equals c.Id
                          where r.StructureId == s.Id
                          orderby r.ValidFrom, r.EvaluationOrder
                          select new { Row = r, c.Code, c.Kind }).ToListAsync(ct).ConfigureAwait(false);
        var map = await fields.ForAsync(SalaryStructureComponentAccess.Fields, "read", s.CompanyId, ct).ConfigureAwait(false);
        var showAmount = map.Of("default_amount") != FieldAccessLevel.Hidden;
        var showPercent = map.Of("default_percent") != FieldAccessLevel.Hidden;
        object Shape(SalaryStructureComponent r, string code, string kind)
        {
            var view = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = r.Id,
                ["componentId"] = r.ComponentId,
                ["componentCode"] = code,
                ["kind"] = kind,
                ["evaluationOrder"] = r.EvaluationOrder,
                ["isMandatory"] = r.IsMandatory,
                ["currencyCode"] = r.CurrencyCode,
                ["validFrom"] = r.ValidFrom,
                ["validTo"] = r.ValidTo,
            };
            if (showAmount)
            {
                view["defaultAmount"] = r.DefaultAmount;
            }

            if (showPercent)
            {
                view["defaultPercent"] = r.DefaultPercent;
            }

            return view;
        }

        return new
        {
            id = s.Id,
            companyId = s.CompanyId,
            code = s.Code,
            nameI18n = JsonSerializer.Deserialize<JsonElement>(s.NameI18n.RootElement.GetRawText()),
            assignRule = s.AssignRule is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(s.AssignRule.RootElement.GetRawText()),
            isActive = s.IsActive,
            rowVersion = s.RowVersion,
            asOf = day,
            components = rows.Where(x => x.Row.ValidFrom <= day && (x.Row.ValidTo == null || x.Row.ValidTo > day)).OrderBy(x => x.Row.EvaluationOrder).Select(x => Shape(x.Row, x.Code, x.Kind)).ToList(),
            history = rows.Select(x => Shape(x.Row, x.Code, x.Kind)).ToList(),
        };
    }

    private async Task CheckRuleAsync(JsonElement rule, Guid company, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var (grades, types, error) = ComponentRules.CheckAssignRule(rule);
        if (error is not null)
        {
            errors["assignRule"] = [error];
            return;
        }

        if (grades.Count > 0)
        {
            var found = await db.Set<Grade>().AsNoTracking().CountAsync(g => grades.Contains(g.Id) && g.CompanyId == company && !g.IsDeleted, ct).ConfigureAwait(false);
            if (found != grades.Count)
            {
                errors["assignRule"] = [GradeUnknown];
            }
        }

        if (types.Any(t => !EmploymentCatalogue.Default.IsEmployeeType(t)))
        {
            errors["assignRule"] = [TypeUnknown];
        }
    }

    private async Task<string> CompanyCurrencyAsync(Guid company, CancellationToken ct) =>
        (await db.Database.SqlQuery<string>($"select base_currency_code::text as \"Value\" from org.companies where id = {company}").ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault() ?? string.Empty;

    private static string Canonical(JsonElement e) => JsonSerializer.Serialize(Sorted(e));

    private static object? Sorted(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => Sorted(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Sorted).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static void Names(SalaryStructure row, SalaryStructureRequest r, bool creating, Dictionary<string, string[]> errors)
    {
        if (!creating && r.NameI18n is null)
        {
            return;
        }

        var names = (r.NameI18n ?? new Dictionary<string, string>()).Select(n => (Key: n.Key.Trim().ToLowerInvariant(), Value: n.Value?.Trim() ?? string.Empty)).Where(n => n.Value.Length > 0).ToList();
        if (names.Count == 0 || names.Any(n => n.Key.Length is < 2 or > 5))
        {
            errors["nameI18n"] = [PayComponentService.NameRequired];
        }
        else if (names.Any(n => n.Value.Length > NameLength))
        {
            errors["nameI18n"] = [PersonService.TooLong];
        }
        else
        {
            row.NameI18n = JsonSerializer.SerializeToDocument(names.DistinctBy(n => n.Key).ToDictionary(n => n.Key, n => n.Value));
        }
    }

    private async Task<PeopleResult> SaveAsync(SalaryStructure row, bool created, CancellationToken ct)
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

        return new PeopleResult(created ? PeopleOutcome.Created : PeopleOutcome.Done, Body: await ViewAsync(row, null, ct).ConfigureAwait(false));
    }

    internal static PeopleResult Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "uq_salary_structures_company_id_code" => PeopleResult.Fields(new Dictionary<string, string[]> { ["code"] = [CodeTaken] }),
        "ex_salary_structure_components_structure_id_component_id" => PeopleResult.Fields(new Dictionary<string, string[]> { ["components.validFrom"] = [PayComponentService.VersionNotLater] }),
        _ => PersonService.Refusal(pg),
    };
}
