using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Entitlements;
using Erp.Kernel.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>What one escalation run did for one customer: its counts, why it was skipped, or the error.</summary>
public sealed record EscalationOutcome(string Code, EscalationRun? Run, string? Skipped, string? Error);

/// <summary>
/// Runs escalation (<see cref="WorkflowEngine.EscalateAsync"/>) for every served customer (FR-XM-32; slice S1.10 ruling Q9):
/// each in its own service scope bound to that customer's database and licence, as a request would be. A customer whose
/// access mode is not full (suspended, locked or read-only licence, database unavailable) is skipped: nothing changes
/// while changes are refused. A failure of one customer is reported and the others go on.
/// </summary>
public sealed class WorkflowEscalationRunner(ControlPlaneOptions options, ITenantDataSourceFactory dataSources, ILicenceResolver licences, IServiceScopeFactory scopes)
{
    public async Task<IReadOnlyList<EscalationOutcome>> RunAsync(string? onlyTenantCode, CancellationToken cancellationToken)
    {
        var tenants = new List<ResolvedTenant>();
        await using (var cp = new NpgsqlConnection(options.ConnectionString))
        {
            await cp.OpenAsync(cancellationToken).ConfigureAwait(false);
            var codes = await TenantDirectory.ServedCodesAsync(cp, onlyTenantCode, cancellationToken).ConfigureAwait(false);
            foreach (var code in codes)
            {
                if (await TenantDirectory.FindByCodeAsync(cp, code, cancellationToken).ConfigureAwait(false) is { } tenant)
                {
                    tenants.Add(tenant);
                }
            }
        }

        var outcomes = new List<EscalationOutcome>();
        foreach (var tenant in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                outcomes.Add(await RunOneAsync(tenant, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new EscalationOutcome(tenant.Code, null, null, ex.Message));
            }
        }

        return outcomes;
    }

    private async Task<EscalationOutcome> RunOneAsync(ResolvedTenant tenant, CancellationToken ct)
    {
        var dataSource = await dataSources.GetAsync(tenant, ct).ConfigureAwait(false);
        var licence = await licences.ResolveAsync(tenant, dataSource, ct).ConfigureAwait(false);
        var mode = TenantAccessPolicy.Decide(tenant.Status, tenant.DatabaseStatus, licence.State);
        if (mode != TenantAccessMode.Full)
        {
            return new EscalationOutcome(tenant.Code, null, mode.ToString().ToLowerInvariant(), null);
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(tenant, mode, dataSource, licence);
        var run = await scope.ServiceProvider.GetRequiredService<WorkflowEngine>().EscalateAsync(ct).ConfigureAwait(false);
        return new EscalationOutcome(tenant.Code, run, null, null);
    }
}

/// <summary>The system user as the actor of background work (escalation acts as the system user).</summary>
internal sealed class SystemActor : ICurrentActor
{
    public Guid UserId => BaselineSeed.SystemUserId;
}

/// <summary>Service registration of escalation outside the API host (worker host, operator command).</summary>
public static class WorkflowEscalationRegistration
{
    /// <summary>
    /// Tenancy, licence resolution, settings and the workflow engine for background escalation. The host adds an
    /// <c>IClock</c> and <c>TimeProvider</c>; modules add their document types and, for HR, the approver directory, as in
    /// the API host.
    /// </summary>
    public static IServiceCollection AddWorkflowEscalation(this IServiceCollection services, ControlPlaneOptions options, ISecretStore secrets, TimeSpan settingsRefresh)
    {
        services.AddErpTenancy(options, secrets);
        services.AddSingleton(new SettingsCacheOptions(settingsRefresh));
        services.AddSingleton<SettingsCache>();
        services.AddSingleton<ILicenceResolver, LicenceResolver>();
        services.AddScoped<ILicensedFeatures, EntitlementFeatures>();
        services.AddScoped<ICurrentActor, SystemActor>();
        services.AddKernelWorkflow();
        services.AddSingleton<WorkflowEscalationRunner>();
        return services;
    }
}
