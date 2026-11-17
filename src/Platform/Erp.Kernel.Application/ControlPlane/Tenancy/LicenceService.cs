using Erp.Kernel.Contracts.Entitlements;

namespace Erp.Kernel.Application.ControlPlane.Tenancy;

/// <summary>
/// The licence as the customer sees it (FR-KRN-ENT-03, FR-KRN-ENT-04, FR-KRN-ENT-06, FR-ADM-OPS-07; ruling Q16): the web shell's
/// capabilities, the licence page and the monthly usage statements. The customer is always the request's; nothing is taken
/// from request input but the months asked for.
/// </summary>
public interface ILicenceService
{
    Task<CapabilitiesView> CapabilitiesAsync(CancellationToken cancellationToken);

    Task<LicenceView> LicenceAsync(CancellationToken cancellationToken);

    /// <summary>Statements from <paramref name="fromPeriod"/> to <paramref name="toPeriod"/> (<c>yyyymm</c>; default the current month).</summary>
    Task<IReadOnlyList<UsageStatement>> UsageAsync(string? fromPeriod, string? toPeriod, CancellationToken cancellationToken);
}

/// <summary>The limit warnings taken during the request (written to the response header by the API host).</summary>
public interface ILimitWarningSource
{
    IReadOnlyList<LimitDecision> Warnings { get; }
}
