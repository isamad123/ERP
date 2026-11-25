using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Access;

/// <summary>
/// Permissions of the API client service (slice S1.27c, FR-ADM-SEC-02): the person who looks after the keys of connected systems.
/// Managing is sensitive: a key is sign-in code and opens the API without a person. An API client itself can never hold either.
/// </summary>
public static class ApiClientPermissions
{
    /// <summary>See the API clients, their scopes, limits and use (never a secret).</summary>
    public const string Read = "iam.api_clients.read";

    /// <summary>Create, rotate and revoke API clients; told when a key expires or a client hits its limit.</summary>
    public const string Manage = "iam.api_clients.manage";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Read, "iam", "permissions.iam.api_clients.read", false),
        new(Manage, "iam", "permissions.iam.api_clients.manage", true),
    ];
}

/// <summary>Codes of the API client service.</summary>
public static class ApiClientCodes
{
    /// <summary>The authentication scheme: <c>Authorization: ApiKey &lt;client_id&gt;.&lt;secret&gt;</c>.</summary>
    public const string Scheme = "ApiKey";

    /// <summary>The token claim that marks a key principal (<c>sub</c> is the client row's id, never a user).</summary>
    public const string ActorClaim = "actor_kind";

    public const string ActorClaimValue = "api_client";

    /// <summary>The scopes of the key, one claim each.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>Held-change kinds (maker-checker, FR-KRN-IAM-14) while <c>iam.maker_checker.sensitive_roles</c> is on.</summary>
    public const string HeldCreate = "iam.api_client.create";

    public const string HeldRotate = "iam.api_client.rotate";

    public const string Table = "iam.api_clients";

    public const string ExpiryJob = "kernel.api_client_expiry";
}

/// <summary>Events of the API client service: audited lifecycle events and the two notices of the notification catalogue.</summary>
public static class ApiClientEvents
{
    public const string Created = "iam.api_client_created";
    public const string Rotated = "iam.api_client_rotated";
    public const string Revoked = "iam.api_client_revoked";
    public const string Expired = "iam.api_client_expired";

    /// <summary>A client used every call its limit allows in a minute (one notice per client and day).</summary>
    public const string LimitReached = "iam.api_client.limit_reached";

    /// <summary>A client's key is within <c>iam.api_client.expiry_notice_days</c> of its end (one notice per expiry date).</summary>
    public const string Expiring = "iam.api_client.expiring";
}

/// <summary>What the person asks for when creating a client (FR-ADM-SEC-02): the scopes are permission codes flagged <c>api_client</c>.</summary>
public sealed record ApiClientCreateRequest(string Name, IReadOnlyList<string> Scopes, IReadOnlyList<string> IpAllow, int RateLimitPerMinute, DateTimeOffset? ExpiresAt);

