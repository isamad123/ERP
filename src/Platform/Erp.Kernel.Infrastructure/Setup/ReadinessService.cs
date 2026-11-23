using System.Data;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Generated.Org;
using Erp.Kernel.Contracts.Setup;
using Erp.Kernel.Domain.Generated.Cfg;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Setup;

/// <summary>
/// The checklist and go-live service (slice S1.18a Build items 2 and 3, rulings Q2 and Q4, CR-074). Items with a check are decided by
/// the check each time the checklist opens (the result is stored, so a tick or a lost tick shows in the change log); manual items and
/// steps are ticked, skipped and re-opened by a person. Every change goes through the kernel context, so the change log records it
/// with who and when (S1.07). A company outside the caller's data scope is treated as unknown.
/// </summary>
public sealed class ReadinessService(KernelDbContext db, ICompanyRepository companies, SetupCatalog catalog, IEnumerable<IReadinessCheck> checks, IClock clock, ICurrentActor actor) : IReadinessService
{
    private const int ReasonLength = 500;
    private readonly Dictionary<string, IReadinessCheck> _checks = checks.ToDictionary(c => c.Name, StringComparer.Ordinal);

    public async Task<ReadinessOverview> OverviewAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        await RequireCompanyAsync(companyId, cancellationToken).ConfigureAwait(false);
        var rows = await LoadAsync(companyId, cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<(string, string), ReadinessResult>();
        var now = clock.UtcNow;
        foreach (var spec in Specs().Where(s => s.Check is not null && (companyId is not null || !s.CompanyLevel)))
        {
            var result = await RunAsync(spec, companyId, cancellationToken).ConfigureAwait(false);
            results[(spec.ModuleId, spec.Key)] = result;
            var row = rows[(spec.ModuleId, spec.Key)];
            if (result.Done && row.Status != SetupStatuses.Done)
            {
                Mark(row, SetupStatuses.Done, null, now);
            }
            else if (!result.Done && row.Status == SetupStatuses.Done)
            {
                Mark(row, SetupStatuses.Pending, null, now);
            }
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        var live = await LiveRowsAsync(companyId, cancellationToken).ConfigureAwait(false);
        var steps = new List<SetupTaskView>();
        var modules = new List<ModuleReadiness>();
        foreach (var module in catalog.Modules.OrderBy(m => m.ModuleId == "kernel" ? 0 : 1).ThenBy(m => m.ModuleId, StringComparer.Ordinal))
        {
            var views = module.Tasks.Where(t => companyId is not null || !t.CompanyLevel).OrderBy(t => t.Order)
                .Select(t => View(t, rows[(t.ModuleId, t.Key)], results.GetValueOrDefault((t.ModuleId, t.Key)))).ToList();
            steps.AddRange(views.Where(v => v.Kind == SetupTaskKinds.Step));
            var items = views.Where(v => v.Kind == SetupTaskKinds.Item).ToList();
            if (items.Count == 0 && module.GoLiveActions.Count == 0)
            {
                continue;
            }

            live.TryGetValue(module.ModuleId, out var liveRow);
            modules.Add(new ModuleReadiness(
                module.ModuleId,
                module.NameKey,
                items.Count(i => i.Required),
                items.Count(i => i.Required && i.Status == SetupStatuses.Done),
                items.Count(i => !i.Required),
                items.Count(i => !i.Required && i.Status == SetupStatuses.Done),
                liveRow is not null,
                liveRow?.CompletedAt,
                liveRow?.Note,
                module.GoLiveActions,
                items));
        }

        return new ReadinessOverview(companyId, steps, modules);
    }

    public Task<SetupTaskView> CompleteAsync(string moduleId, string taskKey, SetupTaskChange change, CancellationToken cancellationToken) =>
        ChangeAsync(moduleId, taskKey, change, SetupStatuses.Done, cancellationToken);

    public Task<SetupTaskView> SkipAsync(string moduleId, string taskKey, SetupTaskChange change, CancellationToken cancellationToken) =>
        ChangeAsync(moduleId, taskKey, change, SetupStatuses.Skipped, cancellationToken);

    public Task<SetupTaskView> ReopenAsync(string moduleId, string taskKey, SetupTaskChange change, CancellationToken cancellationToken) =>
        ChangeAsync(moduleId, taskKey, change, SetupStatuses.Pending, cancellationToken);

    public async Task<GoLiveResult> GoLiveAsync(string moduleId, GoLiveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var module = catalog.Find(moduleId ?? string.Empty) ?? throw new SetupRefusedException(404, "errors.setup.module_unknown");
        await RequireCompanyAsync(request.CompanyId, cancellationToken).ConfigureAwait(false);
        var reason = request.OverrideReason?.Trim();
        if (reason is { Length: > ReasonLength })
        {
            throw new SetupRefusedException(400, "errors.setup.reason_too_long", "overrideReason");
        }

        var existing = await db.Set<SetupTask>().FirstOrDefaultAsync(t => t.ModuleId == moduleId && t.TaskKey == SetupTaskKinds.GoLiveKey && t.CompanyId == request.CompanyId, cancellationToken).ConfigureAwait(false);
        if (existing is { Status: SetupStatuses.Done })
        {
            throw new SetupRefusedException(409, "errors.setup.already_live");
        }

        // Run the checks again now: go-live is decided on the present state, not on the last time the checklist was opened.
        var overview = await OverviewAsync(request.CompanyId, cancellationToken).ConfigureAwait(false);
        var open = overview.Modules.FirstOrDefault(m => m.ModuleId == moduleId)?.Items.Count(i => i.Required && i.Status != SetupStatuses.Done) ?? 0;
        if (open > 0 && string.IsNullOrEmpty(reason))
        {
            throw new SetupRefusedException(400, "errors.setup.reason_required", "overrideReason");
        }

        var now = clock.UtcNow;
        var overridden = open > 0;
        if (existing is null)
        {
            db.Add(new SetupTask
            {
                Id = Guid.CreateVersion7(now),
                ModuleId = module.ModuleId,
                TaskKey = SetupTaskKinds.GoLiveKey,
                CompanyId = request.CompanyId,
                IsRequired = false,
                Status = SetupStatuses.Done,
                CompletedBy = actor.UserId,
                CompletedAt = now,
                Note = overridden ? reason : null,
                CreatedAt = now,
                CreatedBy = actor.UserId,
                UpdatedAt = now,
                UpdatedBy = actor.UserId,
                RowVersion = 1,
            });
        }
        else
        {
            existing.Status = SetupStatuses.Done;
            existing.CompletedBy = actor.UserId;
            existing.CompletedAt = now;
            existing.Note = overridden ? reason : null;
            existing.UpdatedAt = now;
            existing.UpdatedBy = actor.UserId;
            existing.RowVersion++;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return new GoLiveResult(module.ModuleId, request.CompanyId, now, overridden, open);
    }

    private async Task<SetupTaskView> ChangeAsync(string moduleId, string taskKey, SetupTaskChange change, string target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var spec = Specs().FirstOrDefault(s => s.ModuleId == moduleId && s.Key == taskKey) ?? throw new SetupRefusedException(404, "errors.setup.task_unknown");
        if (spec.CompanyLevel ? change.CompanyId is null : change.CompanyId is not null)
        {
            throw new SetupRefusedException(400, spec.CompanyLevel ? "errors.setup.company_required" : "errors.setup.company_not_allowed", "companyId");
        }

        var note = change.Note?.Trim();
        if (note is { Length: > ReasonLength })
        {
            throw new SetupRefusedException(400, "errors.setup.note_too_long", "note");
        }

        await RequireCompanyAsync(change.CompanyId, cancellationToken).ConfigureAwait(false);
        var rows = await LoadAsync(change.CompanyId, cancellationToken).ConfigureAwait(false);
        var row = rows[(spec.ModuleId, spec.Key)];
        switch (target)
        {
            case SetupStatuses.Done when spec.Check is not null:
                throw new SetupRefusedException(409, "errors.setup.decided_by_check");
            case SetupStatuses.Skipped when spec.Kind != SetupTaskKinds.Step:
                throw new SetupRefusedException(400, "errors.setup.only_steps_skip");
            case SetupStatuses.Skipped when row.Status == SetupStatuses.Done:
                throw new SetupRefusedException(409, "errors.setup.already_done");
            case SetupStatuses.Pending when spec.Check is not null && row.Status == SetupStatuses.Done:
                throw new SetupRefusedException(409, "errors.setup.decided_by_check");
        }

        if (row.Status != target || row.Note != note)
        {
            Mark(row, target, target == SetupStatuses.Pending ? null : note, clock.UtcNow);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return View(spec, row, null);
    }

    private void Mark(SetupTask row, string status, string? note, DateTimeOffset now)
    {
        row.Status = status;
        row.Note = note;
        row.CompletedBy = status == SetupStatuses.Pending ? null : actor.UserId;
        row.CompletedAt = status == SetupStatuses.Pending ? null : now;
        row.UpdatedAt = now;
        row.UpdatedBy = actor.UserId;
        row.RowVersion++;
    }

    private IEnumerable<SetupTaskSpec> Specs() => catalog.Modules.SelectMany(m => m.Tasks);

    private async Task<ReadinessResult> RunAsync(SetupTaskSpec spec, Guid? companyId, CancellationToken cancellationToken)
    {
        if (!_checks.TryGetValue(spec.Check!, out var check))
        {
            return ReadinessResult.Open("setup.messages.check_missing");
        }

        try
        {
            return await check.CheckAsync(spec.CompanyLevel ? companyId : null, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a check that cannot run shows its item as open, never as done; the checklist still opens
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return ReadinessResult.Open("setup.messages.check_failed");
        }
    }

    private static SetupTaskView View(SetupTaskSpec spec, SetupTask row, ReadinessResult? result) =>
        new(spec.ModuleId, spec.Key, spec.Kind, spec.TitleKey, spec.Required && spec.Kind == SetupTaskKinds.Item, spec.Level, spec.Link, row.Status,
            spec.Check is null, row.Status == SetupStatuses.Done ? null : result?.MessageKey, row.Status == SetupStatuses.Done ? null : result?.Arguments, row.Note, row.CompletedBy, row.CompletedAt, spec.Order);

    private async Task RequireCompanyAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (companyId is { } id && await companies.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new SetupRefusedException(404, "errors.setup.company_unknown", "companyId");
        }
    }

    /// <summary>The rows of the customer level and of one company, created from the manifests when missing, keyed by module and task.</summary>
    private async Task<Dictionary<(string, string), SetupTask>> LoadAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            var now = clock.UtcNow;
            await SetupTaskSetup.EnsureAsync(connection, transaction, catalog.Modules.Select(m => m with { Tasks = [.. m.Tasks.Where(t => !t.CompanyLevel)] }), now, cancellationToken).ConfigureAwait(false);
            if (companyId is { } id)
            {
                await SetupTaskSetup.EnsureCompanyAsync(connection, transaction, catalog.Modules, id, now, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        var list = await db.Set<SetupTask>().Where(t => t.TaskKey != SetupTaskKinds.GoLiveKey && (t.CompanyId == null || t.CompanyId == companyId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<(string, string), SetupTask>();
        foreach (var row in list)
        {
            map[(row.ModuleId, row.TaskKey)] = row;
        }

        return map;
    }

    private async Task<Dictionary<string, SetupTask>> LiveRowsAsync(Guid? companyId, CancellationToken cancellationToken) =>
        companyId is null
            ? []
            : (await db.Set<SetupTask>().AsNoTracking().Where(t => t.TaskKey == SetupTaskKinds.GoLiveKey && t.CompanyId == companyId && t.Status == SetupStatuses.Done).ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(t => t.ModuleId, StringComparer.Ordinal);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new SetupRefusedException(409, "errors.data.concurrency_conflict");
        }
    }
}

/// <summary>Whether a module has gone live for a company (slice S1.18a, ruling Q4); modules ask it before an action they named in their manifest.</summary>
public sealed class GoLiveGate(KernelDbContext db) : IGoLiveGate
{
    public Task<bool> IsLiveAsync(string moduleId, Guid companyId, CancellationToken cancellationToken) =>
        db.Set<SetupTask>().AsNoTracking().AnyAsync(t => t.ModuleId == moduleId && t.TaskKey == SetupTaskKinds.GoLiveKey && t.CompanyId == companyId && t.Status == SetupStatuses.Done, cancellationToken);
}
