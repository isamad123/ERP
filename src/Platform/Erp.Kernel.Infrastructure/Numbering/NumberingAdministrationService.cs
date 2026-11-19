using System.Globalization;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Generated.Cfg;
using Erp.Kernel.Application.Numbering;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Domain.Generated.Cfg;
using Erp.Kernel.Domain.Numbering;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Numbering;

/// <summary>
/// Numbering administration (slice S1.08 build items 3 and 5, rulings Q4, Q6, Q8, Q10, P1, P2). Scheme writes go through the
/// generated repository, so the caller's data scope (Company Admin: own companies) is checked before and after, and the
/// change log records every change (S1.07). The scheme row is locked before "in use" is checked, so an edit can never
/// race the first allocation (<c>cfg.numbering_next</c> holds the row FOR SHARE).
/// </summary>
public sealed class NumberingAdministrationService(KernelDbContext context, INumberingSchemeRepository schemes, DocumentTypeRegistry registry, IClock clock)
    : INumberingAdministration
{
    public const long MaxStartValue = 999_999_999_999_999;

    public async Task<SchemeView> CreateAsync(CreateSchemeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = registry.Find(request.DocumentType) ?? throw Invalid("errors.numbering.type_unknown");
        if (type.TenantWide ? request.CompanyId is not null : request.CompanyId is null)
        {
            throw Invalid(type.TenantWide ? "errors.numbering.company_not_allowed" : "errors.numbering.company_required");
        }

        await using var tx = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (connection, npgsqlTx) = Connection(tx);
        var row = new NumberingScheme
        {
            CompanyId = request.CompanyId,
            BranchId = request.BranchId,
            DocumentType = type.Code,
            Status = "active",
        };

        // The caller's data scope first, so nothing about another company's branches or schemes is read or answered
        // (S1.08 review finding 6). AddAsync checks again with the final values.
        await schemes.EnsureCanCreateAsync(row, cancellationToken).ConfigureAwait(false);
        await ApplyAsync(connection, npgsqlTx, type, row, request.BranchId, request.Pattern, request.ResetRule, request.GapFree, request.StartValue, cancellationToken).ConfigureAwait(false);
        if (await NumberingDb.ActiveAsync(connection, npgsqlTx, type.Code, row.CompanyId, row.BranchId, cancellationToken).ConfigureAwait(false) is { } active && active.BranchId == row.BranchId)
        {
            throw Invalid("errors.numbering.active_scheme_exists");
        }

        await schemes.AddAsync(row, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SchemeView> EditAsync(Guid id, EditSchemeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RowVersion is null)
        {
            throw Invalid("errors.numbering.row_version_required");
        }

        await using var tx = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (connection, npgsqlTx) = Connection(tx);
        var row = await LockAsync(connection, npgsqlTx, id, cancellationToken).ConfigureAwait(false);
        if ((await NumberingDb.SeriesAsync(connection, npgsqlTx, id, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw Invalid("errors.numbering.scheme_in_use");
        }

        var type = registry.Find(row.DocumentType) ?? throw Invalid("errors.numbering.type_unknown");
        await ApplyAsync(connection, npgsqlTx, type, row, request.BranchId, request.Pattern, request.ResetRule, request.GapFree, request.StartValue, cancellationToken).ConfigureAwait(false);
        if (await NumberingDb.ActiveAsync(connection, npgsqlTx, type.Code, row.CompanyId, row.BranchId, cancellationToken).ConfigureAwait(false) is { } active && active.BranchId == row.BranchId && active.Id != row.Id)
        {
            throw Invalid("errors.numbering.active_scheme_exists");
        }

        await schemes.UpdateAsync(row, request.RowVersion.Value, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SchemeView> RetireAsync(Guid id, RetireSchemeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RowVersion is null)
        {
            throw Invalid("errors.numbering.row_version_required");
        }

        await using var tx = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (connection, npgsqlTx) = Connection(tx);
        var row = await LockAsync(connection, npgsqlTx, id, cancellationToken).ConfigureAwait(false);
        row.Status = "retired";
        await schemes.UpdateAsync(row, request.RowVersion.Value, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DocumentTypeView>> DocumentTypesAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var list = new List<DocumentTypeView>();
        foreach (var type in registry.All)
        {
            bool? active = null;
            if (companyId is not null)
            {
                // Through the scoped repository: a company outside the caller's scope shows no scheme.
                var rows = await schemes.FindByCompanyIdAndBranchIdAndDocumentTypeAsync(type.TenantWide ? null : companyId, null, type.Code, cancellationToken).ConfigureAwait(false);
                active = rows.Any(r => r.Status == "active");
            }

            list.Add(new DocumentTypeView(type.Code, type.Module, type.Assign, type.GapFreeAllowed, type.OverrideAllowed, type.TenantWide, type.HasBranch, type.DefaultPattern, type.DefaultReset, active));
        }

        return list;
    }

    public async Task<VoidReport> VoidReportAsync(Guid schemeId, DateOnly? fromDay, DateOnly? toDay, CancellationToken cancellationToken)
    {
        var (from, to) = (fromDay, toDay);
        if (from is not null && to is not null && from > to)
        {
            throw Invalid("errors.numbering.range_invalid");
        }

        var scheme = await schemes.GetByIdAsync(schemeId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("errors.numbering.not_found");
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var row = await NumberingDb.ByIdAsync(connection, null, schemeId, false, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("errors.numbering.not_found");
            var settings = await NumberingSettings.ReadAsync(connection, null, row.CompanyId, cancellationToken).ConfigureAwait(false);
            var branchCode = row.BranchId is { } b ? await NumberingDb.BranchCodeAsync(connection, null, row.CompanyId, b, cancellationToken).ConfigureAwait(false) : null;
            var pattern = NumberPattern.Parse(row.Pattern);
            var issued = new List<IssuedRange>();
            foreach (var period in await NumberingDb.SeriesAsync(connection, null, schemeId, cancellationToken).ConfigureAwait(false))
            {
                var (start, end) = PeriodDates(period.ScopeKey, settings.FiscalYearStartMonth);
                if ((to is not null && start is not null && start > to) || (from is not null && end is not null && end < from))
                {
                    continue;
                }

                var ctx = new NumberContext(start ?? NumberingSettings.Today(clock.UtcNow, settings.TimeZone), settings.FiscalYearStartMonth, settings.CompanyCode, branchCode);
                issued.Add(new IssuedRange(period.ScopeKey, Print(pattern, row.StartValue, ctx), Print(pattern, period.LastValue, ctx),
                    period.GapFree ? period.LastValue - row.StartValue + 1 : null, period.GapFree));
            }

            var voids = new List<VoidLine>();
            await using (var command = Sql.Command(connection, null, """
                select v.id, v.number, v.reason, v.document_id, v.scheme_id, v.created_at, v.created_by
                from cfg.numbering_voids v
                join cfg.numbering_schemes s on s.id = v.scheme_id
                join cfg.numbering_schemes me on me.id = $1
                where s.document_type = me.document_type and s.company_id is not distinct from me.company_id
                  and s.branch_id is not distinct from me.branch_id and s.pattern = me.pattern
                  and ($2::timestamptz is null or v.created_at >= $2) and ($3::timestamptz is null or v.created_at < $3)
                order by v.created_at, v.id
                """, [schemeId, From(from, settings.TimeZone), From(to?.AddDays(1), settings.TimeZone)]))
            {
                command.Parameters[1].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
                command.Parameters[2].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    voids.Add(new VoidLine(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                        reader.GetGuid(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetGuid(6)));
                }
            }

            return new VoidReport(row.Id, row.DocumentType, row.Pattern, row.GapFree, from, to, issued, voids);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Validates and copies the editable fields (Q4, Q8, P1, P2): known reset rule and a valid pattern; a branch only for a
    /// document type that has one, belonging to the company, and with <c>{branch}</c> in the pattern; gap-free only where
    /// the type allows it; a start value from 1; and a scheme that continues a series (same company, branch, type and
    /// pattern as an earlier scheme) keeps that series' reset rule, gap-free setting and start value.
    /// </summary>
    private static async Task ApplyAsync(NpgsqlConnection c, NpgsqlTransaction tx, DocumentType type, NumberingScheme row, Guid? branchId, string? pattern, string? resetRule, bool? gapFree, long? startValue, CancellationToken ct)
    {
        string? branchCode = null;
        if (branchId is { } branch)
        {
            if (!type.HasBranch)
            {
                throw Invalid("errors.numbering.branch_not_allowed");
            }

            branchCode = await NumberingDb.BranchCodeAsync(c, tx, row.CompanyId, branch, ct).ConfigureAwait(false) ?? throw Invalid("errors.numbering.branch_unknown");
        }

        var settings = await NumberingSettings.ReadAsync(c, tx, row.CompanyId, ct).ConfigureAwait(false);
        NumberPattern parsed;
        try
        {
            parsed = NumberPattern.Validate(pattern?.Trim(), resetRule, new PatternContext(!type.TenantWide, type.HasBranch, settings.CompanyCode?.Length ?? 0, branchCode?.Length ?? 0, BranchScheme: branchId is not null));
        }
        catch (NumberPatternException ex)
        {
            throw Invalid(ex.ErrorKey);
        }

        if (branchId is not null && !parsed.UsesBranch)
        {
            throw Invalid("errors.numbering.branch_token_needed");
        }

        var free = gapFree ?? false;
        if (free && !type.GapFreeAllowed)
        {
            throw Invalid("errors.numbering.gap_free_not_allowed");
        }

        var start = startValue ?? 1;
        if (start is < 1 or > MaxStartValue)
        {
            throw Invalid("errors.numbering.start_value_invalid");
        }

        var others = (await NumberingDb.AllAsync(c, tx, type.Code, row.CompanyId, ct).ConfigureAwait(false))
            .Where(s => s.Id != row.Id && s.BranchId == branchId).ToList();
        var earlier = others.Where(s => s.Pattern == parsed.Text).ToList();
        if (earlier.Exists(s => s.ResetRule != resetRule || s.GapFree != free || s.StartValue != start))
        {
            throw Invalid("errors.numbering.continuation_mismatch");
        }

        // CR-058 (S1.08 review finding 1): a new series of the same company, branch and type must differ from every
        // earlier one in its text, not only in padding, or it would print their numbers again (EMP-0{seq:4} after EMP-{seq:5}).
        if (others.Exists(s => s.Pattern != parsed.Text && SameShape(s.Pattern, parsed)))
        {
            throw Invalid("errors.numbering.pattern_too_close");
        }

        row.BranchId = branchId;
        row.Pattern = parsed.Text;
        row.ResetRule = resetRule!;
        row.GapFree = free;
        row.StartValue = start;
    }

    private static bool SameShape(string earlier, NumberPattern pattern)
    {
        try
        {
            return string.Equals(NumberPattern.Parse(earlier).Shape, pattern.Shape, StringComparison.OrdinalIgnoreCase);
        }
        catch (NumberPatternException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks the caller's data scope, then locks the row (so no allocation runs meanwhile) and reads it again through the
    /// scope. A scheme outside the scope is never locked (S1.08 review finding 6).
    /// </summary>
    private async Task<NumberingScheme> LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        if (await schemes.GetByIdAsync(id, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("errors.numbering.not_found");
        }

        var locked = await NumberingDb.ByIdAsync(c, tx, id, true, ct).ConfigureAwait(false);
        var row = locked is null ? null : await schemes.GetForUpdateAsync(id, ct).ConfigureAwait(false);
        if (row is null)
        {
            throw new KeyNotFoundException("errors.numbering.not_found");
        }

        return row.Status == "retired" ? throw Invalid("errors.numbering.scheme_retired") : row;
    }

    private async Task<SchemeView> ViewAsync(NumberingScheme row, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var inUse = (await NumberingDb.SeriesAsync(connection, null, row.Id, ct).ConfigureAwait(false)).Count > 0;
            return new SchemeView(row.Id, row.CompanyId, row.BranchId, row.DocumentType, row.Pattern, row.ResetRule, row.GapFree, row.StartValue, row.Status, inUse, row.RowVersion);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A number of the report as the scheme prints it; the running value alone when it cannot be printed (a company-wide
    /// scheme with <c>{branch}</c> made before CR-058, or a code that changed since; S1.08 review finding 4).
    /// </summary>
    private static string Print(NumberPattern pattern, long value, NumberContext context)
    {
        try
        {
            return pattern.Render(value, context);
        }
        catch (NumberPatternException)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>First and last day of a period key (null where open).</summary>
    private static (DateOnly? Start, DateOnly? End) PeriodDates(string scope, int fiscalStartMonth)
    {
        if (scope.Length == 4 && int.TryParse(scope, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        }

        if (scope.Length == 7 && DateOnly.TryParseExact(scope + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return (month, month.AddMonths(1).AddDays(-1));
        }

        if (scope.StartsWith("fy", StringComparison.Ordinal) && int.TryParse(scope[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var fy))
        {
            var start = new DateOnly(fy, fiscalStartMonth, 1);
            return (start, start.AddYears(1).AddDays(-1));
        }

        return (null, null);
    }

    private static DateTimeOffset? From(DateOnly? day, string timeZone)
    {
        if (day is not { } d)
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var local = d.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) Connection(IDbContextTransaction tx) =>
        ((NpgsqlConnection)tx.GetDbTransaction().Connection!, (NpgsqlTransaction)tx.GetDbTransaction());

    private static ArgumentException Invalid(string key) => new(key);
}
