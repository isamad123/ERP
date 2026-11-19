using System.Collections.Concurrent;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Tenancy;

/// <summary>
/// One pooled data source per customer database and login (FR-KRN-TEN-03). The credential comes from the secret store;
/// every connection gets the configured statement timeout. A data source can only reach the one database named in
/// its connection string, and its login can connect to no other customer's database (CR-004).
/// </summary>
public sealed class TenantDataSources(ControlPlaneOptions options, ISecretStore secrets) : ITenantDataSourceFactory, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, (string Address, NpgsqlDataSource Source)> _sources = new(StringComparer.Ordinal);
    private readonly List<NpgsqlDataSource> _retired = [];

    public async ValueTask<NpgsqlDataSource> GetAsync(ResolvedTenant tenant, string loginKind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var secret = await secrets.GetAsync(TenantLogins.SecretRef(tenant.AppSecretRef, loginKind), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No {loginKind} credential is stored for customer {tenant.Code}.");
        var key = tenant.Code + "/" + loginKind;
        // A pool is reused only for the same server, database and credential (S1.03 review finding 6).
        var address = string.Join('\n', tenant.ClusterHost, tenant.ClusterPort.ToString(System.Globalization.CultureInfo.InvariantCulture), tenant.DatabaseName, secret);
        if (_sources.TryGetValue(key, out var existing) && existing.Address == address)
        {
            return existing.Source;
        }

        var source = Build(tenant, DatabaseCredential.Parse(secret), loginKind, options);
        lock (_retired)
        {
            if (_sources.TryGetValue(key, out existing))
            {
                _retired.Add(existing.Source); // rotated credential or moved database: keep the old pool alive for requests in flight
            }

            _sources[key] = (address, source);
        }

        return source;
    }

    async ValueTask<System.Data.Common.DbDataSource> ITenantDataSourceFactory.GetAsync(ResolvedTenant tenant, CancellationToken cancellationToken) =>
        await GetAsync(tenant, TenantLogins.ReadWrite, cancellationToken).ConfigureAwait(false);

    public static NpgsqlDataSource Build(ResolvedTenant tenant, DatabaseCredential credential, string loginKind, ControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = tenant.ClusterHost,
            Port = tenant.ClusterPort,
            Database = tenant.DatabaseName,
            Username = credential.Username,
            Password = credential.Password,
            Pooling = true,
            MaxPoolSize = options.MaxPoolSizePerTenant,
            ApplicationName = "erp-" + loginKind,
            Options = $"-c statement_timeout={options.StatementTimeoutSeconds * 1000}",
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, source) in _sources.Values)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var source in _retired)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
