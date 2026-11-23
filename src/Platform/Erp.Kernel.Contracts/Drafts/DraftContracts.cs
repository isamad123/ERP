using System.Text.Json;

namespace Erp.Kernel.Contracts.Drafts;

/// <summary>Limits of form drafts (slice S1.21a; also checked by the database where it can).</summary>
public static class DraftLimits
{
    public const int FormKeyLength = 100;

    /// <summary>Largest draft, before encryption.</summary>
    public const int DataBytes = 65536;

    /// <summary>Drafts one person may keep for one form.</summary>
    public const int PerPersonAndForm = 20;
}

/// <summary>
/// An autosaved draft of a long form (contract "Form drafts API", S1.21a; FR-ADM-UXA-05, S1.12 ruling Q13). The data are the
/// form's own values and the step reached; the server keeps them encrypted and gives them back only to the person who saved them.
/// </summary>
public sealed record DraftItem(Guid Id, string FormKey, Guid? RecordId, JsonElement Data, DateTimeOffset ExpiresAt, DateTimeOffset UpdatedAt, long RowVersion);

/// <summary>Saves the draft of <paramref name="FormKey"/> (and <paramref name="RecordId"/>): creates it or replaces it, and starts the keeping time again.</summary>
public sealed record DraftWrite(string? FormKey, Guid? RecordId, JsonElement? Data);

/// <summary>The outcome of a draft call: the draft, or the error key (and field) the endpoint answers.</summary>
public sealed record DraftOutcome(DraftItem? Draft, int Status, string? ErrorKey = null, string? Field = null)
{
    public static DraftOutcome Ok(DraftItem draft, int status = 200) => new(draft, status);

    public static DraftOutcome Fail(int status, string key, string? field = null) => new(null, status, key, field);
}

/// <summary>
/// Form drafts: the signed-in person's own drafts only; another person's draft is never found. Saving replaces the draft of the
/// same form and record and renews its keeping time (<c>cfg.drafts.keep_days</c>); the daily clean-up removes drafts past it.
/// </summary>
public interface IFormDraftService
{
    Task<IReadOnlyList<DraftItem>> ListAsync(string? formKey, CancellationToken cancellationToken);

    Task<DraftOutcome> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DraftOutcome> SaveAsync(DraftWrite write, CancellationToken cancellationToken);

    /// <summary>Deletes the person's own draft; 404 for one that is not theirs.</summary>
    Task<DraftOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
