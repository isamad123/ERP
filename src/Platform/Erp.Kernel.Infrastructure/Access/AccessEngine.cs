using System.Linq.Expressions;
using System.Reflection;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Access;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// The one authorization engine (FR-HRM-ROL-02, spec 02 BR "every permission decision uses the same engine"): may the
/// current user do an action, which rows may they see or change, and what may they do with each field. Generated
/// endpoints, hand-written services, exports and later reports, AI and mobile all ask this service.
/// <para>
/// Rules (slice S1.06 rulings Q4, A): permissions are the union of the roles valid today (own and group assignments);
/// a row is visible when a role granting <c>resource.action</c>, assigned for the row's company or for all companies,
/// has a data scope covering it (no scope: nothing, deny by default); a table without locating columns needs the
/// permission only; for a field the most open access among those roles wins.
/// </para>
/// <para>
/// The user's roles are read fresh on every request in one round trip (ruling D), so a change applies on the next call
/// on every API server; dates come from the injected clock.
/// </para>
/// </summary>
public sealed class AccessEngine(ITenantConnectionProvider tenant, IClock clock, ICurrentActor actor, IWorkforceDirectory workforce)
    : IPermissionChecker, IDataScopeFilter, IFieldAccess, ICallerAccess
{
    private static readonly MethodInfo GuidContains = ContainsOf<Guid?>();
    private static readonly MethodInfo TextContains = ContainsOf<string?>();

    private Task<AccessSnapshot>? _snapshot;
    private WorkforcePosition? _position;
    private readonly Dictionary<string, IReadOnlyList<Guid>> _sets = new(StringComparer.Ordinal);

    /// <summary>Today's business date (UTC date of the injected clock).</summary>
    public DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    internal Task<AccessSnapshot> SnapshotAsync(CancellationToken cancellationToken) => _snapshot ??= LoadAsync(cancellationToken);

    private async Task<AccessSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await AccessSnapshot.LoadAsync(connection, null, actor.UserId, Today, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ permissions

    public async ValueTask<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken) =>
        (await SnapshotAsync(cancellationToken).ConfigureAwait(false)).Holds(permission);

    public async ValueTask<bool> HoldsAsync(string permission, CancellationToken cancellationToken) =>
        (await SnapshotAsync(cancellationToken).ConfigureAwait(false)).Holds(permission);

    public async ValueTask<bool> CoversCompanyAsync(string permission, Guid? companyId, CancellationToken cancellationToken) =>
        (await SnapshotAsync(cancellationToken).ConfigureAwait(false)).Granting(permission)
            .Any(g => g.Assignment.CompanyId is null || (companyId is not null && g.Assignment.CompanyId == companyId));

    /// <summary>Every permission the user holds today (for "cannot grant what you do not hold").</summary>
    public async Task<IReadOnlySet<string>> PermissionsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Assignments.SelectMany(a => snapshot.Roles[a.RoleId].Permissions).ToHashSet(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ data scopes

    public async ValueTask<IQueryable<T>> ApplyAsync<T>(IQueryable<T> query, ScopeTarget<T> target, string action, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        var predicate = await PredicateAsync(target, action, cancellationToken).ConfigureAwait(false);
        return predicate is null ? query : query.Where(predicate);
    }

    public async ValueTask<bool> AllowsAsync<T>(T row, ScopeTarget<T> target, string action, CancellationToken cancellationToken)
        where T : class
    {
        var predicate = await PredicateAsync(target, action, cancellationToken).ConfigureAwait(false);
        return predicate is null || predicate.Compile()(row);
    }

    /// <summary>The row filter for the user, resource and action; null means every row (no filter needed).</summary>
    public async Task<Expression<Func<T, bool>>?> PredicateAsync<T>(ScopeTarget<T> target, string action, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var grants = snapshot.Granting(target.Resource + "." + action).ToList();
        var e = Expression.Parameter(typeof(T), "e");
        if (grants.Count == 0)
        {
            return Expression.Lambda<Func<T, bool>>(Expression.Constant(false), e);
        }

        if (!target.IsScoped)
        {
            return null;
        }

        var builder = new PredicateBuilder<T>(this, target, e);
        Expression? any = null;
        foreach (var (assignment, role) in grants)
        {
            var scope = role.ScopeFor(target.Resource);
            if (scope is null)
            {
                continue; // deny by default: a role without a scope for a scoped resource adds no rows
            }

            var clause = await builder.GrantAsync(assignment.CompanyId, scope, cancellationToken).ConfigureAwait(false);
            if (clause is ConstantExpression { Value: true })
            {
                return null;
            }

            if (clause is ConstantExpression { Value: false })
            {
                continue;
            }

            any = any is null ? clause : Expression.OrElse(any, clause);
        }

        return Expression.Lambda<Func<T, bool>>(any ?? Expression.Constant(false), e);
    }

    // ------------------------------------------------------------------ field security

    public async ValueTask<FieldAccessMap> ForAsync(FieldCatalog catalog, string action, Guid? companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var granting = snapshot.Granting(catalog.Resource + "." + action).ToList();
        var roles = granting
            .Where(g => g.Assignment.CompanyId is null || companyId is null || g.Assignment.CompanyId == companyId)
            .Select(g => g.Role)
            .Distinct()
            .ToList();

        // No role covers that company: the row is outside the caller's scope and the data-scope check refuses the write
        // (403 out of scope); judging its fields by the caller's other roles keeps that the answer, not a field error.
        return Merge(catalog, roles.Count > 0 ? roles : granting.Select(g => g.Role).Distinct().ToList());
    }

    /// <summary>The fields of a resource as the given roles see them together (most open wins, Q4).</summary>
    internal static FieldAccessMap Merge(FieldCatalog catalog, IReadOnlyCollection<RoleRules> roles)
    {
        var levels = new Dictionary<string, FieldAccessLevel>(StringComparer.Ordinal);
        foreach (var field in catalog.Fields)
        {
            levels[field.Key] = roles.Count == 0 ? FieldAccessLevel.Hidden : roles.Max(r => r.FieldLevel(catalog.Resource, field.Key, field.FieldClass));
        }

        // Custom fields with their own rule in any of the roles (FR-KRN-MET-02).
        var customKeys = roles.SelectMany(r => r.Fields.Keys)
            .Where(k => k.Resource == catalog.Resource && k.Field.StartsWith(FieldAccessMap.CustomPrefix, StringComparison.Ordinal))
            .Select(k => k.Field)
            .Distinct(StringComparer.Ordinal);
        foreach (var key in customKeys)
        {
            levels[key] = roles.Max(r => r.FieldLevel(catalog.Resource, key, null));
        }

        return new FieldAccessMap(levels);
    }

    // ------------------------------------------------------------------ id sets (workforce answers, read once per request)

    internal async Task<WorkforcePosition> PositionAsync(CancellationToken cancellationToken) =>
        _position ??= await workforce.PositionOfUserAsync(actor.UserId, Today, cancellationToken).ConfigureAwait(false);

    internal Guid UserId => actor.UserId;

    internal async Task<IReadOnlyList<Guid>> SetAsync(string key, Func<Task<IReadOnlyList<Guid>>> load)
    {
        if (!_sets.TryGetValue(key, out var set))
        {
            set = await load().ConfigureAwait(false);
            _sets[key] = set;
        }

        return set;
    }

    internal Task<IReadOnlyList<Guid>> DirectReportsAsync(CancellationToken ct) =>
        SetAsync("team", async () => await workforce.DirectReportsAsync((await PositionAsync(ct).ConfigureAwait(false)).EmploymentIds.ToList(), Today, ct).ConfigureAwait(false));

    internal Task<IReadOnlyList<Guid>> SubtreeUnitsAsync(CancellationToken ct) =>
        SetAsync("subtree-units", async () => await DescendantsAsync((await PositionAsync(ct).ConfigureAwait(false)).OrgUnitIds, ct).ConfigureAwait(false));

    internal Task<IReadOnlyList<Guid>> SubtreeEmploymentsAsync(CancellationToken ct) =>
        SetAsync("subtree", async () => await workforce.InOrgUnitsAsync((await SubtreeUnitsAsync(ct).ConfigureAwait(false)).ToList(), Today, ct).ConfigureAwait(false));

    internal Task<IReadOnlyList<Guid>> UnitsAsync(IReadOnlyList<Guid> ids, bool children, CancellationToken ct) =>
        children ? SetAsync("units:" + Key(ids), () => DescendantsAsync(ids, ct)) : Task.FromResult(ids);

    internal Task<IReadOnlyList<Guid>> InOrgUnitsAsync(IReadOnlyList<Guid> units, CancellationToken ct) =>
        SetAsync("in-units:" + Key(units), () => workforce.InOrgUnitsAsync(units.ToList(), Today, ct));

    internal Task<IReadOnlyList<Guid>> InBranchesAsync(IReadOnlyList<Guid> branches, CancellationToken ct) =>
        SetAsync("in-branches:" + Key(branches), () => workforce.InBranchesAsync(branches.ToList(), Today, ct));

    internal Task<IReadOnlyList<Guid>> InCompaniesAsync(IReadOnlyList<Guid> companies, CancellationToken ct) =>
        SetAsync("in-companies:" + Key(companies), () => workforce.InCompaniesAsync(companies.ToList(), ct));

    internal Task<IReadOnlyList<Guid>> PersonsOfAsync(IReadOnlyList<Guid> employments, CancellationToken ct) =>
        SetAsync("persons:" + Key(employments), () => workforce.PersonsOfAsync(employments.ToList(), ct));

    private static string Key(IReadOnlyList<Guid> ids) => string.Join(',', ids.Order());

    /// <summary>
    /// The org units and every unit below them in the reporting hierarchies, as of today (org.org_unit_links, a kernel
    /// table). UNION (not UNION ALL) makes the recursion stop even if links form a cycle (cycle check: S1.14).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> DescendantsAsync(IReadOnlyList<Guid> roots, CancellationToken cancellationToken)
    {
        if (roots.Count == 0)
        {
            return [];
        }

        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            with recursive tree(unit_id) as (
                select unnest($1::uuid[])
                union
                select l.unit_id from org.org_unit_links l
                join org.org_hierarchies h on h.id = l.hierarchy_id and h.kind = 'reporting'
                join tree t on l.parent_unit_id = t.unit_id
                where l.valid_from <= $2 and (l.valid_to is null or l.valid_to > $2)
            )
            select unit_id from tree
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = roots.ToArray() });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = Today });
        var units = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            units.Add(reader.GetGuid(0));
        }

        return units;
    }

    private static MethodInfo ContainsOf<TItem>() =>
        typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(TItem));

    /// <summary>A set of ids the query reads as one parameter (EF Core turns the field access into <c>= any(@p)</c>).</summary>
    private sealed class IdSet(IEnumerable<Guid> ids)
    {
        public readonly Guid?[] Values = ids.Select(i => (Guid?)i).Distinct().ToArray();
    }

    private sealed class TextSet(IEnumerable<string> values)
    {
        public readonly string?[] Values = values.Select(v => (string?)v).ToArray();
    }

    /// <summary>Turns one grant (assignment company + data scope) into a filter over the table's locating columns.</summary>
    private sealed class PredicateBuilder<T>(AccessEngine engine, ScopeTarget<T> target, ParameterExpression e)
        where T : class
    {
        private static readonly Expression True = Expression.Constant(true);
        private static readonly Expression False = Expression.Constant(false);

        private bool Has(ScopeDimension dimension) => target.Locators.ContainsKey(dimension);

        private Expression Match(ScopeDimension dimension, List<Guid> ids)
        {
            if (ids.Count == 0)
            {
                return False;
            }

            var locator = target.Locators[dimension];
            var body = new Rebind(locator.Parameters[0], e).Visit(locator.Body);
            var set = Expression.Field(Expression.Constant(new IdSet(ids)), nameof(IdSet.Values));
            return Expression.Call(GuidContains, set, body);
        }

        private static Expression And(Expression a, Expression b) =>
            a == False || b == False ? False : a == True ? b : b == True ? a : Expression.AndAlso(a, b);

        private static Expression Or(Expression a, Expression b) =>
            a == True || b == True ? True : a == False ? b : b == False ? a : Expression.OrElse(a, b);

        public async Task<Expression> GrantAsync(Guid? assignmentCompany, ScopeRule scope, CancellationToken ct)
        {
            var company = assignmentCompany is { } c ? await CompaniesAsync([c], noDimension: True, ct).ConfigureAwait(false) : True;
            if (company == False)
            {
                return False;
            }

            var kind = await KindAsync(scope, ct).ConfigureAwait(false);
            return And(And(company, kind), Conditions(scope));
        }

        private async Task<Expression> KindAsync(ScopeRule scope, CancellationToken ct)
        {
            switch (scope.Kind)
            {
                case ScopeRule.All:
                    return True;
                case ScopeRule.Own:
                {
                    var me = await engine.PositionAsync(ct).ConfigureAwait(false);
                    var own = False;
                    if (Has(ScopeDimension.Employment))
                    {
                        own = Or(own, Match(ScopeDimension.Employment, me.EmploymentIds.ToList()));
                    }

                    if (Has(ScopeDimension.Person))
                    {
                        own = Or(own, Match(ScopeDimension.Person, me.PersonIds.ToList()));
                    }

                    if (Has(ScopeDimension.User))
                    {
                        own = Or(own, Match(ScopeDimension.User, [engine.UserId]));
                    }

                    return own;
                }

                case ScopeRule.DirectTeam:
                    return await PeopleAsync(await engine.DirectReportsAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                case ScopeRule.Subtree:
                    if (Has(ScopeDimension.Employment) || Has(ScopeDimension.Person))
                    {
                        return await PeopleAsync(await engine.SubtreeEmploymentsAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    }

                    return Has(ScopeDimension.OrgUnit) ? Match(ScopeDimension.OrgUnit, (await engine.SubtreeUnitsAsync(ct).ConfigureAwait(false)).ToList()) : False;
                case ScopeRule.OrgUnits:
                {
                    var units = await engine.UnitsAsync(scope.Ids, scope.IncludeChildren, ct).ConfigureAwait(false);
                    return Has(ScopeDimension.OrgUnit)
                        ? Match(ScopeDimension.OrgUnit, units.ToList())
                        : await PeopleAsync(await engine.InOrgUnitsAsync(units, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                }

                case ScopeRule.Branches:
                    return Has(ScopeDimension.Branch)
                        ? Match(ScopeDimension.Branch, scope.Ids.ToList())
                        : await PeopleAsync(await engine.InBranchesAsync(scope.Ids, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                case ScopeRule.Companies:
                    return await CompaniesAsync(scope.Ids, noDimension: False, ct).ConfigureAwait(false);
                default:
                    return False;
            }
        }

        /// <summary>Rows of the given employments: by the employment column, else by the person column.</summary>
        private async Task<Expression> PeopleAsync(IReadOnlyList<Guid> employments, CancellationToken ct)
        {
            if (Has(ScopeDimension.Employment))
            {
                return Match(ScopeDimension.Employment, employments.ToList());
            }

            return Has(ScopeDimension.Person) ? Match(ScopeDimension.Person, (await engine.PersonsOfAsync(employments, ct).ConfigureAwait(false)).ToList()) : False;
        }

        /// <summary>
        /// Rows of the given companies: by the company column, else (person records) by the persons employed there.
        /// <paramref name="noDimension"/> is the answer for tables that belong to no company (ruling A: the permission decides).
        /// </summary>
        private async Task<Expression> CompaniesAsync(IReadOnlyList<Guid> companies, Expression noDimension, CancellationToken ct)
        {
            if (Has(ScopeDimension.Company))
            {
                return Match(ScopeDimension.Company, companies.ToList());
            }

            if (Has(ScopeDimension.Person))
            {
                var employments = await engine.InCompaniesAsync(companies, ct).ConfigureAwait(false);
                return Match(ScopeDimension.Person, (await engine.PersonsOfAsync(employments, ct).ConfigureAwait(false)).ToList());
            }

            return noDimension;
        }

        private Expression Conditions(ScopeRule scope)
        {
            var all = True;
            foreach (var condition in scope.Conditions)
            {
                if (!target.Attributes.TryGetValue(condition.Attribute, out var attribute))
                {
                    return False; // a condition the table cannot test never widens access
                }

                var body = new Rebind(attribute.Parameters[0], e).Visit(attribute.Body);
                var set = Expression.Field(Expression.Constant(new TextSet(condition.Values)), nameof(TextSet.Values));
                all = And(all, Expression.Call(TextContains, set, body));
            }

            return all;
        }
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
