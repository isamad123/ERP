namespace Erp.Kernel.Contracts.Identity;

/// <summary>
/// Who is signed in (slice S1.12b, S1.12 ruling Q9; contract "Session context API"): what the web shell needs to show or
/// hide menus and buttons, choose the language, time zone and number style, and offer the company switcher. It is never an
/// authorization: the server checks every request again.
/// </summary>
/// <param name="UserId">The signed-in person (<c>iam.users.id</c>).</param>
/// <param name="DisplayName">The person's display name.</param>
/// <param name="Language">The person's language (<c>iam.users.language</c>, always set); the web starts every sign-in in it (ruling 2026-11-21).</param>
/// <param name="TimeZone">The person's time zone (<c>iam.users.time_zone</c>, always set).</param>
/// <param name="DefaultCompanyId">The person's default company when it is one of <paramref name="Companies"/>, else the first of them.</param>
/// <param name="Companies">Active companies where the person holds any role today (a role for all companies: every active company).</param>
/// <param name="Permissions">Permission codes held today, each with the companies it covers (null: all companies).</param>
/// <param name="Mfa">Second-factor state, worked out as sign-in does.</param>
/// <param name="Numbers">Digits and grouping on screens for the requested company (ruling Q6).</param>
public sealed record SessionContextView(
    Guid UserId,
    string DisplayName,
    string Language,
    string TimeZone,
    Guid? DefaultCompanyId,
    IReadOnlyList<SessionCompanyView> Companies,
    IReadOnlyList<PermissionGrantView> Permissions,
    SessionMfaView Mfa,
    NumberFormatView Numbers);

/// <summary>A company of the switcher: id, code and name per language (<c>org.companies.name_i18n</c>).</summary>
public sealed record SessionCompanyView(Guid Id, string Code, IReadOnlyDictionary<string, string> Name);

/// <summary>A permission held today and the companies it covers (null: all companies).</summary>
public sealed record PermissionGrantView(string Code, IReadOnlyList<Guid>? Companies);

/// <summary>Second-factor state: required by policy, methods in use, and the enrolment deadline while the grace period runs.</summary>
public sealed record SessionMfaView(bool Required, IReadOnlyList<string> Methods, DateTimeOffset? EnrolBy);

/// <summary>Settings <c>loc.number.digits</c> and <c>loc.number.grouping</c> in effect (S1.12 ruling Q6).</summary>
public sealed record NumberFormatView(string Digits, string Grouping);

/// <summary>The session context of the signed-in person (S1.12 ruling Q9).</summary>
public interface ISessionContextService
{
    /// <summary>
    /// The context (null when the signed-in id names no person); <paramref name="companyId"/> chooses the company layer of
    /// the number settings when it is one of the person's companies.
    /// </summary>
    Task<SessionContextView?> GetAsync(Guid? companyId, CancellationToken cancellationToken);
}
