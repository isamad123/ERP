using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Provisioning;

/// <summary>
/// One provisioning run per customer at a time (R-031, slice S1.11f2): a session advisory lock in the control-plane
/// database, keyed by the customer code. Every provisioning run of an installation uses the same control-plane database,
/// so the lock covers them all; different customers use different keys and never wait for each other. The lock lives on
/// its own unpooled connection and is held for the whole run, which spans many transactions and connections; if the
/// process dies, the session ends and the lock with it. The session must not be ended by an idle limit and must reach the
/// server directly (not through a transaction-pooling proxy), so the provisioner checks after every step that the lock
/// is still held (S1.11f2 review finding 2).
/// </summary>
public sealed class ProvisioningLock : IAsyncDisposable
{
    /// <summary>First half of the two-part advisory-lock key; the second is hashtext(customer code).</summary>
    public const string LockClass = "cp.tenants.provision";

    private readonly NpgsqlConnection _connection;
    private readonly string _code;

    private ProvisioningLock(NpgsqlConnection connection, string code) => (_connection, _code) = (connection, code);

    /// <summary>
    /// Takes the customer's lock. When another run holds it, <paramref name="waiting"/> is called once and this run waits,
    /// without a time limit, until that run ends or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static async Task<ProvisioningLock> AcquireAsync(string controlPlaneConnectionString, string code, Func<ValueTask>? waiting, CancellationToken cancellationToken)
    {
        TenantCodes.Validate(code);
        var builder = new NpgsqlConnectionStringBuilder(controlPlaneConnectionString)
        {
            Pooling = false,
            ApplicationName = "erp-provision-lock",
            KeepAlive = 30, // a dead connection is noticed instead of the run carrying on without its lock
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // The wait has no time limit (it ends with the other run or a cancel), and the idle session holding the lock
            // must not be ended by the server while provisioning works on other connections.
            await using (var limits = new NpgsqlCommand("set statement_timeout = 0; set lock_timeout = 0; set idle_session_timeout = 0; set idle_in_transaction_session_timeout = 0", connection))
            {
                await limits.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await LockAsync(connection, "pg_try_advisory_lock", code, cancellationToken).ConfigureAwait(false))
            {
                if (waiting is not null)
                {
                    await waiting().ConfigureAwait(false);
                }

                await LockAsync(connection, "pg_advisory_lock", code, cancellationToken).ConfigureAwait(false);
            }

            return new ProvisioningLock(connection, code);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Stops the run if this session no longer holds the customer's lock (the session was ended, or a proxy moved it to
    /// another server process): another run may then be working on the same customer.
    /// </summary>
    public async Task EnsureHeldAsync(CancellationToken cancellationToken)
    {
        bool held;
        try
        {
            await using var command = new NpgsqlCommand("""
                select exists (select 1 from pg_locks where locktype = 'advisory' and pid = pg_backend_pid() and granted
                    and classid = hashtext($1)::oid and objid = hashtext($2)::oid and objsubid = 2)
                """, _connection);
            command.Parameters.Add(new NpgsqlParameter { Value = LockClass });
            command.Parameters.Add(new NpgsqlParameter { Value = _code });
            held = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            throw new ProvisioningLockLostException(_code, ex);
        }

        if (!held)
        {
            throw new ProvisioningLockLostException(_code, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await LockAsync(_connection, "pg_advisory_unlock", _code, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // The session is gone, and its locks with it; closing the connection below is all that is left. A run that lost
            // its lock earlier has already been stopped by EnsureHeldAsync after the step where it happened.
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> LockAsync(NpgsqlConnection connection, string function, string code, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // the function name is one of three constants of this class; the key values are parameters
        await using var command = new NpgsqlCommand($"select {function}(hashtext($1), hashtext($2))::text", connection) { CommandTimeout = 0 };
#pragma warning restore CA2100
        command.Parameters.Add(new NpgsqlParameter { Value = LockClass });
        command.Parameters.Add(new NpgsqlParameter { Value = code });
        // pg_advisory_lock returns void (""); the try and unlock forms return a boolean.
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not "false";
    }
}

/// <summary>The provisioning run no longer holds its customer's lock and stops before its next step (R-031).</summary>
public sealed class ProvisioningLockLostException(string code, Exception? inner) :
    Exception($"Provisioning of {code} lost its lock in the control-plane database (the session was ended); run the command again to resume.", inner);
