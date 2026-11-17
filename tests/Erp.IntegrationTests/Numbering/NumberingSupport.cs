using System.Data.Common;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Hrm.Infrastructure.Generated;
using Erp.IntegrationTests.Access;
using Erp.Kernel.Contracts.Numbering;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Numbering;
using Npgsql;

namespace Erp.IntegrationTests.Numbering;

/// <summary>
/// The numbering service wired as the API host wires it (slice S1.08), against the access-test customer through its
/// application login (<c>app_rw</c>): the same rights the API has. Each test uses fresh companies, so tests never share
/// a series.
/// </summary>
public sealed class NumberingRig : IAsyncDisposable
{
    private NumberingRig(AccessHarness harness, NpgsqlDataSource app)
    {
        Harness = harness;
        App = app;
        Registry = new DocumentTypeRegistry([new HrmNumberingTypes()]);
        var tenant = new FixedTenant(app);
        Allocator = new NumberAllocator(tenant, harness.Clock, Registry);
        Voids = new NumberVoids(tenant, harness.Clock, new SystemActor(), Registry);
    }

    public AccessHarness Harness { get; }

    /// <summary>Pooled connections of the application login (enough for 100 concurrent transactions).</summary>
    public NpgsqlDataSource App { get; }

    public DocumentTypeRegistry Registry { get; }

    public NumberAllocator Allocator { get; }

    public NumberVoids Voids { get; }

    public static async Task<NumberingRig> StartAsync(AccessHarness harness, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var fx = harness.Fixture;
        var tenant = await fx.TenantAsync(harness.Code, ct);
        var credential = DatabaseCredential.Parse((await fx.Secrets.GetAsync(TenantLogins.SecretRef(tenant.AppSecretRef, TenantLogins.ReadWrite), ct))!);
        var options = new ControlPlaneOptions { ConnectionString = fx.Options.ConnectionString, MaxPoolSizePerTenant = 120, StatementTimeoutSeconds = 30 };
        return new NumberingRig(harness, TenantDataSources.Build(tenant, credential, TenantLogins.ReadWrite, options));
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();

    /// <summary>A scheme written as the database owner would (the scheme services have their own tests).</summary>
    public async Task<Guid> SchemeAsync(Guid? company, string type, string pattern, string reset, bool gapFree = false, long start = 1, Guid? branch = null, CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();
        await Harness.ExecAsync("""
            insert into cfg.numbering_schemes (id, company_id, branch_id, document_type, pattern, reset_rule, gap_free, start_value, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5, $6, $7, $8, 'active', now(), $9, now(), $9)
            """, ct, id, company, branch, type, pattern, reset, gapFree, start, BaselineSeed.SystemUserId);
        return id;
    }

    /// <summary>A second branch of the company (the first is created with the company, rule SC-2).</summary>
    public async Task<(Guid Id, string Code)> BranchAsync(Guid company, CancellationToken ct)
    {
        var code = "B" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var id = Guid.CreateVersion7();
        await Harness.ExecAsync("""
            insert into org.branches (id, company_id, code, name_i18n, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, '{"en":"Test branch"}', now(), $4, now(), $4)
            """, ct, id, company, code, BaselineSeed.SystemUserId);
        return (id, code);
    }

    /// <summary>Takes a number at posting in a transaction of its own and commits or rolls back.</summary>
    public async Task<string> PostAsync(NumberRequest request, bool commit, CancellationToken ct, TimeSpan? holdFor = null)
    {
        await using var connection = await App.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var number = await Allocator.AssignAsync(request, tx, ct);
        if (holdFor is { } hold)
        {
            await Task.Delay(hold, ct);
        }

        if (commit)
        {
            await tx.CommitAsync(ct);
        }
        else
        {
            await tx.RollbackAsync(ct);
        }

        return number;
    }

    private sealed class FixedTenant(DbDataSource source) : ITenantConnectionProvider
    {
        public DbDataSource DataSource => source;
    }

    private sealed class SystemActor : ICurrentActor
    {
        public Guid UserId => BaselineSeed.SystemUserId;
    }
}
