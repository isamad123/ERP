using System.Data.Common;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>Shared parts of the two held-change handlers of the pay set-up (slice S1.34b): the context on the approval's transaction.</summary>
internal static class PayHeldChanges
{
    public const string StructureKind = "hrm.salary_structure.change";
    public const string CompensationKind = "hrm.compensation.change";

    /// <summary>An HR context on the approval's connection and transaction, so rows and audit entries commit with the decision.</summary>
    public static async Task<HrmDbContext> OnAsync(IServiceProvider services, DbTransaction transaction, CancellationToken ct)
    {
        var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        return db;
    }
}
