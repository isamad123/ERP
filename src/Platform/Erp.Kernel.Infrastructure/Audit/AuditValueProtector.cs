using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Audit;

/// <summary>
/// Protects classified values inside <c>aud.change_log.changes</c> (spec 03 R-21; slice S1.07 Q5, CR-026):
/// <list type="bullet">
/// <item><c>personal</c> and <c>restricted</c> values are encrypted with the data subject's own key (<c>aud.subject_keys</c>,
/// wrapped by the customer's field key); destroying that key makes them unreadable while every hash stays valid.</item>
/// <item><c>salary</c> values are encrypted with the customer's field key: payroll records are kept by law and are not
/// erased (spec 03 section 14.3).</item>
/// <item>Ciphertext and blind-index columns (<c>enc</c>, <c>hmac</c>) are never copied: the entry only says they changed. The
/// same holds for a personal or restricted value whose person is unknown: never the customer key (review S1.07 finding 5).</item>
/// </list>
/// A protected field is stored as <c>{"field", "class", "key": "subject"|"customer"|"erased", "before", "after"}</c> with
/// base64 values; an unprotected one as <c>{"field", "before", "after"}</c>.
/// </summary>
public sealed class AuditValueProtector(NpgsqlConnection connection, NpgsqlTransaction? transaction, IFieldEncryptor customerKey)
{
    public const string KeySubject = "subject";
    public const string KeyCustomer = "customer";
    public const string KeyErased = "erased";
    private const string SubjectKeyPurpose = "aud.subject_keys.wrapped_key";

    private readonly Dictionary<Guid, IFieldEncryptor?> _subjects = [];

    /// <summary>The purpose bound to a protected value: a ciphertext cannot be moved to another table or field.</summary>
    public static string Purpose(string entityType, string field) => "aud.change_log:" + entityType + "." + field;

    /// <summary>One field change as stored; <paramref name="create"/> leaves out "before", <paramref name="delete"/> "after".</summary>
    public async Task<JsonObject> FieldAsync(string entityType, string field, string? fieldClass, bool redacted, JsonNode? before, JsonNode? after, bool create, bool delete, Guid? subject, CancellationToken cancellationToken)
    {
        var change = new JsonObject { ["field"] = field };
        if (redacted)
        {
            change["redacted"] = true;
            return change;
        }

        IFieldEncryptor? key = null;
        if (fieldClass is not null)
        {
            change["class"] = fieldClass;
            if (fieldClass == "salary")
            {
                key = customerKey;
                change["key"] = KeyCustomer;
            }
            else if (subject is null)
            {
                // R-21 (review S1.07 finding 5): a personal value is kept only under its person's key, so erasure can reach
                // it. When the person cannot be found (for example an employment created in the same, uncommitted
                // transaction) the entry says the field changed, without values, as for encrypted columns (R-013).
                change["redacted"] = true;
                return change;
            }
            else
            {
                key = await SubjectKeyAsync(subject.Value, cancellationToken).ConfigureAwait(false);
                change["key"] = key is null ? KeyErased : KeySubject;
                if (key is null)
                {
                    return change;
                }
            }
        }

        if (!create)
        {
            change["before"] = Protect(before, key, entityType, field);
        }

        if (!delete)
        {
            change["after"] = Protect(after, key, entityType, field);
        }

        return change;
    }

    private static JsonNode? Protect(JsonNode? value, IFieldEncryptor? key, string entityType, string field) =>
        key is null || value is null
            ? value?.DeepClone()
            : JsonValue.Create(Convert.ToBase64String(key.Encrypt(Encoding.UTF8.GetBytes(value.ToJsonString()), Purpose(entityType, field))));

    /// <summary>The person's key (created on first use); null when it was destroyed (erasure).</summary>
    private async Task<IFieldEncryptor?> SubjectKeyAsync(Guid person, CancellationToken cancellationToken)
    {
        if (_subjects.TryGetValue(person, out var cached))
        {
            return cached;
        }

        var offered = customerKey.Encrypt(RandomNumberGenerator.GetBytes(32), SubjectKeyPurpose);
        var (wrapped, destroyed) = await AuditDb.EnsureSubjectKeyAsync(connection, transaction, person, offered, cancellationToken).ConfigureAwait(false);
        var key = destroyed ? null : Unwrap(customerKey, wrapped);
        _subjects[person] = key;
        return key;
    }

    internal static IFieldEncryptor Unwrap(IFieldEncryptor customerKey, byte[] wrapped) =>
        new AesGcmFieldEncryptor(customerKey.Decrypt(wrapped, SubjectKeyPurpose), 1);
}

/// <summary>
/// Reads protected values back for an allowed viewer (record history and search, slice S1.07): subject-key values are
/// decrypted with the person's key, or shown as erased when it was destroyed; customer-key values with the field key.
/// </summary>
public sealed class AuditValueReader(NpgsqlConnection connection, IFieldEncryptor customerKey)
{
    private readonly Dictionary<Guid, IFieldEncryptor?> _subjects = [];

    /// <summary>True when the last call decrypted a personal or restricted value (the viewer's access is logged, Q6).</summary>
    public bool DecryptedPersonal { get; private set; }

    /// <summary>The field change with plain values, or with <c>"erased": true</c> and no values when the key is gone.</summary>
    public async Task<JsonObject> ReadAsync(string entityType, JsonObject stored, Guid? subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var change = (JsonObject)stored.DeepClone();
        var field = (string?)change["field"] ?? string.Empty;
        var keyKind = (string?)change["key"];
        IFieldEncryptor? key = keyKind switch
        {
            AuditValueProtector.KeyCustomer => customerKey,
            AuditValueProtector.KeySubject when subject is { } person => await SubjectKeyAsync(person, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
        change.Remove("key");
        if (keyKind is null)
        {
            return change;
        }

        if (key is null)
        {
            change.Remove("before");
            change.Remove("after");
            change["erased"] = true;
            return change;
        }

        foreach (var side in new[] { "before", "after" })
        {
            if (change[side] is JsonValue v && v.TryGetValue<string>(out var b64))
            {
                var plain = key.Decrypt(Convert.FromBase64String(b64), AuditValueProtector.Purpose(entityType, field));
                change[side] = JsonNode.Parse(plain);
            }
        }

        DecryptedPersonal |= keyKind == AuditValueProtector.KeySubject;
        return change;
    }

    private async Task<IFieldEncryptor?> SubjectKeyAsync(Guid person, CancellationToken cancellationToken)
    {
        if (_subjects.TryGetValue(person, out var cached))
        {
            return cached;
        }

        var found = await AuditDb.FindSubjectKeyAsync(connection, person, cancellationToken).ConfigureAwait(false);
        var key = found is { Destroyed: false } k ? AuditValueProtector.Unwrap(customerKey, k.WrappedKey) : null;
        _subjects[person] = key;
        return key;
    }
}
