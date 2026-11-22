using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// Held changes of business modules (slice S1.20a, CR-076 S1.20a detail d): a module stores a change as a maker-checker request
/// through <see cref="IHeldChanges"/> and registers an <see cref="IHeldChangeHandler"/> for its kind. Approving and rejecting go
/// through the same endpoints and rules as the kernel's own requests (pending, not expired, never by the person who asked:
/// FR-KRN-IAM-14, FR-XM-32); the permission needed is the handler's, and the handler checks and applies the change in the
/// approval's transaction.
/// </summary>
public sealed partial class AccessAdministrationService
{
    private List<IHeldChangeHandler> HeldHandlers => services.GetServices<IHeldChangeHandler>().ToList();

    private IHeldChangeHandler? HandlerOf(string kind) => HeldHandlers.FirstOrDefault(h => string.Equals(h.Kind, kind, StringComparison.Ordinal));

    /// <summary>The handler of the request's kind when it is a module's held change; null for the kernel's own kinds.</summary>
    private async Task<IHeldChangeHandler?> HeldHandlerOfRequestAsync(Guid requestId, CancellationToken ct)
    {
        if (HeldHandlers.Count == 0)
        {
            return null;
        }

        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        var kind = await IdentityDb.ScalarAsync<string>(c, null, "select action_type from iam.maker_checker_requests where id = $1", ct, requestId).ConfigureAwait(false);
        return kind is null ? null : HandlerOf(kind);
    }

    private Task<AccessResult> DecideHeldAsync(IHeldChangeHandler handler, Guid requestId, string? reason, bool approve, CancellationToken cancellationToken) =>
        RunAsync(handler.ApprovePermission, async (c, tx, now, by) =>
        {
            if (!approve && string.IsNullOrWhiteSpace(reason))
            {
                return AccessResult.Invalid(ReasonRequired);
            }

            if (await LockRequestAsync(c, tx, requestId, cancellationToken).ConfigureAwait(false) is not { } request || request.ActionType != handler.Kind)
            {
                return AccessResult.NotFound;
            }

            if (request.Status != "pending")
            {
                return AccessResult.Invalid(request.Status == "expired" ? Expired : NotPending);
            }

            if (request.RequestedBy == by)
            {
                return AccessResult.Forbidden(OwnRequest); // nobody decides their own request (FR-XM-32; also a database check)
            }

            var requestReason = await IdentityDb.ScalarAsync<string>(c, tx, "select reason from iam.maker_checker_requests where id = $1", cancellationToken, requestId).ConfigureAwait(false);
            if (approve && await handler.ApplyAsync(new HeldChange(requestId, request.ActionType, request.RequestedBy, request.Payload, requestReason), tx, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return problem.Forbidden ? AccessResult.Forbidden(problem.ErrorKey) : AccessResult.Invalid(problem.ErrorKey);
            }

            await IdentityDb.ExecuteAsync(c, tx, """
                update iam.maker_checker_requests set status = $5, decided_by = $2, decided_at = $3, reason = coalesce($4, reason), updated_at = $3, updated_by = $2,
                    row_version = row_version + 1 where id = $1
                """, cancellationToken, requestId, by, now, reason, approve ? "approved" : "rejected").ConfigureAwait(false);
            await AccessDb.RaiseAsync(c, tx, approve ? AccessEvents.ChangeApproved : AccessEvents.ChangeRejected, "iam.maker_checker_request", requestId, by, now,
                new { action = request.ActionType, requested_by = request.RequestedBy, reason }, cancellationToken).ConfigureAwait(false);
            return AccessResult.Done(requestId);
        }, cancellationToken);

    /// <summary>The module kinds the caller may decide (holds the handler's permission), and every module kind.</summary>
    private async Task<(string[] Decidable, string[] All)> HeldKindsAsync(CancellationToken ct)
    {
        var decidable = new List<string>();
        foreach (var handler in HeldHandlers)
        {
            if (await permissions.HasPermissionAsync(handler.ApprovePermission, ct).ConfigureAwait(false))
            {
                decidable.Add(handler.Kind);
            }
        }

        return ([.. decidable], [.. HeldHandlers.Select(h => h.Kind)]);
    }

    /// <summary>A module's pending request in the inbox: described by its handler, with the refusal approving would give now.</summary>
    private static async Task<(IReadOnlyDictionary<string, string>? Subject, IReadOnlyList<ChangeDetail> Details, string? Problem)> DescribeHeldAsync(
        IHeldChangeHandler handler, NpgsqlTransaction tx, Guid id, string kind, Guid by, string payload, string? reason, bool own, CancellationToken ct)
    {
        var change = new HeldChange(id, kind, by, payload, reason);
        var description = await handler.DescribeAsync(change, tx, ct).ConfigureAwait(false);
        var details = description.Details.Select(d => new ChangeDetail(d.Label, d.Type, d.Text, d.Date, d.Names, d.Items)).ToList();
        var problem = own ? OwnRequest : (await handler.CheckAsync(change, tx, ct).ConfigureAwait(false))?.ErrorKey;
        return (description.Subject, details, problem);
    }
}

/// <summary>Stores a module's change as a pending maker-checker request in the module's transaction (<see cref="IHeldChanges"/>).</summary>
internal sealed class HeldChangeService(IClock clock, ICurrentActor actor, IEnumerable<IHeldChangeHandler> handlers) : IHeldChanges
{
    public async Task<Guid> SubmitAsync(HeldChangeRequest request, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!handlers.Any(h => string.Equals(h.Kind, request.Kind, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"No held-change handler is registered for {request.Kind}.");
        }

        var tx = (NpgsqlTransaction)transaction;
        var c = tx.Connection ?? throw new InvalidOperationException("The transaction has no connection.");
        var now = clock.UtcNow;
        var by = actor.UserId;
        var id = Guid.CreateVersion7(now);
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.maker_checker_requests (id, action_type, subject_type, subject_id, payload, requested_by, status, reason, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, 'pending', $7, $8, $6, $8, $6)
            """, cancellationToken, id, request.Kind, request.SubjectType, request.SubjectId, new JsonPayload(request.Payload), by, request.Reason, now).ConfigureAwait(false);
        await AccessDb.RaiseAsync(c, tx, AccessEvents.ChangeRequested, "iam.maker_checker_request", id, by, now,
            new { action = request.Kind, subject_type = request.SubjectType, subject_id = request.SubjectId }, cancellationToken).ConfigureAwait(false);
        return id;
    }
}
