using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Migration;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>
/// The activation link of a customer's first tenant administrator, printed by the operator CLI (slice S1.05 ruling Q6;
/// e-mail delivery is S1.11). The link points at the customer's own host, and the token works only in that
/// customer's database.
/// </summary>
public sealed class FirstAdminActivation(ControlPlaneOptions options, ISecretStore secrets, IClock clock, MigrationSet migrations)
{
    public sealed record Link(string Url, DateTimeOffset ExpiresAt);

    /// <summary>A fresh link (the previous one stops working), or null when no invited tenant administrator is waiting.</summary>
    public async Task<Link?> IssueAsync(string tenantCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantCode);
        var host = await TenantHosts.PrimaryAsync(options, tenantCode, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {tenantCode} is not registered.");
        var tenant = await new TenantDirectory(options, clock).FindByHostAsync(host, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {tenantCode} has no database yet.");
        await using var owner = await new TenantProvisioner(options, secrets, clock, migrations).OpenTenantAsync(tenant, TenantLogins.Owner, cancellationToken).ConfigureAwait(false);
        if (await ActivationTokens.FirstInvitedAdminAsync(owner, cancellationToken).ConfigureAwait(false) is not { } userId)
        {
            return null;
        }

        var token = await ActivationTokens.IssueAsync(owner, userId, BaselineSeed.SystemUserId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return token is null ? null : new Link($"https://{host}/activate?token={Uri.EscapeDataString(token.Token)}", token.ExpiresAt);
    }
}
