using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.Kernel.Application.Access;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>The permission catalogue and the previews of the data-scope and field-security designers (FR-ADM-ROL-01, 06, 07).</summary>
public sealed partial class AccessAdministrationService
{
    public async Task<IReadOnlyList<CatalogEntry>> CatalogAsync(CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var command = IdentityDb.Command(c, null, """
            select p.code, p.module_id, p.description_i18n ->> 'key', p.is_sensitive,
                   coalesce(array_agg(r.code order by r.code) filter (where r.id is not null), '{}')
            from iam.permissions p
            left join iam.role_permissions rp on rp.permission_id = p.id
            left join iam.roles r on r.id = rp.role_id and not r.is_deleted
            where p.retired_at is null
            group by p.id, p.code, p.module_id, p.description_i18n, p.is_sensitive
            order by p.module_id, p.code
            """);
        var list = new List<CatalogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var code = reader.GetString(0);
            var dot = code.LastIndexOf('.'); // codes are module.resource.action; anything else is listed as a resource without action
            list.Add(new CatalogEntry(code, reader.GetString(1), dot < 0 ? code : code[..dot], dot < 0 ? string.Empty : code[(dot + 1)..], reader.IsDBNull(2) ? "permissions." + code : reader.GetString(2),
                reader.GetBoolean(3), reader.GetFieldValue<string[]>(4)));
        }

        return list;
    }

    public async Task<ScopePreview?> DataScopeAsync(Guid userId, string resource, int limit, CancellationToken cancellationToken)
    {
        if (resources.Find(resource) is not { } target)
        {
            return null;
        }

        await using (var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false))
        {
            if (!await IdentityDb.ScalarAsync<bool>(c, null, "select exists (select 1 from iam.users where id = $1 and not is_deleted)", cancellationToken, userId).ConfigureAwait(false))
            {
                return null;
            }
        }

        // The same engine, asked as the sample user: the preview is exactly what that user's own calls would return.
        var asUser = new AccessEngine(tenant, clock, new SampleUser(userId), workforce);
        var result = await target.Preview(services, asUser, limit, cancellationToken).ConfigureAwait(false);
        return new ScopePreview(resource, userId, result.Count, result.FirstPage);
    }

    public async Task<FieldPreview?> FieldsAsync(Guid roleId, string resource, CancellationToken cancellationToken)
    {
        if (resources.Find(resource) is not { } target)
        {
            return null;
        }

        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        if (!await IdentityDb.ScalarAsync<bool>(c, null, "select exists (select 1 from iam.roles where id = $1 and not is_deleted)", cancellationToken, roleId).ConfigureAwait(false))
        {
            return null;
        }

        var rules = await RoleRulesAsync(c, roleId, cancellationToken).ConfigureAwait(false);

        // A role without the read permission sees no field of the resource, whatever its field rules say (Q4).
        var map = AccessEngine.Merge(target.Fields, rules.Permissions.Contains(resource + ".read") ? [rules] : []);
        // The custom fields of the table (S1.15b, FR-KRN-MET-02): each defined key is a row of the matrix, retired ones too (their data stays),
        // with the name the administrator gave it; a key with a rule but no definition (a retired experiment) stays listed.
        var custom = new Dictionary<string, IReadOnlyDictionary<string, string>?>(StringComparer.Ordinal);
        await using (var command = IdentityDb.Command(c, null, "select field_key, label_i18n::text from cfg.custom_field_defs where entity_type = $1 order by seq, field_key", resource))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                custom[FieldAccessMap.CustomPrefix + reader.GetString(0)] = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1));
            }
        }

        foreach (var key in map.Levels.Keys.Where(k => k.StartsWith(FieldAccessMap.CustomPrefix, StringComparison.Ordinal)))
        {
            custom.TryAdd(key, null);
        }

        var items = target.Fields.Fields.Select(f => new FieldPreviewItem(f.Key, f.FieldClass, AccessSnapshot.LevelText(map.Of(f.Key))))
            .Concat(custom.Select(k => new FieldPreviewItem(k.Key, null, AccessSnapshot.LevelText(map.Of(k.Key)), k.Value)))
            .ToList();
        return new FieldPreview(resource, roleId, items);
    }

    private static async Task<RoleRules> RoleRulesAsync(NpgsqlConnection c, Guid roleId, CancellationToken ct)
    {
        var rules = new RoleRules();
        await using (var command = IdentityDb.Command(c, null, "select p.code from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id and p.retired_at is null where rp.role_id = $1", roleId))
        {
            foreach (var code in await AccessDb.TextsAsync(command, ct).ConfigureAwait(false))
            {
                rules.Permissions.Add(code);
            }
        }

        await using (var command = IdentityDb.Command(c, null, "select resource_type, field_key, access from iam.field_policies where role_id = $1", roleId))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rules.Fields[(reader.GetString(0), reader.GetString(1))] = AccessSnapshot.ParseLevel(reader.GetString(2));
            }
        }

        return rules;
    }

    private sealed record SampleUser(Guid UserId) : ICurrentActor;
}
