using Erp.BuildingBlocks.Infrastructure.Migrations;
using Erp.CodeGen;
using Erp.CodeGen.Dictionary;
using Erp.CodeGen.Emit;
using Npgsql;

namespace Erp.IntegrationTests.Infrastructure;

/// <summary>
/// One empty customer database and one empty control-plane database on a real PostgreSQL 16, migrated with the
/// generated SQL files (slice S1.02). Shared by the tests of one collection and dropped afterwards.
/// </summary>
public sealed class MigratedDatabases : IAsyncLifetime
{
    private readonly PostgresFixture _server = new();
    private ScratchDatabase? _tenant;
    private ScratchDatabase? _controlPlane;

    public static string Root { get; } = RepositoryRoot.Find(AppContext.BaseDirectory);

    public static ExpectedCatalog Catalog { get; } = ExpectedCatalog.Load(Path.Combine(Root, CatalogEmitter.CatalogPath));

    public static IReadOnlyList<string> TenantScripts =>
        SqlScriptRunner.Scripts(Path.Combine(Root, SqlEmitter.KernelMigrations), Path.Combine(Root, SqlEmitter.HrmMigrations));

    public static IReadOnlyList<string> ControlPlaneScripts =>
        SqlScriptRunner.Scripts(Path.Combine(Root, SqlEmitter.ControlPlaneMigrations));

    public string TenantConnectionString => _tenant!.ConnectionString;

    public string ControlPlaneConnectionString => _controlPlane!.ConnectionString;

    public string AdminConnectionString => _server.ConnectionString;

    public string ConnectionStringFor(string database) => database == "tenant" ? TenantConnectionString : ControlPlaneConnectionString;

    public async ValueTask InitializeAsync()
    {
        await _server.InitializeAsync().ConfigureAwait(false);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ct = TestContext.Current.CancellationToken;
        _tenant = await ScratchDatabase.CreateAsync(_server.ConnectionString, "erp_it_tenant_" + suffix, ct).ConfigureAwait(false);
        _controlPlane = await ScratchDatabase.CreateAsync(_server.ConnectionString, "erp_it_cp_" + suffix, ct).ConfigureAwait(false);
        await SqlScriptRunner.ApplyAsync(_tenant.ConnectionString, TenantScripts, ct).ConfigureAwait(false);
        await SqlScriptRunner.ApplyAsync(_controlPlane.ConnectionString, ControlPlaneScripts, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var ct = CancellationToken.None;
        if (_tenant is not null)
        {
            await _tenant.DropAsync(ct).ConfigureAwait(false);
        }

        if (_controlPlane is not null)
        {
            await _controlPlane.DropAsync(ct).ConfigureAwait(false);
        }

        await _server.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<NpgsqlConnection> OpenAsync(string database, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionStringFor(database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async Task<CatalogSnapshot> SnapshotAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(database, cancellationToken).ConfigureAwait(false);
        return await CatalogReader.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
    }
}

[CollectionDefinition(Name)]
public sealed class MigratedDatabaseGroup : ICollectionFixture<MigratedDatabases>
{
    public const string Name = "Migrated databases";
}
