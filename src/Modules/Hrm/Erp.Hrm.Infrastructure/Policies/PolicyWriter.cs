using System.Globalization;
using System.Text.Json;
using Erp.Hrm.Application.People;
using Erp.Hrm.Application.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Erp.Kernel.Contracts.Metadata;
using Erp.Kernel.Contracts.Org;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Policies;

/// <summary>Error keys of the policy services (texts in <c>Erp.Hrm.Contracts/Policies/Localization</c>).</summary>
internal static class PolicyErrors
{
    public const string UnknownType = "errors.hrm.policy.unknown_type";
    public const string CodeShape = "errors.hrm.policy.code_shape";
    public const string CodeTaken = "errors.hrm.policy.code_taken";
    public const string NameRequired = "errors.hrm.policy.name_required";
    public const string Retired = "errors.hrm.policy.retired";
    public const string NotDraft = "errors.hrm.policy.not_draft";
    public const string DraftExists = "errors.hrm.policy.draft_exists";
    public const string ActivateAfter = "errors.hrm.policy.activate_after";
    public const string RetireAfter = "errors.hrm.policy.retire_after";
    public const string StillAssigned = "errors.hrm.policy.still_assigned";
    public const string TooManyAssignments = "errors.hrm.policy.assignment.too_many";
    public const string PrecedenceRange = "errors.hrm.policy.assignment.precedence_range";
    public const string PrecedenceTaken = "errors.hrm.policy.assignment.precedence_taken";
    public const string PolicyUnknown = "errors.hrm.policy.assignment.policy_unknown";
    public const string PolicyNoVersion = "errors.hrm.policy.assignment.policy_no_version";
    public const string SetDate = "errors.hrm.policy.assignment.set_date";
    public const string UnknownValue = "errors.hrm.policy.rule.unknown_value";
    public const string CustomUnknown = "errors.hrm.policy.rule.custom_field_unknown";
    public const string CustomRetired = "errors.hrm.policy.rule.custom_field_retired";
    public const string CustomType = "errors.hrm.policy.rule.custom_field_type";
    public const string CustomValue = "errors.hrm.policy.rule.custom_value";
}

/// <summary>Employees an activated set would leave without a policy (a warning, slice S1.23a Build item 5).</summary>
internal sealed record Unmatched(int Count, IReadOnlyList<UnmatchedEmployee> Items);

internal sealed record UnmatchedEmployee(Guid EmploymentId, string EmployeeNo, string Name, string Reason);

/// <summary>
/// The checks and writes of policy activations, shared by the policy service and the approval of held activations (so a held change is
/// checked again exactly as it would be now). Works on the given HR context and its transaction.
/// </summary>
internal sealed class PolicyWriter(HrmDbContext db, PolicyTypeCatalog types, IOrgStructure org, ICustomFieldCatalog customFields)
{
    public const string EntityType = "hrm.employments";
    public const int MaxAssignments = 200;
    public const int UnmatchedShown = 50;

    /// <summary>Statuses of the employees the activation check looks at (current employments, those waiting to join included).</summary>
    public static readonly string[] CurrentStatuses = ["pre_joining", "active", "on_notice"];

    /// <summary>The policy version activation's refusal, or null. The version must be a valid draft of an active policy, dated after the version in force.</summary>
    public async Task<PeopleResult?> CheckActivationAsync(Policy policy, PolicyVersion version, DateOnly from, CancellationToken ct)
    {
        if (policy.IsDeleted || !policy.IsActive)
        {
            return PeopleResult.Conflict(PolicyErrors.Retired);
        }

        if (version.Status != "draft")
        {
            return PeopleResult.Conflict(PolicyErrors.NotDraft);
        }

        var type = types.Find(policy.PolicyType);
        if (type is null)
        {
            return PeopleResult.Invalid(PolicyErrors.UnknownType);
        }

        if (PolicySchema.Check(type.Schema, version.Parameters.RootElement) is { Count: > 0 } errors)
        {
            return PeopleResult.Fields(errors);
        }

        var latest = await LatestAsync(policy.Id, ct).ConfigureAwait(false);
        if (latest is not null && from <= latest.ValidFrom)
        {
            return new PeopleResult(PeopleOutcome.Invalid, "errors.data.invalid_request", FieldErrors: new Dictionary<string, string[]> { ["effectiveFrom"] = [PolicyErrors.ActivateAfter] },
                Extensions: new Dictionary<string, object?> { ["after"] = latest.ValidFrom, ["version"] = latest.Version });
        }

        return null;
    }

