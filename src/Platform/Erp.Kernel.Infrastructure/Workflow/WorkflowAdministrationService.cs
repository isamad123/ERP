using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// Route administration (slice S1.10 build item 2; FR-KRN-WFL-01, FR-KRN-WFL-10; rulings Q2, Q10, R2, R3): one route per
/// document type; drafts are edited, checked and activated; an active version is frozen; activating a new version retires
/// the old one and changes nothing for requests already running. A route of a sensitive document type is activated only
/// after a second administrator approves it (maker-checker, kind <c>wf.version.activate</c>). Activation is refused while
/// the route has a problem, including a step that cannot find any approver (spec 02 section 14 BR).
/// </summary>
public sealed class WorkflowAdministrationService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    ILicensedFeatures licence,
    WorkflowCatalog catalog,
    IApproverDirectory directory,
    ICallerAccess caller)
    : IWorkflowAdministration
{
    public const string DefinitionNotFound = "errors.workflow.definition_not_found";
    public const string VersionNotFound = "errors.workflow.version_not_found";
    public const string VersionChanged = "errors.workflow.version_changed";

    /// <summary>Refusal of a route change by someone whose right to edit routes is limited to some companies (review S1.10a finding 1).</summary>
    public const string AllCompaniesRequired = "errors.workflow.all_companies_required";

    private sealed record DefinitionRow(Guid Id, string Code, string NameJson, string DocumentType, Guid? ActiveVersionId, string Status, bool RequiresApproval, long RowVersion);

    private sealed record VersionRow(Guid Id, Guid DefinitionId, int Version, string Json, string Status, DateTimeOffset? ActivatedAt, Guid? ActivatedBy, long RowVersion);

    public async Task<IReadOnlyList<WorkflowDocumentTypeView>> DocumentTypesAsync(CancellationToken cancellationToken)
    {
        var list = new List<WorkflowDocumentTypeView>();
        foreach (var type in catalog.All.OrderBy(t => t.Code, StringComparer.Ordinal))
        {
            if (await licence.IsLicensedAsync(type.EntitlementKey, cancellationToken).ConfigureAwait(false))
            {
                list.Add(new WorkflowDocumentTypeView(type.Code, type.Module, type.Name, [.. type.Fields.Select(f => new WorkflowFieldView(f.Name, f.Type, f.Label, f.Choices))],
                    type.BulkApproval, type.RequiresApproval, type.DecidePermission));
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<WorkflowDefinitionView>> DefinitionsAsync(CancellationToken cancellationToken) =>
        await ReadAsync(async work =>
        {
            var list = new List<WorkflowDefinitionView>();
            foreach (var row in await DefinitionsAsync(work, "select " + DefinitionColumns + " from wf.workflow_definitions order by code", cancellationToken).ConfigureAwait(false))
            {
                if (await VisibleTypeAsync(row.DocumentType, cancellationToken).ConfigureAwait(false) is { } type)
                {
                    list.Add(await ViewAsync(work, row, type, cancellationToken).ConfigureAwait(false));
                }
            }

            return list;
        }, cancellationToken).ConfigureAwait(false);

    public async Task<WorkflowDefinitionDetail?> DefinitionAsync(Guid id, CancellationToken cancellationToken) =>
        await ReadAsync(async work =>
        {
            if (await DefinitionAsync(work, id, lockRow: false, cancellationToken).ConfigureAwait(false) is not { } row
                || await VisibleTypeAsync(row.DocumentType, cancellationToken).ConfigureAwait(false) is not { } type)
            {
                return null;
            }

            var versions = await VersionsAsync(work, "select " + VersionColumns + " from wf.workflow_versions where definition_id = $1 order by version", cancellationToken, id).ConfigureAwait(false);
            return new WorkflowDefinitionDetail(await ViewAsync(work, row, type, cancellationToken).ConfigureAwait(false), [.. versions.Select(VersionView)]);
        }, cancellationToken).ConfigureAwait(false);

    public Task<WorkflowVersionView> NewDraftAsync(Guid definitionId, NewDraftRequest request, CancellationToken cancellationToken) =>
        WriteAsync(async work =>
        {
            ArgumentNullException.ThrowIfNull(request);
            await RequireAllCompaniesAsync(cancellationToken).ConfigureAwait(false);
            var definition = await DefinitionAsync(work, definitionId, lockRow: true, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException(DefinitionNotFound);
            var type = await VisibleTypeAsync(definition.DocumentType, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException(DefinitionNotFound);
            string json;
            if (request.CopyOf is { } copy)
            {
                var source = await VersionAsync(work, copy, lockRow: false, cancellationToken).ConfigureAwait(false);
                json = source is not null && source.DefinitionId == definitionId ? source.Json : throw new KeyNotFoundException(VersionNotFound);
            }
            else if (request.Route is { ValueKind: not JsonValueKind.Null } route)
            {
                json = route.GetRawText();
            }
            else
            {
                json = (definition.ActiveVersionId is { } active ? (await VersionAsync(work, active, lockRow: false, cancellationToken).ConfigureAwait(false))?.Json : null)
                    ?? throw new WorkflowException("errors.workflow.route_required");
            }

            var stored = Stored(json, type);
            var next = await work.ScalarAsync<int>("select coalesce(max(version), 0) + 1 from wf.workflow_versions where definition_id = $1", cancellationToken, definitionId).ConfigureAwait(false);
            var id = work.NewId();
            await work.ExecuteAsync("""
                insert into wf.workflow_versions (id, definition_id, version, definition, status, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3, $4, 'draft', $5, $6, $5, $6, 1)
                """, cancellationToken, id, definitionId, next, new JsonPayload(stored), work.Now, work.Actor).ConfigureAwait(false);
            work.Change("wf.workflow_versions", id, null, "create", ("definition_id", null, definitionId), ("version", null, next), ("status", null, "draft"));
            return VersionView(new VersionRow(id, definitionId, next, stored, "draft", null, null, 1));
        }, cancellationToken);

    public Task<WorkflowVersionView> EditDraftAsync(Guid versionId, EditDraftRequest request, CancellationToken cancellationToken) =>
        WriteAsync(async work =>
        {
            ArgumentNullException.ThrowIfNull(request);
            await RequireAllCompaniesAsync(cancellationToken).ConfigureAwait(false);
            var rowVersion = request.RowVersion ?? throw new WorkflowException("errors.workflow.row_version_required");
            if (request.Route is not { ValueKind: not JsonValueKind.Null } route)
            {
                throw new WorkflowException("errors.workflow.route_required");
            }

            var (_, version, type) = await LockVersionAsync(work, versionId, cancellationToken).ConfigureAwait(false);
            if (version.Status != "draft")
            {
                throw new WorkflowException("errors.workflow.version_not_draft"); // an active version is frozen (KRN-WFL-10)
            }

            if (version.RowVersion != rowVersion)
            {
                throw new WorkflowException(VersionChanged);
            }

            var stored = Stored(route.GetRawText(), type);
            await work.ExecuteAsync("""
                update wf.workflow_versions set definition = $2, updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1
                """, cancellationToken, versionId, new JsonPayload(stored), work.Now, work.Actor).ConfigureAwait(false);
            using (var before = JsonDocument.Parse(version.Json))
            using (var after = JsonDocument.Parse(stored))
            {
                work.Change("wf.workflow_versions", versionId, null, "update", ("definition", before.RootElement.Clone(), after.RootElement.Clone()));
            }

            return VersionView(version with { Json = stored, RowVersion = version.RowVersion + 1 });
        }, cancellationToken);

    public async Task<RouteCheckView> CheckAsync(Guid versionId, CancellationToken cancellationToken) =>
        await ReadAsync(async work =>
        {
            var version = await VersionAsync(work, versionId, lockRow: false, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException(VersionNotFound);
            var definition = await DefinitionAsync(work, version.DefinitionId, lockRow: false, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException(VersionNotFound);
            var type = await VisibleTypeAsync(definition.DocumentType, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException(VersionNotFound);
            var problems = await ProblemsAsync(work, version.Json, type, cancellationToken).ConfigureAwait(false);
            return new RouteCheckView(problems.Count == 0, [.. problems.Select(p => new RouteProblemView(p.Path, p.ErrorKey))]);
        }, cancellationToken).ConfigureAwait(false);

    public Task<ActivationResult> ActivateAsync(Guid versionId, ActivateVersionRequest request, CancellationToken cancellationToken) =>
        WriteAsync(async work =>
        {
            ArgumentNullException.ThrowIfNull(request);
            await RequireAllCompaniesAsync(cancellationToken).ConfigureAwait(false);
            var rowVersion = request.RowVersion ?? throw new WorkflowException("errors.workflow.row_version_required");
            var (definition, version, type) = await LockVersionAsync(work, versionId, cancellationToken).ConfigureAwait(false);
            await EnsureActivatableAsync(work, definition, version, type, rowVersion, cancellationToken).ConfigureAwait(false);
            if (definition.RequiresApproval || type.RequiresApproval)
            {
                // Ruling Q10: a sensitive document type's route needs a second administrator (S1.06 maker-checker). The
                // document type counts too, so a type marked sensitive after its route was seeded is protected (review
                // S1.10a finding 5, CR-059).
                var change = new AccessChange
                {
                    Kind = AccessChange.ActivateWorkflowVersion,
                    WorkflowVersionId = versionId,
                    WorkflowVersionRowVersion = rowVersion,
                    Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
                };
                var pending = await AccessAdministrationService.SubmitChangeAsync(work.Connection, work.Transaction, change, work.Now, work.Actor, cancellationToken).ConfigureAwait(false);
                return new ActivationResult("pending", null, pending.RequestId);
            }

            var activated = await ApplyActivationAsync(work, definition, version, cancellationToken).ConfigureAwait(false);
            return new ActivationResult("done", VersionView(activated), null);
        }, cancellationToken);

    public Task<WorkflowDefinitionView> DeactivateAsync(Guid definitionId, DeactivateDefinitionRequest request, CancellationToken cancellationToken) =>
        WriteAsync(async work =>
        {
            ArgumentNullException.ThrowIfNull(request);
            await RequireAllCompaniesAsync(cancellationToken).ConfigureAwait(false);
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? throw new WorkflowException("errors.workflow.reason_required") : request.Reason.Trim();
            var rowVersion = request.RowVersion ?? throw new WorkflowException("errors.workflow.row_version_required");
            var definition = await DefinitionAsync(work, definitionId, lockRow: true, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException(DefinitionNotFound);
            var type = await VisibleTypeAsync(definition.DocumentType, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException(DefinitionNotFound);
            if (definition.RowVersion != rowVersion)
            {
                throw new WorkflowException(VersionChanged);
            }

            if (definition.Status != "active")
            {
                throw new WorkflowException("errors.workflow.definition_not_active");
            }

            await work.ExecuteAsync("""
                update wf.workflow_definitions set status = 'inactive', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1
                """, cancellationToken, definitionId, work.Now, work.Actor).ConfigureAwait(false);
            work.Change("wf.workflow_definitions", definitionId, null, "status", ("status", "active", "inactive"));
            await work.RaiseAsync(WorkflowEvents.DefinitionDeactivated, "wf.workflow_definition", definitionId, new { definition_id = definitionId, document_type = definition.DocumentType, reason }, cancellationToken).ConfigureAwait(false);
            return await ViewAsync(work, definition with { Status = "inactive", RowVersion = definition.RowVersion + 1 }, type, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    // ================================================================== maker-checker (AccessAdministrationService)

    /// <summary>
    /// Checks a held activation when the second administrator approves it: the draft is unchanged since the request
    /// (the same row version) and the route still passes every check. Returns a localization key when refused.
    /// </summary>
    internal async Task<string?> ValidateActivationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid versionId, long rowVersion, CancellationToken ct)
    {
        var work = new WorkflowWork(connection, transaction, clock.UtcNow, actor.UserId);
        try
        {
            var (definition, version, type) = await LockVersionAsync(work, versionId, ct).ConfigureAwait(false);
            await EnsureActivatableAsync(work, definition, version, type, rowVersion, ct).ConfigureAwait(false);
            return null;
        }
        catch (KeyNotFoundException)
        {
            return VersionNotFound;
        }
        catch (WorkflowException ex)
        {
            return ex.ErrorKey;
        }
    }

    /// <summary>Applies an approved activation in the approval's transaction (after <see cref="ValidateActivationAsync"/>).</summary>
    internal static async Task<Guid> ApplyHeldActivationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid versionId, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var work = new WorkflowWork(connection, transaction, now, by);
        var version = await VersionAsync(work, versionId, lockRow: true, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(VersionNotFound);
        var definition = await DefinitionAsync(work, version.DefinitionId, lockRow: true, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(VersionNotFound);
        await ApplyActivationAsync(work, definition, version, ct).ConfigureAwait(false);
        await work.FlushAsync(ct).ConfigureAwait(false);
        return versionId;
    }

    // ================================================================== rules

    /// <summary>
    /// A route applies to every company of the customer (ruling Q10), so changing one (new draft, edit, activate,
    /// deactivate) needs <c>wf.workflow_definitions.update</c> granted for all companies, as the second person of a held
    /// activation already must (review S1.10a finding 1, CR-059). Reading routes stays "any company" (ruling Q14).
    /// </summary>
    private async Task RequireAllCompaniesAsync(CancellationToken ct)
    {
        if (!await caller.CoversCompanyAsync(WorkflowPermissions.RoutesUpdate, null, ct).ConfigureAwait(false))
        {
            throw new WorkflowException(AllCompaniesRequired);
        }
    }

    private async Task EnsureActivatableAsync(WorkflowWork work, DefinitionRow definition, VersionRow version, WorkflowDocumentType type, long rowVersion, CancellationToken ct)
    {
        if (version.Status != "draft")
        {
            throw new WorkflowException("errors.workflow.version_not_draft");
        }

        if (version.RowVersion != rowVersion)
        {
            throw new WorkflowException(VersionChanged);
        }

        _ = definition;
        var problems = await ProblemsAsync(work, version.Json, type, ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new WorkflowException("errors.workflow.route_not_ready", [.. problems.Select(p => p.ToString())]);
        }
    }

    /// <summary>The old active version retires; the draft becomes active; the route points at it and is active again (ruling R3).</summary>
    private static async Task<VersionRow> ApplyActivationAsync(WorkflowWork work, DefinitionRow definition, VersionRow version, CancellationToken ct)
    {
        if (definition.ActiveVersionId is { } old && old != version.Id)
        {
            await work.ExecuteAsync("""
                update wf.workflow_versions set status = 'retired', updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1 and status = 'active'
                """, ct, old, work.Now, work.Actor).ConfigureAwait(false);
            work.Change("wf.workflow_versions", old, null, "status", ("status", "active", "retired"));
        }

        await work.ExecuteAsync("""
            update wf.workflow_versions set status = 'active', activated_at = $2, activated_by = $3, updated_at = $2, updated_by = $3, row_version = row_version + 1 where id = $1
            """, ct, version.Id, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_versions", version.Id, null, "status", ("status", "draft", "active"), ("activated_by", null, work.Actor));
        await work.ExecuteAsync("""
            update wf.workflow_definitions set active_version_id = $2, status = 'active', updated_at = $3, updated_by = $4, row_version = row_version + 1 where id = $1
            """, ct, definition.Id, version.Id, work.Now, work.Actor).ConfigureAwait(false);
        work.Change("wf.workflow_definitions", definition.Id, null, "update", ("active_version_id", definition.ActiveVersionId, version.Id), ("status", definition.Status, "active"));
        await work.RaiseAsync(WorkflowEvents.VersionActivated, "wf.workflow_version", version.Id,
            new { definition_id = definition.Id, document_type = definition.DocumentType, version = version.Version, retired_version_id = definition.ActiveVersionId }, ct).ConfigureAwait(false);
        return version with { Status = "active", ActivatedAt = work.Now, ActivatedBy = work.Actor, RowVersion = version.RowVersion + 1 };
    }

    /// <summary>
    /// Every problem of a route for this customer: the static ones (format, fields, operators, ruling R2) and the dynamic
    /// ones: named users active, roles existing with at least one holder, directory kinds the installed modules can answer.
    /// </summary>
    private async Task<List<RouteProblem>> ProblemsAsync(WorkflowWork work, string json, WorkflowDocumentType type, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var (route, problems) = RouteFormat.Parse(doc.RootElement, catalog.Fields(type.Code), type.BulkApproval);
        var list = problems.ToList();
        if (route is null)
        {
            return list;
        }

        for (var i = 0; i < route.Steps.Count; i++)
        {
            var step = route.Steps[i];
            foreach (var (spec, part) in new[] { (step.Approver, "approver"), (step.Fallback, "fallback"), (step.Escalation, "escalation") })
            {
                if (spec is not null && await SpecProblemAsync(work, spec, type, ct).ConfigureAwait(false) is { } problem)
                {
                    list.Add(new RouteProblem($"steps[{i}].{part}", problem));
                }
            }
        }

        return list;
    }

    /// <summary>
    /// What stops an approver specification from deciding requests of the type for this customer, or null. Named users
    /// must be active and hold the right to decide (<c>wf.workflow_tasks.update</c> and the type's own decide permission)
    /// for at least one company; a role must exist, have a holder and grant both permissions (review S1.10a finding 2,
    /// CR-059); directory kinds must be answered by an installed module. Document-user and directory approvers are
    /// people known only per request: the engine checks them when it assigns.
    /// </summary>
    private async Task<string?> SpecProblemAsync(WorkflowWork work, ApproverSpec spec, WorkflowDocumentType type, CancellationToken ct)
    {
        switch (spec.Kind)
        {
            case ApproverKinds.User:
                if (!await ApproverResolution.ActiveUserAsync(work, spec.UserId!.Value, ct).ConfigureAwait(false))
                {
                    return "errors.workflow.user_not_active";
                }

                return await ApproverResolution.CanDecideSomewhereAsync(work, spec.UserId.Value, type.DecidePermission, ct).ConfigureAwait(false) ? null : ApproverCannotDecide;
            case ApproverKinds.Role:
                if (await ApproverResolution.RoleIdAsync(work, spec.Role!, ct).ConfigureAwait(false) is not { } role)
                {
                    return "errors.workflow.role_unknown";
                }

                if ((await ApproverResolution.RoleHoldersAsync(work, role, null, ApproverResolution.Today(work), ct).ConfigureAwait(false)).Count == 0)
                {
                    return "errors.workflow.role_no_holder";
                }

                return await ApproverResolution.RoleGrantsAsync(work, role, [WorkflowPermissions.TasksDecide, .. type.DecidePermission is { } own ? [own] : Array.Empty<string>()], ct).ConfigureAwait(false)
                    ? null : ApproverCannotDecide;
            case ApproverKinds.Manager or ApproverKinds.DepartmentHead or ApproverKinds.Position:
                return directory.Supports(spec.Kind) ? null : "errors.workflow.kind_unavailable";
            default:
                return null;
        }
    }

    public const string ApproverCannotDecide = "errors.workflow.approver_cannot_decide";

    /// <summary>Checks and stores a route in its canonical shape; refused with every problem listed.</summary>
    private string Stored(string json, WorkflowDocumentType type)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new WorkflowException("errors.workflow.route_format");
        }

        using (doc)
        {
            var (route, problems) = RouteFormat.Parse(doc.RootElement, catalog.Fields(type.Code), type.BulkApproval);
            return route is null ? throw new WorkflowException("errors.workflow.route_invalid", [.. problems.Select(p => p.ToString())]) : RouteFormat.Serialize(route);
        }
    }

    // ================================================================== reading

    private const string DefinitionColumns = "id, code, name_i18n::text, document_type, active_version_id, status, requires_approval, row_version";

    private const string VersionColumns = "id, definition_id, version, definition::text, status, activated_at, activated_by, row_version";

    private async Task<WorkflowDocumentType?> VisibleTypeAsync(string code, CancellationToken ct) =>
        catalog.Find(code) is { } type && await licence.IsLicensedAsync(type.EntitlementKey, ct).ConfigureAwait(false) ? type : null;

    private async Task<WorkflowDefinitionView> ViewAsync(WorkflowWork work, DefinitionRow row, WorkflowDocumentType type, CancellationToken ct)
    {
        List<RouteProblem> problems;
        int? active = null;
        if (row.Status != "active" || row.ActiveVersionId is null || await VersionAsync(work, row.ActiveVersionId.Value, lockRow: false, ct).ConfigureAwait(false) is not { } version)
        {
            problems = [new RouteProblem("route", "errors.workflow.no_active_route")];
        }
        else
        {
            active = version.Version;
            problems = await ProblemsAsync(work, version.Json, type, ct).ConfigureAwait(false);
        }

        var name = JsonSerializer.Deserialize<Dictionary<string, string>>(row.NameJson) ?? [];
        return new WorkflowDefinitionView(row.Id, row.Code, row.DocumentType, name, row.Status, row.ActiveVersionId, active, row.RequiresApproval, row.RowVersion,
            problems.Count == 0, [.. problems.Select(p => new RouteProblemView(p.Path, p.ErrorKey))]);
    }

    private static WorkflowVersionView VersionView(VersionRow v)
    {
        using var doc = JsonDocument.Parse(v.Json);
        return new WorkflowVersionView(v.Id, v.DefinitionId, v.Version, v.Status, doc.RootElement.Clone(), v.ActivatedAt, v.ActivatedBy, v.RowVersion);
    }

    private async Task<(DefinitionRow Definition, VersionRow Version, WorkflowDocumentType Type)> LockVersionAsync(WorkflowWork work, Guid versionId, CancellationToken ct)
    {
        var definitionId = await work.ScalarAsync<Guid?>("select definition_id from wf.workflow_versions where id = $1", ct, versionId).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(VersionNotFound);
        var definition = await DefinitionAsync(work, definitionId, lockRow: true, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(VersionNotFound);
        var version = await VersionAsync(work, versionId, lockRow: true, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(VersionNotFound);
        var type = await VisibleTypeAsync(definition.DocumentType, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(VersionNotFound);
        return (definition, version, type);
    }

    private static async Task<DefinitionRow?> DefinitionAsync(WorkflowWork work, Guid id, bool lockRow, CancellationToken ct) =>
        (await DefinitionsAsync(work, "select " + DefinitionColumns + " from wf.workflow_definitions where id = $1" + (lockRow ? " for update" : string.Empty), ct, id).ConfigureAwait(false)).FirstOrDefault();

    private static async Task<List<DefinitionRow>> DefinitionsAsync(WorkflowWork work, string sql, CancellationToken ct, params object?[] values)
    {
        var list = new List<DefinitionRow>();
        await using var command = work.Command(sql, values);
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DefinitionRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), WorkflowWork.NullableGuid(r, 4), r.GetString(5), r.GetBoolean(6), r.GetInt64(7)));
        }

        return list;
    }

    private static async Task<VersionRow?> VersionAsync(WorkflowWork work, Guid id, bool lockRow, CancellationToken ct) =>
        (await VersionsAsync(work, "select " + VersionColumns + " from wf.workflow_versions where id = $1" + (lockRow ? " for update" : string.Empty), ct, id).ConfigureAwait(false)).FirstOrDefault();

    private static async Task<List<VersionRow>> VersionsAsync(WorkflowWork work, string sql, CancellationToken ct, params object?[] values)
    {
        var list = new List<VersionRow>();
        await using var command = work.Command(sql, values);
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new VersionRow(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), WorkflowWork.NullableTime(r, 5), WorkflowWork.NullableGuid(r, 6), r.GetInt64(7)));
        }

        return list;
    }

    private async Task<T> ReadAsync<T>(Func<WorkflowWork, Task<T>> read, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        return await read(new WorkflowWork(c, tx, clock.UtcNow, actor.UserId)).ConfigureAwait(false);
    }

    private async Task<T> WriteAsync<T>(Func<WorkflowWork, Task<T>> write, CancellationToken ct)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var work = new WorkflowWork(c, tx, clock.UtcNow, actor.UserId);
        var result = await write(work).ConfigureAwait(false);
        await work.FlushAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }
}
