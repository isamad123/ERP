using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Contracts.Metadata;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Access;

/// <summary>
/// Tells the kernel whether the target of an <c>employee</c> custom field exists (slice S1.15b, FR-KRN-MET-02): any employment of the
/// customer, whatever its status, so an old value stays valid when the employee has left.
/// </summary>
public sealed class EmployeeReferenceResolver(ITenantConnectionProvider tenant) : ICustomReferenceResolver
{
    public string DataType => CustomFieldTypes.Employee;

    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await tenant.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select exists (select 1 from hrm.employments where id = @id)", connection);
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