    /// <summary>Activates the draft from the date; the version in force before ends the day before (its end date is the start of the new one).</summary>
    public async Task ActivateAsync(Policy policy, PolicyVersion version, DateOnly from, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var latest = await db.Set<PolicyVersion>().Where(v => v.PolicyId == policy.Id && v.Status != "draft").OrderByDescending(v => v.ValidFrom).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (latest is not null && latest.ValidTo is null)
        {
            latest.ValidTo = from;
            Bump(latest, now, actor);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // first, so the two periods never overlap (exclusion constraint)
        }

        var tracked = await db.Set<PolicyVersion>().FirstAsync(v => v.Id == version.Id, ct).ConfigureAwait(false);
        tracked.Status = "active";
        tracked.ValidFrom = from;
        tracked.ValidTo = null;
        Bump(tracked, now, actor);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The latest version that was ever in force (active or retired), or null.</summary>
    public Task<PolicyVersion?> LatestAsync(Guid policyId, CancellationToken ct) =>
        db.Set<PolicyVersion>().AsNoTracking().Where(v => v.PolicyId == policyId && v.Status != "draft").OrderByDescending(v => v.ValidFrom).FirstOrDefaultAsync(ct);

    // ================================================================== assignment sets

    /// <summary>Takes the lock of one type in one company for the rest of the transaction (activations of one set run one after another).</summary>
    public Task LockSetAsync(Guid companyId, string type, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({"hrm.policy_assignments:" + companyId.ToString("D", CultureInfo.InvariantCulture) + ":" + type}, 0))", ct);

    /// <summary>The start date of the newest set of the type in the company (ended or not), or null when none was ever activated.</summary>
    public async Task<DateOnly?> LatestSetAsync(Guid companyId, string type, CancellationToken ct) =>
        await (from a in db.Set<PolicyAssignment>().AsNoTracking()
               join p in db.Set<Policy>().AsNoTracking() on a.PolicyId equals p.Id
               where a.CompanyId == companyId && p.PolicyType == type
               select (DateOnly?)a.ValidFrom).MaxAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Checks a set as a whole (ruling Q4, Build item 5): the policies are active policies of the type and company with a version, every
    /// precedence is unique and between 1 and 9999, every rule reads, uses the type's attributes and names records that exist in the
    /// company (a custom field must be active: a rule on a retired field is refused). Field errors are keyed <c>assignments[i].field</c>.
    /// </summary>
    public async Task<(List<LoadedAssignment>? Set, Dictionary<string, string[]>? Errors)> PrepareAsync(PolicyType type, Guid companyId, IReadOnlyList<PolicyAssignmentInput> inputs, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (inputs.Count > MaxAssignments)
        {
            errors["assignments"] = [PolicyErrors.TooManyAssignments];
            return (null, errors);
        }

        var policyIds = inputs.Where(i => i.PolicyId is not null).Select(i => i.PolicyId!.Value).Distinct().ToList();
        var policies = await db.Set<Policy>().AsNoTracking().Where(p => policyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);
        var withVersion = await db.Set<PolicyVersion>().AsNoTracking().Where(v => policyIds.Contains(v.PolicyId) && v.Status == "active").Select(v => v.PolicyId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var fields = (await customFields.FieldsAsync(EntityType, ct).ConfigureAwait(false)).ToDictionary(f => f.FieldKey, StringComparer.Ordinal);
        var seen = new HashSet<int>();
        var rows = new List<(int Index, Policy Policy, int Precedence, PolicyRule Rule)>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var at = string.Create(CultureInfo.InvariantCulture, $"assignments[{i}]");
            Policy? policy = null;
            if (input.PolicyId is not { } pid || !policies.TryGetValue(pid, out policy) || policy.CompanyId != companyId || policy.PolicyType != type.Code || policy.IsDeleted || !policy.IsActive)
            {
                errors[at + ".policyId"] = [PolicyErrors.PolicyUnknown];
            }
            else if (!withVersion.Contains(policy.Id))
            {
                errors[at + ".policyId"] = [PolicyErrors.PolicyNoVersion];
            }

            if (input.Precedence is not { } precedence || precedence is < 1 or > 9999)
            {
                errors[at + ".precedence"] = [PolicyErrors.PrecedenceRange];
                precedence = 0;
            }
            else if (!seen.Add(precedence))
            {
                errors[at + ".precedence"] = [PolicyErrors.PrecedenceTaken];
            }

            var (rule, problem) = input.Rule is { } json ? PolicyRules.Parse(json, type.Attributes) : (new PolicyRule([]), null);
            if (problem is not null)
            {
                errors[at + ".rule"] = [problem];
                continue;
            }

            if (CustomProblem(rule!, fields) is { } custom)
            {
                errors[at + ".rule"] = [custom];
                continue;
            }

            if (policy is not null)
            {
                rows.Add((i, policy, precedence, rule!));
            }
        }

        // Every record a rule names exists in the company: grades of the HR module, units, branches and locations of the kernel (D-10).
        var unknown = await UnknownIdsAsync(companyId, rows.Select(r => r.Rule).ToList(), ct).ConfigureAwait(false);
        foreach (var r in rows.Where(r => r.Rule.Conditions.SelectMany(c => c.Attribute is PolicyRules.EmployeeType or PolicyRules.Custom ? [] : c.Values.Select(v => v.GetGuid())).Any(unknown.Contains)
                     || r.Rule.Conditions.Any(c => c.Attribute == PolicyRules.EmployeeType && c.Values.Any(v => !EmploymentCatalogue.Default.IsEmployeeType(v.GetString())))))
        {
            errors[string.Create(CultureInfo.InvariantCulture, $"assignments[{r.Index}].rule")] = [PolicyErrors.UnknownValue];
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var now = DateTimeOffset.UnixEpoch;
        return ([.. rows.OrderBy(r => r.Precedence).Select(r => new LoadedAssignment(
            new PolicyAssignment { Id = Guid.Empty, CompanyId = companyId, PolicyId = r.Policy.Id, Precedence = r.Precedence, Rule = r.Rule.ToJson(), CreatedAt = now, UpdatedAt = now },
            r.Policy, r.Rule))], null);
    }

    /// <summary>
    /// The current employees (on the date) a set would leave without a policy: for a required type every one, otherwise none (the check
    /// is a warning; a type that applies to some employees only is not required).
    /// </summary>
    public async Task<Unmatched> UnmatchedAsync(PolicyType type, Guid companyId, DateOnly from, IReadOnlyList<LoadedAssignment> set, CancellationToken ct)
    {
        if (!type.Required)
        {
            return new Unmatched(0, []);
        }

        var resolver = new PolicyResolver(db, types, org);
        var loaded = await resolver.CompleteAsync(set, from, ct).ConfigureAwait(false);
        // The versions of a set being prepared: every policy has an active version, but maybe not yet on this date.
        var people = await PolicyAttributeReader.CompanyAsync(db, companyId, CurrentStatuses, from, ct).ConfigureAwait(false);
        var missing = people.Select(p => (p.Employment, Decision: PolicyResolver.Decide(type, p.Attributes, from, loaded)))
            .Where(p => p.Decision.Outcome == Contracts.Policies.PolicyResolution.NoPolicy).ToList();
        var shown = missing.Take(UnmatchedShown).ToList();
        var personIds = shown.Select(s => s.Employment.PersonId).Distinct().ToList();
        var names = await db.Set<Person>().AsNoTracking().Where(p => personIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => string.Join(' ', new[] { p.FirstName, p.LastName }.Where(n => !string.IsNullOrEmpty(n))), ct).ConfigureAwait(false);
        return new Unmatched(missing.Count, [.. shown.Select(s => new UnmatchedEmployee(s.Employment.Id, s.Employment.EmployeeNo, names.GetValueOrDefault(s.Employment.PersonId) ?? string.Empty, s.Decision.Reason!))]);
    }

    /// <summary>
    /// The date rule of a new set: after the start of the newest set, or on that same start when it is still in the future (that set is
    /// then replaced before it ever applied). Null when the date is acceptable.
    /// </summary>
    public static string? DateProblem(DateOnly from, DateOnly? latest, DateOnly today) =>
        latest is not { } s || from > s || (from == s && s > today) ? null : PolicyErrors.SetDate;

    /// <summary>Replaces the set in force from the date with the prepared one; returns the new assignment ids.</summary>
    public async Task<List<Guid>> ApplySetAsync(PolicyType type, Guid companyId, DateOnly start, IReadOnlyList<LoadedAssignment> set, DateTimeOffset now, Guid actor, CancellationToken ct)
    {
        var current = await (from a in db.Set<PolicyAssignment>()
                             join p in db.Set<Policy>() on a.PolicyId equals p.Id
                             where a.CompanyId == companyId && p.PolicyType == type.Code && (a.ValidTo == null || a.ValidTo > start)
                             select a).ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in current)
        {
            if (row.ValidFrom >= start)
            {
                db.Remove(row); // a future set (same start) that never applied
            }
            else
            {
                row.ValidTo = start;
                Bump(row, now, actor);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var ids = new List<Guid>();
        foreach (var a in set)
        {
            var row = new PolicyAssignment
            {
                Id = Guid.CreateVersion7(now),
                CompanyId = companyId,
                PolicyId = a.Policy.Id,
                Precedence = a.Row.Precedence,
                Rule = a.Rule.ToJson(),
                ValidFrom = start,
                ValidTo = null,
                CreatedAt = now,
                CreatedBy = actor,
                UpdatedAt = now,
                UpdatedBy = actor,
                RowVersion = 1,
            };
            db.Add(row);
            ids.Add(row.Id);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ids;
    }

    // ================================================================== names and values

    /// <summary>The names of every record the rules name (for rules in words): id to code and names; custom fields with their options.</summary>
    public async Task<object> NamesAsync(Guid companyId, IReadOnlyList<PolicyRule> rules, PolicyAttributesView? extra, CancellationToken ct)
    {
        var grades = rules.SelectMany(r => PolicyRules.Ids(r, PolicyRules.Grade)).Concat(extra?.Grade is { } g ? [g] : []).Distinct().ToList();
        var units = rules.SelectMany(r => PolicyRules.Ids(r, PolicyRules.Unit)).Concat(extra?.Unit is { } u ? [u] : []).Distinct().ToList();
        var branches = rules.SelectMany(r => PolicyRules.Ids(r, PolicyRules.Branch)).Concat(extra?.Branch is { } b ? [b] : []).Distinct().ToList();
        var locations = rules.SelectMany(r => PolicyRules.Ids(r, PolicyRules.Location)).Concat(extra?.Location is { } l ? [l] : []).Distinct().ToList();
        var names = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var grade in await db.Set<Grade>().AsNoTracking().Where(x => grades.Contains(x.Id) && x.CompanyId == companyId).ToListAsync(ct).ConfigureAwait(false))
        {
            names[grade.Id.ToString()] = new { code = grade.Code, name = PolicyAttributeReader.Names(grade.NameI18n), rank = grade.Rank };
        }

        foreach (var (kind, ids) in new[] { (OrgRecordKind.OrgUnit, units), (OrgRecordKind.Branch, branches), (OrgRecordKind.Location, locations) })
        {
            foreach (var r in await org.FindAsync(kind, ids, ct).ConfigureAwait(false))
            {
                names[r.Id.ToString()] = new { code = r.Code, name = r.Name };
            }
        }

        var fields = await customFields.FieldsAsync(EntityType, ct).ConfigureAwait(false);
        return new
        {
            records = names,
            customFields = fields.Select(f => new { key = f.FieldKey, dataType = f.DataType, status = f.Status, label = f.Label, options = f.Options }),
            employeeTypes = EmploymentCatalogue.Default.EmployeeTypes,
        };
    }

    private async Task<HashSet<Guid>> UnknownIdsAsync(Guid companyId, IReadOnlyList<PolicyRule> rules, CancellationToken ct)
    {
        var unknown = new HashSet<Guid>();
        var grades = rules.SelectMany(r => PolicyRules.Ids(r, PolicyRules.Grade)).Distinct().ToList();
        var found = await db.Set<Grade>().AsNoTracking().Where(g => grades.Contains(g.Id) && g.CompanyId == companyId && !g.IsDeleted).Select(g => g.Id).ToListAsync(ct).ConfigureAwait(false);
        unknown.UnionWith(grades.Except(found));
        foreach (var (kind, attribute) in new[] { (OrgRecordKind.OrgUnit, PolicyRules.Unit), (OrgRecordKind.Branch, PolicyRules.Branch), (OrgRecordKind.Location, PolicyRules.Location) })
        {
            var ids = rules.SelectMany(r => PolicyRules.Ids(r, attribute)).Distinct().ToList();
            var records = await org.FindAsync(kind, ids, ct).ConfigureAwait(false);
            unknown.UnionWith(ids.Except(records.Where(r => r.CompanyId == companyId).Select(r => r.Id)));
        }

        return unknown;
    }

    /// <summary>A custom condition names an active field of the employment of a type rules can compare, with values of that type.</summary>
    private static string? CustomProblem(PolicyRule rule, Dictionary<string, CustomFieldInfo> fields)
    {
        foreach (var c in rule.Conditions.Where(c => c.Attribute == PolicyRules.Custom))
        {
            if (!fields.TryGetValue(c.Field!, out var field))
            {
                return PolicyErrors.CustomUnknown;
            }

            if (field.Status != "active")
            {
                return PolicyErrors.CustomRetired;
            }

            Func<JsonElement, bool>? fits = field.DataType switch
            {
                CustomFieldTypes.Text => v => v.ValueKind == JsonValueKind.String,
                CustomFieldTypes.Choice => v => v.ValueKind == JsonValueKind.String && field.Options.Any(o => o.Code == v.GetString()),
                CustomFieldTypes.Bool => v => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                CustomFieldTypes.Number => v => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
                CustomFieldTypes.DecimalNumber => v => v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out _),
                CustomFieldTypes.Date => v => v.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                _ => null,
            };
            if (fits is null)
            {
                return PolicyErrors.CustomType;
            }

            if (!c.Values.All(fits))
            {
                return PolicyErrors.CustomValue;
            }
        }

        return null;
    }

    public static void Bump(PolicyVersion row, DateTimeOffset now, Guid actor)
    {
        row.UpdatedAt = now;
        row.UpdatedBy = actor;
        row.RowVersion++;
    }

    public static void Bump(PolicyAssignment row, DateTimeOffset now, Guid actor)
    {
        row.UpdatedAt = now;
        row.UpdatedBy = actor;
        row.RowVersion++;
    }

    public static void Bump(Policy row, DateTimeOffset now, Guid actor)
    {
        row.UpdatedAt = now;
        row.UpdatedBy = actor;
        row.RowVersion++;
    }

    /// <summary>The refusal answer of a database rule of the policy tables.</summary>
    public static PeopleResult Refusal(Npgsql.PostgresException pg) => pg.ConstraintName switch
    {
        "uq_policies_company_id_policy_type_code" => PeopleResult.Conflict(PolicyErrors.CodeTaken),
        "uq_policy_versions_policy_id" => PeopleResult.Conflict(PolicyErrors.DraftExists),
        "ck_policy_assignments_precedence_unique" => PeopleResult.Conflict(PolicyErrors.PrecedenceTaken),
        "ck_policy_versions_fixed" or "ck_policy_assignments_fixed" => PeopleResult.Conflict(PolicyErrors.NotDraft),
        _ when pg.SqlState is "40001" or "40P01" => PeopleResult.Conflict(PersonService.Concurrency),
        _ => PeopleResult.Invalid("errors.data.invalid_request"),
    };
}

/// <summary>The employee's ids the view also names (their grade, unit, branch and location).</summary>
internal sealed record PolicyAttributesView(Guid? Grade, Guid? Unit, Guid? Branch, Guid? Location);
