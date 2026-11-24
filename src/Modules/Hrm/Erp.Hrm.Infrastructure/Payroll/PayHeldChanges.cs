using System.Data.Common;
using System.Text;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.Hrm.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>Shared parts of the two held-change handlers of the pay set-up (slice S1.34b): the context on the approval's transaction.</summary>
internal static class PayHeldChanges
{
    public const string StructureKind = "hrm.salary_structure.change";
    public const string CompensationKind = "hrm.compensation.change";
    public const string ApproverCannotSee = "errors.hrm.pay.approver_cannot_see_amounts";

    private const string SealPurpose = "hrm.pay.held_change";

    /// <summary>
    /// The payload kept in the kernel's request table: the plan sealed with the customer's data key. A plan holds amounts, which are
    /// salary-class data (GHR-06), and the kernel table has no field class, so nothing readable is kept there (review S1.34b H2).
    /// </summary>
    public static string Seal<T>(IFieldEncryptor enc, T plan)
    {
        var cipher = enc.Encrypt(JsonSerializer.SerializeToUtf8Bytes(plan, JsonSerializerOptions.Web), SealPurpose);
        return JsonSerializer.Serialize(new SealedPlan(1, Convert.ToBase64String(cipher)), JsonSerializerOptions.Web);
    }

    /// <summary>Reads a sealed plan back; null when the payload is not a sealed plan of this kind.</summary>
    public static T? Open<T>(IFieldEncryptor enc, string payload)
        where T : class
    {
        try
        {
            var sealedPlan = JsonSerializer.Deserialize<SealedPlan>(payload, JsonSerializerOptions.Web);
            return sealedPlan is null || sealedPlan.V != 1
                ? null
                : JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(enc.Decrypt(Convert.FromBase64String(sealedPlan.Sealed), SealPurpose)), JsonSerializerOptions.Web);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private sealed record SealedPlan(int V, string Sealed);

    /// <summary>An HR context on the approval's connection and transaction, so rows and audit entries commit with the decision.</summary>
    public static async Task<HrmDbContext> OnAsync(IServiceProvider services, DbTransaction transaction, CancellationToken ct)
    {
        var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(transaction.Connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        return db;
    }
}
