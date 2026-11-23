using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Numbering;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Contracts.Setup;
using Erp.Kernel.Domain.Generated.Iam;
using Erp.Kernel.Domain.Generated.Org;
using Erp.Kernel.Domain.Generated.Ref;
using Erp.Kernel.Domain.Generated.Wf;
using Erp.Kernel.Infrastructure.Generated;
using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Erp.Kernel.Infrastructure.Setup;

/// <summary>
/// The kernel's readiness checks (slice S1.18a Build item 1): read-only questions about the customer's own tables. Each one
/// returns done, or open with a message key and the numbers to show.
/// </summary>
internal static class Args
{
    public static IReadOnlyDictionary<string, string> Of(string name, string value) => new Dictionary<string, string> { [name] = value };
}

/// <summary>The company's required profile is complete (legal name, country, currency, language, a recorded registration).</summary>
public sealed class CompanyProfileCheck(KernelDbContext db, IClock clock) : IReadinessCheck
{
    public string Name => "kernel.company_profile";

    public async Task<ReadinessResult> CheckAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (companyId is not { } id)
        {
            return ReadinessResult.Open("setup.messages.company_needed");
        }

        var company = await db.Set<Company>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (company is null)
        {
            return ReadinessResult.Open("setup.messages.company_needed");
        }

        if (string.IsNullOrWhiteSpace(company.LegalName) || string.IsNullOrWhiteSpace(company.CountryCode) || string.IsNullOrWhiteSpace(company.BaseCurrencyCode) || string.IsNullOrWhiteSpace(company.DefaultLanguage))
        {
            return ReadinessResult.Open("setup.messages.company_fields_missing");
        }

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var registered = await db.Set<CompanyRegistration>().AsNoTracking().AnyAsync(r => r.CompanyId == id && r.ValidFrom <= today && (r.ValidTo == null || r.ValidTo >= today), cancellationToken).ConfigureAwait(false);
        return registered ? ReadinessResult.Ok : ReadinessResult.Open("setup.messages.registration_missing");
    }
}

/// <summary>The company has at least one location.</summary>
public sealed class LocationCheck(KernelDbContext db) : IReadinessCheck
{
    public string Name => "kernel.location";

    public async Task<ReadinessResult> CheckAsync(Guid? companyId, CancellationToken cancellationToken) =>
        companyId is { } id && await db.Set<Location>().AsNoTracking().AnyAsync(l => l.CompanyId == id && !l.IsDeleted, cancellationToken).ConfigureAwait(false)
            ? ReadinessResult.Ok
            : ReadinessResult.Open("setup.messages.location_missing");
}

/// <summary>The company has at least one organization unit.</summary>
public sealed class UnitStructureCheck(KernelDbContext db) : IReadinessCheck
{
    public string Name => "kernel.unit_structure";

    public async Task<ReadinessResult> CheckAsync(Guid? companyId, CancellationToken cancellationToken) =>
        companyId is { } id && await db.Set<OrgUnit>().AsNoTracking().AnyAsync(u => u.CompanyId == id && !u.IsDeleted, cancellationToken).ConfigureAwait(false)
            ? ReadinessResult.Ok
            : ReadinessResult.Open("setup.messages.units_missing");
}

/// <summary>The company (or one of its locations) uses a holiday calendar that lists at least one holiday of the current year.</summary>
public sealed class HolidayCalendarCheck(KernelDbContext db, IClock clock) : IReadinessCheck
{
    public string Name => "kernel.holiday_calendar";

    public async Task<ReadinessResult> CheckAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (companyId is not { } id)
        {
            return ReadinessResult.Open("setup.messages.company_needed");
        }

        var year = clock.UtcNow.Year;
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);
        var calendars = await db.Set<Company>().AsNoTracking().Where(c => c.Id == id && c.HolidayCalendarId != null).Select(c => c.HolidayCalendarId!.Value)
            .Concat(db.Set<Location>().AsNoTracking().Where(l => l.CompanyId == id && !l.IsDeleted && l.HolidayCalendarId != null).Select(l => l.HolidayCalendarId!.Value))
            .Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var listed = calendars.Count > 0 && await db.Set<Holiday>().AsNoTracking().AnyAsync(h => calendars.Contains(h.CalendarId) && h.HolidayDate >= from && h.HolidayDate <= to, cancellationToken).ConfigureAwait(false);
        return listed ? ReadinessResult.Ok : ReadinessResult.Open("setup.messages.holidays_missing", Args.Of("year", year.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}

/// <summary>Every numbered document type has an active scheme for the company (S1.08, handoff "For S1.18").</summary>
public sealed class NumberingCheck(INumberingAdministration numbering) : IReadinessCheck
{
    public string Name => "kernel.numbering";

    public async Task<ReadinessResult> CheckAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (companyId is not { } id)
        {
            return ReadinessResult.Open("setup.messages.company_needed");
        }

        var types = await numbering.DocumentTypesAsync(id, cancellationToken).ConfigureAwait(false);
        var missing = types.Where(t => t.HasActiveScheme != true).Select(t => t.Code).Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0
            ? ReadinessResult.Ok
            : ReadinessResult.Open("setup.messages.numbering_missing", new Dictionary<string, string> { ["count"] = missing.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["types"] = string.Join(", ", missing) });
    }
}

/// <summary>Every licensed document type has an active approval route.</summary>
public sealed class ApprovalRoutesCheck(KernelDbContext db, WorkflowCatalog catalog, ILicensedFeatures licence) : IReadinessCheck
{
    public string Name => "kernel.approval_routes";

    public async Task<ReadinessResult> CheckAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var active = (await db.Set<WorkflowDefinition>().AsNoTracking().Where(d => d.ActiveVersionId != null).Select(d => d.DocumentType).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var type in catalog.All.OrderBy(t => t.Code, StringComparer.Ordinal))
        {
            if (!active.Contains(type.Code) && await licence.IsLicensedAsync(type.EntitlementKey, cancellationToken).ConfigureAwait(false))
            {
                missing.Add(type.Code);
            }
        }

        return missing.Count == 0
            ? ReadinessResult.Ok
            : ReadinessResult.Open("setup.messages.routes_missing", new Dictionary<string, string> { ["count"] = missing.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["types"] = string.Join(", ", missing) });
    }
}

/// <summary>At least two people hold the Tenant Administrator role, so nobody is locked out by one lost account.</summary>
public sealed class OtherAdministratorCheck(KernelDbContext db, IClock clock) : IReadinessCheck
{
    public string Name => "kernel.other_administrator";

    public async Task<ReadinessResult> CheckAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var administrators = await (from ur in db.Set<UserRole>().AsNoTracking()
                                    join r in db.Set<Role>().AsNoTracking() on ur.RoleId equals r.Id
                                    join u in db.Set<User>().AsNoTracking() on ur.UserId equals u.Id
                                    where r.BaselineCode == "tenant_admin" && !r.IsDeleted && r.IsActive && !u.IsDeleted && (u.Status == "active" || u.Status == "invited")
                                        && ur.ValidFrom <= today && (ur.ValidTo == null || ur.ValidTo >= today)
                                    select u.Id).Distinct().CountAsync(cancellationToken).ConfigureAwait(false);
        return administrators >= 2 ? ReadinessResult.Ok : ReadinessResult.Open("setup.messages.other_administrator_missing");
    }
}
