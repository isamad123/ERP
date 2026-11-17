using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Domain.Numbering;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Kernel.Infrastructure.Numbering;

/// <summary>
/// Records voided numbers (FR-KRN-NUM-05, FR-XM-06; slice S1.08 Q10). A cancelled document keeps its number; the module
/// calls this in the cancel's transaction. The void belongs to the scheme whose pattern produced the number (the active
/// one first, then the newest); a number no scheme produced (a manual one) is recorded against the active scheme. Rows
/// are append-only (trigger) and one per number (unique key), so the same number cannot be voided twice.
/// </summary>
public sealed class NumberVoids(ITenantConnectionProvider tenant, IClock clock, ICurrentActor actor, DocumentTypeRegistry registry) : INumberVoids
{
    public const int MaxReasonLength = 500;

    public async Task<Guid> RecordAsync(VoidRequest request, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = registry.Get(request.DocumentType);
        if (type.TenantWide ? request.CompanyId is not null : request.CompanyId is null)
        {
            throw new NumberingException(type.TenantWide ? "errors.numbering.company_not_allowed" : "errors.numbering.company_required");
        }

        var number = request.Number?.Trim();
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(number))
        {
            throw new NumberingException("errors.numbering.number_empty");
        }

        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
        {
            throw new NumberingException("errors.numbering.void_reason_required");
        }

        NpgsqlConnection? own = null;
        var tx = transaction as NpgsqlTransaction;
        if (transaction is not null && tx?.Connection is null)
        {
            throw new ArgumentException("The transaction must be an open PostgreSQL transaction.", nameof(transaction));
        }

        try
        {
            var connection = tx?.Connection ?? (own = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false));
            var now = clock.UtcNow;
            var branch = type.HasBranch ? request.BranchId : null;
            var schemes = (await NumberingDb.AllAsync(connection, tx, type.Code, request.CompanyId, cancellationToken).ConfigureAwait(false))
                .Where(s => s.BranchId is null || s.BranchId == branch)
                .OrderByDescending(s => s.Status == "active").ThenByDescending(s => s.BranchId is not null).ThenByDescending(s => s.CreatedAt)
                .ToList();
            SchemeRow? owner = null;
            foreach (var scheme in schemes)
            {
                var context = await NumberingDb.ContextAsync(connection, tx, scheme, null, now, branch, cancellationToken).ConfigureAwait(false);
                if (NumberPattern.Parse(scheme.Pattern).TryMatch(number, scheme.ResetRule, context.FiscalYearStartMonth, context.CompanyCode, context.BranchCode, context.Date, out _, out _))
                {
                    owner = scheme;
                    break;
                }
            }

            owner ??= schemes.FirstOrDefault(s => s.Status == "active") ?? throw new NumberingException("errors.numbering.no_active_scheme");
            var id = Guid.CreateVersion7(now);
            await using var command = Sql.Command(connection, tx, """
                insert into cfg.numbering_voids (id, scheme_id, number, reason, document_id, created_at, created_by)
                values ($1, $2, $3, $4, $5::uuid, $6, $7)
                """, [id, owner.Id, number, reason, request.DocumentId, now, actor.UserId]);
            command.Parameters[4].NpgsqlDbType = NpgsqlDbType.Uuid;
            if (tx is not null)
            {
                await tx.SaveAsync("erp_numbering_void", cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                if (tx is not null)
                {
                    await tx.RollbackAsync("erp_numbering_void", cancellationToken).ConfigureAwait(false);
                }

                throw new NumberingException("errors.numbering.already_voided");
            }

            if (tx is not null)
            {
                await tx.ReleaseAsync("erp_numbering_void", cancellationToken).ConfigureAwait(false);
            }

            return id;
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
