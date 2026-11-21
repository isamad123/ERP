using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Identity;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Notifications;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Identity;

/// <summary>
/// Who is signed in (slice S1.12b, S1.12 ruling Q9): the person's name, language and time zone, the companies where they
/// hold any role today, their permission codes with the companies each covers (from the one access engine), their
/// second-factor state (the same rule as sign-in) and the number style in force (ruling Q6). Read only; the web shell uses
/// it to show or hide things and the server keeps deciding every request.
/// </summary>
public sealed class SessionContextService(ITenantConnectionProvider tenant, ICurrentActor actor, AccessEngine access, ISettings settings, IClock clock)
    : ISessionContextService
{
    public async Task<SessionContextView?> GetAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var userId = actor.UserId;
        var now = clock.UtcNow;
        var grants = await access.GrantsAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var user = (await NotificationConsumer.ReadAsync(connection, tx,
            "select display_name, btrim(language), time_zone, default_company_id from iam.users where id = $1",
            r => (Name: r.GetString(0), Language: r.GetString(1), TimeZone: r.GetString(2), DefaultCompany: r.IsDBNull(3) ? (Guid?)null : r.GetGuid(3)),
            cancellationToken, userId).ConfigureAwait(false)).SingleOrDefault();
        if (user.Name is null)
        {
            return null; // a token of a person who no longer exists (never issued in practice): nothing to show
        }

        // Companies where the person holds any permission today; a role for all companies opens every active company.
        var all = grants.Any(g => g.Companies is null);
        var named = grants.Where(g => g.Companies is not null).SelectMany(g => g.Companies!).Distinct().ToArray();
        var companies = await NotificationConsumer.ReadAsync(connection, tx, """
            select id, code, name_i18n::text from org.companies
            where not is_deleted and is_active and status = 'active' and ($1 or id = any($2))
            order by code
            """, r => new SessionCompanyView(r.GetGuid(0), r.GetString(1), Names(r.GetString(2))), cancellationToken, all, named).ConfigureAwait(false);

        var defaultCompany = companies.FirstOrDefault(c => c.Id == user.DefaultCompany)?.Id ?? companies.FirstOrDefault()?.Id;
        var mfa = await MfaAsync(connection, tx, userId, now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The number style follows the company in hand when it is one of the person's companies (deviation h of CR-063).
        var numbersCompany = companyId is { } asked && companies.Any(c => c.Id == asked) ? asked : defaultCompany;
        var context = new SettingContext(numbersCompany, userId);
        var digits = (await settings.GetAsync(KernelSettingKeys.LocNumberDigits, context, cancellationToken).ConfigureAwait(false)).Value.GetString() ?? "western";
        var grouping = (await settings.GetAsync(KernelSettingKeys.LocNumberGrouping, context, cancellationToken).ConfigureAwait(false)).Value.GetString() ?? "international";

        return new SessionContextView(userId, user.Name, user.Language, user.TimeZone, defaultCompany, companies, grants, mfa, new NumberFormatView(digits, grouping));
    }

    /// <summary>The second-factor state as sign-in works it out (<c>AuthenticationService.ContinueAsync</c>).</summary>
    private static async Task<SessionMfaView> MfaAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var policy = await IdentitySettings.ReadAsync(connection, tx, userId, now, cancellationToken).ConfigureAwait(false);
        var kinds = await NotificationConsumer.ReadAsync(connection, tx, "select distinct kind from iam.mfa_devices where user_id = $1 and status = 'active' order by kind",
            r => r.GetString(0), cancellationToken, userId).ConfigureAwait(false);
        List<string> usable = [.. kinds.Where(k => k != MfaKinds.Email || policy.Mfa.AllowedMethods.Contains(MfaKinds.Email, StringComparer.Ordinal))];
        DateTimeOffset? enrolBy = null;
        if (policy.Mfa.Required && usable.Count == 0)
        {
            var activatedAt = await IdentityDb.ActivatedAtAsync(connection, tx, userId, cancellationToken).ConfigureAwait(false) ?? now;
            enrolBy = policy.Mfa.GraceEndsAt(activatedAt);
        }

        return new SessionMfaView(policy.Mfa.Required, usable, enrolBy);
    }

    private static Dictionary<string, string> Names(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }
}