/// <summary>An API client as lists and reads show it: never a secret, never a hash.</summary>
public sealed record ApiClientView(
    Guid Id,
    string ClientId,
    string Name,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> IpAllow,
    int RateLimitPerMinute,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    string Status,
    DateTimeOffset? PreviousSecretValidUntil,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// The answer to creating or rotating: the secret, shown this once. While the change waits for a second person (<see cref="HeldRequestId"/> set) the
/// key does not work yet; it works as soon as the request is approved, and the secret is never kept or shown again.
/// </summary>
public sealed record ApiClientSecret(Guid? Id, string ClientId, string Secret, string ApiKey, Guid? HeldRequestId, DateTimeOffset? ValidUntil);

/// <summary>One day of use of one client (FR-ADM-SEC-02, FR-KRN-INT-07): calls, errors, the 95th percentile of the answer time and the last use.</summary>
public sealed record ApiClientUsageDay(DateOnly Date, int Calls, int Errors4xx, int Errors5xx, int RateLimited, int P95Ms, int MaxMs);

/// <summary>The usage of a client over a number of days, newest first, with the last use.</summary>
public sealed record ApiClientUsage(Guid ClientId, DateTimeOffset? LastUsedAt, IReadOnlyList<ApiClientUsageDay> Days);

/// <summary>A permission a client may be given as a scope (the manifest flag <c>api_client</c>).</summary>
public sealed record ApiClientScopeOption(string Code, string Module, string DescriptionKey);

/// <summary>A refusal of the API client service: the HTTP status and a localization key.</summary>
public sealed class ApiClientException(int status, string errorKey) : Exception(errorKey)
{
    public const string Forbidden = "errors.iam.api_client.forbidden";
    public const string NotFound = "errors.iam.api_client.not_found";
    public const string NameRequired = "errors.iam.api_client.name_required";
    public const string ScopesRequired = "errors.iam.api_client.scopes_required";
    public const string ScopeNotAllowed = "errors.iam.api_client.scope_not_allowed";
    public const string IpInvalid = "errors.iam.api_client.ip_invalid";
    public const string RateInvalid = "errors.iam.api_client.rate_invalid";
    public const string ExpiryInvalid = "errors.iam.api_client.expiry_invalid";
    public const string NotActive = "errors.iam.api_client.not_active";
    public const string Changed = "errors.iam.api_client.request_changed";

    public int Status { get; } = status;

    public string ErrorKey { get; } = errorKey;
}

/// <summary>The API client service (slice S1.27c Build item 10).</summary>
public interface IApiClientService
{
    /// <summary>The permissions a client may be given (the manifest flag <c>api_client</c>).</summary>
    Task<IReadOnlyList<ApiClientScopeOption>> ScopeOptionsAsync(CancellationToken cancellationToken);

    Task<ApiClientSecret> CreateAsync(ApiClientCreateRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiClientView>> ListAsync(CancellationToken cancellationToken);

    Task<ApiClientView> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ApiClientSecret> RotateAsync(Guid id, CancellationToken cancellationToken);

    Task<ApiClientView> RevokeAsync(Guid id, CancellationToken cancellationToken);

    Task<ApiClientUsage> UsageAsync(Guid id, int days, CancellationToken cancellationToken);
}

/// <summary>The answer of the rate check for one call: refused calls say how many seconds to wait and which limit was hit.</summary>
public readonly record struct ApiRateDecision(bool Allowed, int RetryAfterSeconds, bool ClientLimit);

/// <summary>An API client that has proved its key: the row's id, its scopes today (flagged <c>api_client</c> and still held by the client) and its limit.</summary>
public sealed record ApiKeyPrincipal(Guid Id, string ClientId, string Name, IReadOnlyList<string> Scopes, int RateLimitPerMinute);

/// <summary>Why a key was refused (S1.27c Build item 11). <see cref="IpNotAllowed"/> answers 403; every other refusal answers 401.</summary>
public enum ApiKeyFailure
{
    None,
    Malformed,
    Unknown,
    Revoked,
    Expired,
    IpNotAllowed,
    SecretInvalid,
    Busy,
}

public sealed record ApiKeyResult(ApiKeyPrincipal? Principal, ApiKeyFailure Failure, Guid? ClientRowId);

/// <summary>Checks <c>Authorization: ApiKey &lt;client_id&gt;.&lt;secret&gt;</c> against the bound customer's database only (D-02).</summary>
public interface IApiKeyAuthenticator
{
    Task<ApiKeyResult> AuthenticateAsync(string key, string? callerAddress, CancellationToken cancellationToken);
}

/// <summary>Rate limits and usage of the calls of API clients (S1.27c Build items 12 and 13), per host.</summary>
public interface IApiClientMeter
{
    /// <summary>Counts one call of the client against its limit and the customer's ceiling.</summary>
    Task<ApiRateDecision> AcquireAsync(Guid clientId, int clientLimit, CancellationToken cancellationToken);

    /// <summary>Adds an answered call to the client's usage (never fails the request).</summary>
    void Record(Guid clientId, int status, double elapsedMilliseconds, bool refusedByLimit);

    /// <summary>Raises the limit notice for the client at most once per day.</summary>
    Task NoticeLimitReachedAsync(Guid clientId, int limit, CancellationToken cancellationToken);

    /// <summary>Adds this host's counts to the database now (the background service does it every minute).</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Who an API-key request acts as (slice S1.27c): bound once per request by the key gate to the client's scopes. The access engine then gives the request
/// exactly those permissions, scope "all", no field rights, instead of roles read for a user (the same shape as a background job's grant).
/// </summary>
public sealed class ApiClientContext
{
    public bool IsBound { get; private set; }

    public Guid ClientId { get; private set; }

    public IReadOnlySet<string> Scopes { get; private set; } = new HashSet<string>();

    public void Bind(Guid clientId, IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (IsBound)
        {
            throw new InvalidOperationException("The API client context of this request is already bound.");
        }

        IsBound = true;
        ClientId = clientId;
        Scopes = scopes.ToHashSet(StringComparer.Ordinal);
    }
}
