using System.Data.Common;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Domain.Numbering;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Numbering;

/// <summary>
/// The kernel numbering service (FR-KRN-NUM-02, 03, 06; slice S1.08 build item 2). Finds the scheme (the document's
/// branch first, else the company-wide one, Q6; a company with no scheme at all gets its default, Q7), works out the period
/// from the document's business date (or today in the company's time zone, Q5), takes the next value through
/// <c>cfg.numbering_next</c> and renders the number (Q4).
/// <list type="bullet">
/// <item>Gap-allowed: on its own short connection outside any business transaction, so callers never wait on each other
/// and a failed save leaves a gap that is never filled (Q2).</item>
/// <item>Gap-free: inside the caller's transaction, as its last step; another poster waits at most 5 seconds (Q3).</item>
/// </list>
/// </summary>
public sealed class NumberAllocator(ITenantConnectionProvider tenant, IClock clock, DocumentTypeRegistry registry) : INumberAllocator
{
    /// <summary>SQLSTATE lock_not_available: the gap-free counter stayed locked for longer than the 5-second limit.</summary>
    private const string LockNotAvailable = "55P03";

    public async Task<string> AssignOnCreateAsync(NumberRequest request, CancellationToken cancellationToken)
    {
        var type = Check(request);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var scheme = await ResolveAsync(connection, type, request, cancellationToken).ConfigureAwait(false);
            if (scheme.GapFree)
            {
                throw new NumberingException("errors.numbering.numbered_at_posting");
            }

            try
            {
                return await TakeAsync(connection, null, scheme, request, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == NumberingDb.SchemeChangedState && attempt == 0)
            {
                // The scheme was retired or edited between reading and taking: read it again once.
            }
        }
    }

    public async Task<string> AssignAsync(NumberRequest request, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var type = Check(request);
        if (transaction is not NpgsqlTransaction tx || tx.Connection is not { } callerConnection)
        {
            throw new ArgumentException("A number at posting needs the caller's open PostgreSQL transaction.", nameof(transaction));
        }

        var branch = type.HasBranch ? request.BranchId : null;
        for (var attempt = 0; ; attempt++)
        {
            // Read in the caller's transaction, so a gap-free post needs no second connection.
            var scheme = await NumberingDb.ActiveAsync(callerConnection, tx, type.Code, request.CompanyId, branch, cancellationToken).ConfigureAwait(false);
            if (scheme is null || !scheme.GapFree)
            {
                // Gap-allowed (or no scheme yet): taken outside the caller's transaction, like a number on create.
                await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
                scheme ??= await ResolveAsync(connection, type, request, cancellationToken).ConfigureAwait(false);
                if (!scheme.GapFree)
                {
                    try
                    {
                        return await TakeAsync(connection, null, scheme, request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (PostgresException ex) when (ex.SqlState == NumberingDb.SchemeChangedState && attempt == 0)
                    {
                        continue;
                    }
                }
            }

            // Gap-free: in the caller's transaction, its last step. A savepoint keeps the transaction usable if the scheme
            // changed under us; a lock wait beyond 5 seconds fails the post (the caller rolls back, nothing is saved).
            await tx.SaveAsync("erp_numbering", cancellationToken).ConfigureAwait(false);
            try
            {
                var number = await TakeAsync(callerConnection, tx, scheme, request, cancellationToken).ConfigureAwait(false);
                await tx.ReleaseAsync("erp_numbering", cancellationToken).ConfigureAwait(false);
                return number;
            }
            catch (PostgresException ex) when (ex.SqlState == NumberingDb.SchemeChangedState && attempt == 0)
            {
                await tx.RollbackAsync("erp_numbering", cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
            {
                throw new NumberingException("errors.numbering.busy");
            }
        }
    }

    public async Task<ManualNumberCheck> CheckManualAsync(NumberRequest request, string number, CancellationToken cancellationToken)
    {
        var type = Check(request);
        if (!type.OverrideAllowed)
        {
            return new ManualNumberCheck(false, "errors.numbering.override_not_allowed");
        }

        var typed = number?.Trim();
        if (string.IsNullOrEmpty(typed))
        {
            return new ManualNumberCheck(false, "errors.numbering.number_empty");
        }

        if (typed.Length > NumberPattern.MaxLength || typed.Any(char.IsControl))
        {
            return new ManualNumberCheck(false, "errors.numbering.number_too_long");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var scheme = await ResolveAsync(connection, type, request, cancellationToken).ConfigureAwait(false);
        if (scheme.GapFree)
        {
            return new ManualNumberCheck(false, "errors.numbering.override_gap_free");
        }

        var now = clock.UtcNow;
        var context = await NumberingDb.ContextAsync(connection, null, scheme, request.Date, now, request.BranchId, cancellationToken).ConfigureAwait(false);
        var pattern = NumberPattern.Parse(scheme.Pattern);
        if (!pattern.TryMatch(typed, scheme.ResetRule, context.FiscalYearStartMonth, context.CompanyCode, context.BranchCode, context.Date, out var value, out var scope))
        {
            return new ManualNumberCheck(true, null);
        }

        var issued = (await NumberingDb.SeriesAsync(connection, null, scheme.Id, cancellationToken).ConfigureAwait(false)).FirstOrDefault(p => p.ScopeKey == scope);
        var next = issued is null ? scheme.StartValue : issued.LastValue + 1;
        return value >= next ? new ManualNumberCheck(false, "errors.numbering.manual_in_series") : new ManualNumberCheck(true, null);
    }

    /// <summary>The document type, with its company rule (tenant-wide types have none, every other type needs one, Q6).</summary>
    private DocumentType Check(NumberRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = registry.Get(request.DocumentType);
        if (type.TenantWide ? request.CompanyId is not null : request.CompanyId is null)
        {
            throw new NumberingException(type.TenantWide ? "errors.numbering.company_not_allowed" : "errors.numbering.company_required");
        }

        return type;
    }

    private async Task<SchemeRow> ResolveAsync(NpgsqlConnection connection, DocumentType type, NumberRequest request, CancellationToken cancellationToken)
    {
        var branch = type.HasBranch ? request.BranchId : null;
        var scheme = await NumberingDb.ActiveAsync(connection, null, type.Code, request.CompanyId, branch, cancellationToken).ConfigureAwait(false);
        if (scheme is not null)
        {
            return scheme;
        }

        // A company added after the last migrate gets its default scheme on first use (Q7).
        await using (var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await NumberingDb.EnsureDefaultAsync(connection, tx, type, request.CompanyId, clock.UtcNow, audited: true, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new NumberingException("errors.numbering.company_unknown");
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await NumberingDb.ActiveAsync(connection, null, type.Code, request.CompanyId, branch, cancellationToken).ConfigureAwait(false)
            ?? throw new NumberingException("errors.numbering.no_active_scheme");
    }

    private async Task<string> TakeAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, SchemeRow scheme, NumberRequest request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var context = await NumberingDb.ContextAsync(connection, tx, scheme, request.Date, now, request.BranchId, cancellationToken).ConfigureAwait(false);
        var scope = NumberPattern.ScopeKey(scheme.ResetRule, context.Date, context.FiscalYearStartMonth);
        var value = await NumberingDb.NextAsync(connection, tx, scheme, scope, now, cancellationToken).ConfigureAwait(false);
        try
        {
            return NumberPattern.Parse(scheme.Pattern).Render(value, context);
        }
        catch (NumberPatternException ex)
        {
            throw new NumberingException(ex.ErrorKey);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
}
