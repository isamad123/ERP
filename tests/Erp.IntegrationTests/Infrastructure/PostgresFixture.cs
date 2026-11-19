using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests.Infrastructure;

/// <summary>
/// A real PostgreSQL 16 for integration tests (CLAUDE.md: never mock the database).
/// 1. If ERP_TEST_PG holds a connection string (CI service container, or a local server), that server is used.
/// 2. Otherwise a postgres:16 container is started with Testcontainers (needs Docker).
/// If neither is available the tests fail with an explanation; they are never silently skipped.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "ERP_TEST_PG";
    public const string Image = "postgres:16";

    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = configured;
        }
        else
        {
            try
            {
                _container = new PostgreSqlBuilder(Image).Build();
                await _container.StartAsync().ConfigureAwait(false);
                ConnectionString = _container.GetConnectionString();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidOperationException(
                    $"No PostgreSQL available for integration tests. Set {ConnectionStringVariable} to a PostgreSQL 16 " +
                    "connection string, or start Docker so Testcontainers can run postgres:16. See CLAUDE.md 'Commands'.", ex);
            }
        }

        // R-028: the server-wide group roles (CR-004) are created here, race-safely, before any fixture applies the
        // frozen grants migrations. Those migrations create a missing role with a check-then-create that fails when
        // two fixtures run them at once on a new server; in production provisioning always creates the roles first.
        await using var connection = await OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        await ClusterAdministrator.EnsureGroupRolesAsync(connection, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
