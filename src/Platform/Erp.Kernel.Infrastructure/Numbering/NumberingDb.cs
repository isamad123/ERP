using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Domain.Numbering;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Numbering;

/// <summary>One numbering scheme as the allocator reads it.</summary>
public sealed record SchemeRow(Guid Id, Guid? CompanyId, Guid? BranchId, string DocumentType, string Pattern, string ResetRule, bool GapFree, long StartValue, string Status, long RowVersion, DateTimeOffset CreatedAt);

/// <summary>One period of a series: the last value issued (slice S1.08 function <c>cfg.numbering_series_state</c>).</summary>
public sealed record SeriesPeriod(string ScopeKey, long LastValue, bool GapFree);

/// <summary>Parameterized SQL of the numbering service (slice S1.08). Every value is a parameter.</summary>
public static class NumberingDb
{
    /// <summary>SQLSTATE of <c>cfg.numbering_next</c> when the scheme was retired or changed after it was read.</summary>
    public const string SchemeChangedState = "ER801";

    private const string Columns = "id, company_id, branch_id, document_type, pattern, reset_rule, gap_free, start_value, status, row_version, created_at";

    /// <summary>The active scheme for the document: its branch's first, else the company-wide one (Q6).</summary>
    public static async Task<SchemeRow?> ActiveAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string documentType, Guid? companyId, Guid? branchId, CancellationToken ct)
    {
        await using var command = Sql.Command(c, tx, $"""
            select {Columns} from cfg.numbering_schemes
            where document_type = $1 and status = 'active' and company_id is not distinct from $2::uuid
              and (branch_id is null or branch_id = $3::uuid)
            order by branch_id nulls last limit 1
            """, [documentType, companyId, branchId]);
        command.Parameters[1].NpgsqlDbType = NpgsqlDbType.Uuid;
        command.Parameters[2].NpgsqlDbType = NpgsqlDbType.Uuid;
        var rows = await ReadAsync(command, ct).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Every scheme (any status) of a document type for a company, newest first.</summary>
    public static async Task<IReadOnlyList<SchemeRow>> AllAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string documentType, Guid? companyId, CancellationToken ct)
    {
        await using var command = Sql.Command(c, tx, $"""
            select {Columns} from cfg.numbering_schemes
            where document_type = $1 and company_id is not distinct from $2::uuid
            order by created_at desc, id desc
            """, [documentType, companyId]);
        command.Parameters[1].NpgsqlDbType = NpgsqlDbType.Uuid;
        return await ReadAsync(command, ct).ConfigureAwait(false);
    }

    public static async Task<SchemeRow?> ByIdAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var command = Sql.Command(c, tx, $"select {Columns} from cfg.numbering_schemes where id = $1" + (forUpdate ? " for update" : string.Empty), [id]);
        var rows = await ReadAsync(command, ct).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Creates the default scheme of a document type for a company (or the customer, for tenant-wide types) when the
    /// company has no scheme of that type at all (Q7): company-wide, gap-allowed, start value 1. A scheme an administrator
    /// retired is never brought back. <paramref name="audited"/>: the allocator's creation on first use is change-logged with
    /// the system as actor (Q7); seeding by provisioning and <c>migrate</c> is baseline data like roles and settings and is not.
    /// Returns true when it created one.
    /// </summary>
    public static async Task<bool> EnsureDefaultAsync(NpgsqlConnection c, NpgsqlTransaction tx, DocumentType type, Guid? companyId, DateTimeOffset now, bool audited, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);
        var system = BaselineSeed.SystemUserId;
        var id = Guid.CreateVersion7(now);
        await using var command = Sql.Command(c, tx, """
            insert into cfg.numbering_schemes (id, company_id, branch_id, document_type, pattern, reset_rule, gap_free, start_value, status, created_at, created_by, updated_at, updated_by, row_version)
            select $1, $2::uuid, null, $3, $4, $5, false, 1, 'active', $6, $7, $6, $7, 1
            where not exists (select 1 from cfg.numbering_schemes where document_type = $3 and company_id is not distinct from $2::uuid)
            on conflict do nothing
            """, [id, companyId, type.Code, type.DefaultPattern, type.DefaultReset, now, system]);
        command.Parameters[1].NpgsqlDbType = NpgsqlDbType.Uuid;
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            return false;
        }

        if (!audited)
        {
            return true;
        }

        var after = new JsonObject
        {
            ["company_id"] = companyId,
            ["document_type"] = type.Code,
            ["pattern"] = type.DefaultPattern,
            ["reset_rule"] = type.DefaultReset,
            ["gap_free"] = false,
            ["start_value"] = 1,
            ["status"] = "active",
        };
        var changes = new JsonArray();
        foreach (var (field, value) in after)
        {
            changes.Add(new JsonObject { ["field"] = field, ["before"] = null, ["after"] = value?.DeepClone() });
        }

        var request = AuditAmbient.Current;
        await AuditDb.AppendChangesAsync(c, tx,
            [new ChangeEntry(Guid.CreateVersion7(now), now, null, ActorKinds.System, "cfg.numbering_schemes", id, companyId, "create", changes, null,
                request?.CorrelationId, request?.IpAddress, AuditSources.System, system)], ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The next running value (function <c>cfg.numbering_next</c>): on the caller's transaction for gap-free schemes.</summary>
    public static async Task<long> NextAsync(NpgsqlConnection c, NpgsqlTransaction? tx, SchemeRow scheme, string scopeKey, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        return await Sql.ScalarAsync<long>(c, tx, "select cfg.numbering_next($1, $2, $3, $4)", ct, scheme.Id, scheme.RowVersion, scopeKey, now).ConfigureAwait(false);
    }

    /// <summary>What the scheme's series has issued, per period (function <c>cfg.numbering_series_state</c>).</summary>
    public static async Task<IReadOnlyList<SeriesPeriod>> SeriesAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid schemeId, CancellationToken ct)
    {
        await using var command = Sql.Command(c, tx, "select scope_key, last_value, gap_free from cfg.numbering_series_state($1) order by scope_key", [schemeId]);
        var list = new List<SeriesPeriod>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new SeriesPeriod(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2)));
        }

        return list;
    }

    /// <summary>Code of a branch of the company, or null when the branch is not the company's.</summary>
    public static Task<string?> BranchCodeAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid? companyId, Guid branchId, CancellationToken ct) =>
        Sql.ScalarAsync<string>(c, tx, "select code from org.branches where id = $1 and company_id = $2 and not is_deleted", ct, branchId, companyId);

    /// <summary>The values a number of this scheme is rendered with.</summary>
    public static async Task<NumberContext> ContextAsync(NpgsqlConnection c, NpgsqlTransaction? tx, SchemeRow scheme, DateOnly? date, DateTimeOffset now, Guid? branchId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var settings = await NumberingSettings.ReadAsync(c, tx, scheme.CompanyId, ct).ConfigureAwait(false);
        string? branchCode = null;
        if (branchId is { } b && scheme.Pattern.Contains("{branch}", StringComparison.Ordinal))
        {
            branchCode = await BranchCodeAsync(c, tx, scheme.CompanyId, b, ct).ConfigureAwait(false) ?? throw new NumberingException("errors.numbering.branch_unknown");
        }

        return new NumberContext(date ?? NumberingSettings.Today(now, settings.TimeZone), settings.FiscalYearStartMonth, settings.CompanyCode, branchCode);
    }

    private static async Task<IReadOnlyList<SchemeRow>> ReadAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var list = new List<SchemeRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new SchemeRow(
                reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetInt64(7), reader.GetString(8), reader.GetInt64(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return list;
    }
}
