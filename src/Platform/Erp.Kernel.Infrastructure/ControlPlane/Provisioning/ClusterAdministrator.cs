using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

/// <summary>A PostgreSQL server that hosts customer databases (cp.db_clusters).</summary>
public sealed record ClusterInfo(Guid Id, string Name, string Host, int Port, string AdminSecretRef, string Status, int MaxDatabases);

/// <summary>
/// Server-level work done with the cluster administrator login: the spec 03 section 3.7 group roles, one set of logins
/// per customer (CR-004) and the customer database itself. Every step is safe to repeat.
/// </summary>
public sealed partial class ClusterAdministrator(ISecretStore secrets)
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

            // A database of this name is taken over only if it is the customer's own, from an earlier run of this step (S1.03
            // review finding 12); anything else on the server with that name stops provisioning.
            var owner = await Sql.ScalarAsync<string>(connection, null, "select pg_get_userbyid(datdba) from pg_database where datname = $1", cancellationToken, database).ConfigureAwait(false);
            var expectedOwner = TenantLogins.LoginName(tenantCode, TenantLogins.Owner);
            if (owner is not null && owner != expectedOwner)
            {
                throw new InvalidOperationException($"Database {database} already exists on cluster {cluster.Name} and is owned by {owner}, not {expectedOwner}; provisioning does not take it over.");
            }

            if (owner is null)
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
    public static Task EnsureGroupRolesAsync(NpgsqlConnection connection, CancellationToken cancellationToken) =>
        EnsureNoLoginRolesAsync(connection, TenantLogins.GroupRoles, cancellationToken);

    /// <summary>
    /// Creates each role (no login) unless it exists. Roles are server-wide, so two provisionings on a new server can
    /// create the same role at the same moment; the loser's duplicate error is ignored instead of failing (R-028).
    /// A check before <c>create role</c> cannot prevent that race. An advisory lock could only help if every session
    /// that creates these roles took it in the same database; they do not (the frozen grants migrations run in each
    /// customer database), so the database's own duplicate error is relied on instead.
    /// </summary>
    internal static async Task EnsureNoLoginRolesAsync(NpgsqlConnection connection, IEnumerable<string> roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var names = roles.ToArray();
        foreach (var role in names)
        {
            // CREATE ROLE cannot take parameters, and the statement sits inside a $do$ block: only plain lower-case
            // names are accepted, then quoted as an identifier (R-028 review finding 4).
            if (!RoleNamePattern().IsMatch(role))
            {
                throw new ArgumentException($"'{role}' is not a valid role name.", nameof(roles));
            }

            await Sql.ExecuteAsync(connection, null,
                $"do $do$ begin create role {Sql.Identifier(role)} nologin; exception when duplicate_object or unique_violation then null; end $do$",
                cancellationToken).ConfigureAwait(false);
        }

        // A role of the same name that already existed is accepted only if it is a plain group role, as CR-004 creates
        // them: no login, no special powers and no membership of other roles. Otherwise every customer login made a
        // member of it would silently gain those powers (R-028 review finding 3).
        var unsafeRoles = await Sql.ScalarAsync<string>(connection, null,
            """
            select string_agg(r.rolname, ', ' order by r.rolname)
            from pg_roles r
            where r.rolname = any($1)
              and (r.rolcanlogin or r.rolsuper or r.rolcreaterole or r.rolcreatedb or r.rolreplication or r.rolbypassrls
                   or exists (select 1 from pg_auth_members m where m.member = r.oid))
            """,
            cancellationToken, [names]).ConfigureAwait(false);
        if (unsafeRoles is not null)
        {
            throw new InvalidOperationException(
                $"The server already has role(s) {unsafeRoles} with login, special powers or memberships; customer logins would inherit them. " +
                "Remove those rights (or the roles) on the server and run provisioning again.");
        }
    }

    /// <summary>
    /// The SCRAM-SHA-256 verifier PostgreSQL stores for <paramref name="password"/> (RFC 5802, RFC 7677; the format of
    /// pg_authid.rolpassword): <c>SCRAM-SHA-256$iterations:salt$StoredKey:ServerKey</c>. Generated passwords are ASCII, so
    /// SASLprep leaves them unchanged.
    /// </summary>
    internal static string ScramVerifier(string password, byte[] salt, int iterations = 4096)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        if (!password.All(char.IsAscii))
        {
            throw new ArgumentException("Only ASCII database passwords are supported.", nameof(password));
        }

        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var storedKey = SHA256.HashData(HMACSHA256.HashData(salted, "Client Key"u8));
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        CryptographicOperations.ZeroMemory(salted);
        return string.Create(CultureInfo.InvariantCulture,
            $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}");
    }

    [GeneratedRegex(@"\A[a-z_][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleNamePattern();

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
        // format() quotes the name (%I) and the verifier (%L) on the server; nothing is concatenated in C#. The server is
        // given a SCRAM-SHA-256 verifier, never the password, so the password cannot appear in a server log (S1.03 review
        // finding 7); PostgreSQL stores a verifier as given.
        var exists = await Sql.ScalarAsync<bool>(connection, null, "select exists (select 1 from pg_roles where rolname = $1)", cancellationToken, credential.Username).ConfigureAwait(false);
        var statement = await Sql.ScalarAsync<string>(connection, null,
            exists ? "select format('alter role %I with login inherit password %L', $1, $2)" : "select format('create role %I with login inherit password %L', $1, $2)",
            cancellationToken, credential.Username, ScramVerifier(credential.Password, RandomNumberGenerator.GetBytes(16))).ConfigureAwait(false);
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
