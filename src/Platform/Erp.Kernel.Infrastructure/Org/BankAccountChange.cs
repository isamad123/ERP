namespace Erp.Kernel.Infrastructure.Org;

/// <summary>
/// One change of a company bank account as data (slice S1.14a, FR-KRN-IAM-14): applied at once, or stored as the payload of
/// an <c>iam.maker_checker_requests</c> row and applied by the second administrator in the approval's transaction. The
/// account number and IBAN are carried as ciphertext (field encryptor, same purpose as the columns), never as text.
/// </summary>
internal sealed record BankAccountChange
{
    public required Guid Id { get; init; }

    public required Guid CompanyId { get; init; }

    /// <summary><c>create</c>, <c>update</c> or <c>delete</c>.</summary>
    public required string Operation { get; init; }

    public string? BankCode { get; init; }

    public string? AccountTitle { get; init; }

    public string? CurrencyCode { get; init; }

    public string? SalaryFileFormat { get; init; }

    /// <summary>Null on an update that keeps the stored account number.</summary>
    public byte[]? AccountNumber { get; init; }

    /// <summary>The last four characters of the new account number, for the approver's description (the rest is never shown).</summary>
    public string? AccountNumberLast4 { get; init; }

    public byte[]? Iban { get; init; }

    public byte[]? IbanHmac { get; init; }

    public bool ClearIban { get; init; }

    /// <summary>The row version the requester read; an edit made meanwhile makes the request void.</summary>
    public long? RowVersion { get; init; }
}
