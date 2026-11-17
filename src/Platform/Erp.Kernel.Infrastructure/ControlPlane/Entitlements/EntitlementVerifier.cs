using Erp.Kernel.Domain.Entitlements;
using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Erp.Kernel.Infrastructure.Entitlements;
using Npgsql;

namespace Erp.Kernel.Infrastructure.ControlPlane.Entitlements;

/// <summary>
/// The checks every signed entitlement passes before the platform stores it, at provisioning and at <c>entitlement
/// apply</c> (FR-KRN-ENT-01; slice S1.09 rulings Q1 and Q3; spec 02 section 25 entitlement testing). Each refusal names
/// its reason: an unknown signing key, a revoked key, a key outside its validity, a signature that does not match the
/// payload (tampered), claims that break the key catalogue.
/// </summary>
public static class EntitlementVerifier
{
    /// <summary>Checks the signing key and the signature, then the claims; returns the parsed claims.</summary>
    public static async Task<EntitlementClaims> VerifyAsync(NpgsqlConnection controlPlane, NpgsqlTransaction? transaction, SignedEntitlement entitlement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        await using (var command = Sql.Command(controlPlane, transaction,
            "select public_key, valid_from, valid_to, revoked_at from cp.signing_keys where key_id = $1 and purpose = 'entitlement'", [entitlement.KeyId]))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Signing key '{entitlement.KeyId}' is not a registered entitlement key.");
            }

            var publicKey = reader.GetFieldValue<byte[]>(0);
            if (!reader.IsDBNull(3))
            {
                throw new InvalidOperationException($"Signing key '{entitlement.KeyId}' was revoked; entitlements it signed are refused.");
            }

            if (reader.GetFieldValue<DateTimeOffset>(1) > now || (!reader.IsDBNull(2) && reader.GetFieldValue<DateTimeOffset>(2) <= now))
            {
                throw new InvalidOperationException($"Signing key '{entitlement.KeyId}' is not valid at this time.");
            }

            if (!entitlement.Verify(publicKey))
            {
                throw new InvalidOperationException("The entitlement signature is not valid (the file was changed after signing, or signed with another key).");
            }
        }

        return EntitlementCatalogs.Parse(entitlement.Payload.Claims);
    }
}
