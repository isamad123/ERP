using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

/// <summary>A PostgreSQL server that hosts customer databases (cp.db_clusters).</summary>
public sealed record ClusterInfo(Guid Id, string Name, string Host, int Port, string AdminSecretRef, string Status, int MaxDatabases);

/// <summary>
/// Server-level work done with the cluster administrator login: the spec 03 section 3.7 group roles, one set of logins
/// per customer (CR-004) and the customer database itself. Every step is safe to repeat.
/// </summary>
public sealed class ClusterAdministrator(ISecretStore secrets)
{
    public async Task<NpgsqlConnection> OpenAdminAsync(ClusterInfo cluster, string database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var admin = DatabaseCredential.Parse(await secrets.GetAsync(cluster.AdminSecretRef, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No administrator credential is stored for cluster {cluster.Name}."));
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = cluster.Host,
            Port = cluster.Port,
            Database = database,
            Username = admin.Username,
            Password = admin.Password,
            Pooling = false,
            ApplicationName = "erp-provisioning",
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Creates the customer's logins (passwords to the secret store first) and its database, and closes it to everyone else.</summary>
    public async Task CreateDatabaseAsync(ClusterInfo cluster, string tenantCode, string appSecretRef, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        TenantCodes.Validate(tenantCode);
        var database = TenantLogins.DatabaseName(tenantCode);
        await using (var connection = await OpenAdminAsync(cluster, "postgres", cancellationToken).ConfigureAwait(false))
        {
            await EnsureGroupRolesAsync(connection, cancellationToken).ConfigureAwait(false);
            foreach (var (kind, groupRole) in TenantLogins.All)
            {
                var credential = await EnsureCredentialAsync(TenantLogins.SecretRef(appSecretRef, kind), TenantLogins.LoginName(tenantCode, kind), cancellationToken).ConfigureAwait(false);
                await EnsureLoginAsync(connection, credential, groupRole, cancellationToken).ConfigureAwait(false);
            }

            var exists = await Sql.ScalarAsync<bool>(connection, null, "select exists (select 1 from pg_database where datname = $1)", cancellationToken, database).ConfigureAwait(false);
            if (!exists)
            {
                // CREATE DATABASE cannot take parameters; both names were validated by TenantCodes.Validate.
                await Sql.ExecuteAsync(connection, null,
                    $"create database {Sql.Identifier(database)} owner {Sql.Identifier(TenantLogins.LoginName(tenantCode, TenantLogins.Owner))} encoding 'UTF8' template template0",
                    cancellationToken).ConfigureAwait(false);
            }

            await Sql.ExecuteAsync(connection, null, $"revoke all on database {Sql.Identifier(database)} from public", cancellationToken).ConfigureAwait(false);
            foreach (var (kind, _) in TenantLogins.All)
            {
                var privileges = kind is TenantLogins.Owner ? "connect, create, temporary" : "connect";
                await Sql.ExecuteAsync(connection, null,
                    $"grant {privileges} on database {Sql.Identifier(database)} to {Sql.Identifier(TenantLogins.LoginName(tenantCode, kind))}",
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The spec 03 section 3.7 roles (no login); customer logins are members (CR-004).</summary>
    public static async Task EnsureGroupRolesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var role in TenantLogins.GroupRoles)
        {
            await Sql.ExecuteAsync(connection, null,
                $"do $do$ begin if not exists (select 1 from pg_roles where rolname = '{role}') then create role {role} nologin; end if; end $do$",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DatabaseCredential> EnsureCredentialAsync(string secretRef, string login, CancellationToken cancellationToken)
    {
        var stored = await secrets.GetAsync(secretRef, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            return DatabaseCredential.Parse(stored);
        }

        var credential = new DatabaseCredential(login, DatabaseCredential.NewPassword());
        await secrets.PutAsync(secretRef, credential.ToSecret(), cancellationToken).ConfigureAwait(false);
        return credential;
    }

    private static async Task EnsureLoginAsync(NpgsqlConnection connection, DatabaseCredential credential, string? groupRole, CancellationToken cancellationToken)
    {
        // format() quotes the name (%I) and the password (%L) on the server; nothing is concatenated in C#.
        var exists = await Sql.ScalarAsync<bool>(connection, null, "select exists (select 1 from pg_roles where rolname = $1)", cancellationToken, credential.Username).ConfigureAwait(false);
        var statement = await Sql.ScalarAsync<string>(connection, null,
            exists ? "select format('alter role %I with login inherit password %L', $1, $2)" : "select format('create role %I with login inherit password %L', $1, $2)",
            cancellationToken, credential.Username, credential.Password).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, null, statement!, cancellationToken).ConfigureAwait(false);
        if (groupRole is not null)
        {
            await Sql.ExecuteAsync(connection, null, $"grant {groupRole} to {Sql.Identifier(credential.Username)}", cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Customer codes become host names, database names and login names, so they are restricted to a safe form.</summary>
public static class TenantCodes
{
    public static void Validate(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length is < 2 or > 30 || code[0] is < 'a' or > 'z' || !code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')))
        {
            throw new ArgumentException($"Customer code '{code}' must be 2 to 30 lower-case letters or digits, starting with a letter.", nameof(code));
        }
    }
}
