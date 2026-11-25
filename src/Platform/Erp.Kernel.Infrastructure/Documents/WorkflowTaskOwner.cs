using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Documents;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// Files on workflow decisions (KRN-WFL-07, ruling Q23): the person the task is for, the one who acted on it and the
/// requester of the request may read its files; the assignee of an open task and the requester of an open request may add
/// them. Nobody else, whatever their role: a decision's evidence is only for the people in the request.
/// </summary>
public sealed class WorkflowTaskOwner(ITenantConnectionProvider tenant, ICurrentActor actor) : IAttachmentOwner
{
    public const string Type = "wf.workflow_tasks";

    public string OwnerType => Type;

    public async Task<AttachmentOwnerAccess?> AccessAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        await using var c = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await Notifications.NotificationConsumer.ReadAsync(c, null, """
            select i.company_id, t.assignee_user_id = $2, t.acted_by = $2, i.requester_user_id = $2, t.status = 'pending', i.status in ('running', 'returned')
            from wf.workflow_tasks t join wf.workflow_instances i on i.id = t.instance_id where t.id = $1
            """, r => (Company: r.GetGuid(0), Assignee: !r.IsDBNull(1) && r.GetBoolean(1), Actor: !r.IsDBNull(2) && r.GetBoolean(2), Requester: r.GetBoolean(3), Pending: r.GetBoolean(4), Open: r.GetBoolean(5)),
            cancellationToken, ownerId, actor.UserId).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var t = rows[0];
        var read = t.Assignee || t.Actor || t.Requester;
        return new AttachmentOwnerAccess(t.Company, read, (t.Assignee && t.Pending) || (t.Requester && t.Open), read);
    }
}
