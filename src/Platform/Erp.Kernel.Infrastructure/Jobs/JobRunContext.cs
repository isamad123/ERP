using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>
/// Who the work of one service scope acts as when the scope runs a background job (slice S1.11 ruling Q4, R-009). Bound
/// by the runner before the handler starts; unbound in every request scope, so requests are unaffected.
/// <list type="bullet">
/// <item>A job-actor run acts as the reserved system user and the access engine gives it exactly the manifest's
/// permissions (<see cref="Grant"/>), scope "all", no field rights: never a general bypass.</item>
/// <item>A requested run acts as the person who asked (<see cref="ActorUserId"/>), with that person's own rights.</item>
/// </list>
/// The audit log shows source <c>job</c>, actor kind <c>job</c> (or <c>user</c> for a requested run) and the run id as
/// correlation id, which names the job (ruling P9).
/// </summary>
public sealed class JobRunContext
{
    public bool IsBound { get; private set; }

    public Guid RunId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    /// <summary>The person a requested run acts as; null for a job-actor run.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>The permissions of a job-actor run; null for a requested run (the person's own roles apply).</summary>
    public IReadOnlySet<string>? Grant { get; private set; }

    public void BindJobActor(Guid runId, string code, IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        Bind(runId, code);
        Grant = permissions.ToHashSet(StringComparer.Ordinal);
    }

    public void BindRequester(Guid runId, string code, Guid userId)
    {
        Bind(runId, code);
        ActorUserId = userId;
    }

    private void Bind(Guid runId, string code)
    {
        if (IsBound)
        {
            throw new InvalidOperationException("The job context of this scope is already bound.");
        }

        IsBound = true;
        RunId = runId;
        Code = code;
    }
}

/// <summary>The actor written to created_by and updated_by: the person of a requested run, else the system user.</summary>
public sealed class JobCurrentActor(JobRunContext job) : ICurrentActor
{
    public Guid UserId => job.ActorUserId ?? BaselineSeed.SystemUserId;
}

/// <summary>The audit context of a job run (ruling P9): source job, the run id as correlation id.</summary>
public sealed class JobAuditContext(JobRunContext job) : IAuditContext
{
    private readonly Guid _process = Guid.CreateVersion7();

    public Guid CorrelationId => job.IsBound ? job.RunId : _process;

    public string? IpAddress => null;

    public string Source => job.IsBound ? AuditSources.Job : AuditSources.System;

    public Guid? ActorUserId => job.ActorUserId;

    public string ActorKind => job.ActorUserId is null ? (job.IsBound ? ActorKinds.Job : ActorKinds.System) : ActorKinds.User;
}
