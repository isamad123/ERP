namespace Erp.Kernel.Contracts.Access;

/// <summary>A signed-in user's place in the workforce on one date: their employments, persons and current org units.</summary>
public sealed record WorkforcePosition(IReadOnlyList<Guid> EmploymentIds, IReadOnlyList<Guid> PersonIds, IReadOnlyList<Guid> OrgUnitIds)
{
    public static WorkforcePosition None { get; } = new([], [], []);
}

/// <summary>
/// The questions the kernel's data scopes need answered about employees (slice S1.06 Q3, CR-018). The kernel may not
/// read HR tables (D-10), so the HR module implements this from its employments and effective-dated assignments; the
/// kernel only ever sees ids. Without the HR module every answer is empty, so team and subtree scopes match nothing.
/// Every question is "as of" a business date taken from the injected clock.
/// </summary>
public interface IWorkforceDirectory
{
    /// <summary>The user's employments (linked by user), their persons, and the org units of their assignments valid on the date.</summary>
    Task<WorkforcePosition> PositionOfUserAsync(Guid userId, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>Employments whose assignment valid on the date names one of the given employments as manager (direct team).</summary>
    Task<IReadOnlyList<Guid>> DirectReportsAsync(IReadOnlyCollection<Guid> managerEmploymentIds, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>Employments whose assignment valid on the date is in one of the org units.</summary>
    Task<IReadOnlyList<Guid>> InOrgUnitsAsync(IReadOnlyCollection<Guid> orgUnitIds, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>Employments whose assignment valid on the date is in one of the branches.</summary>
    Task<IReadOnlyList<Guid>> InBranchesAsync(IReadOnlyCollection<Guid> branchIds, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>Employments of the given companies.</summary>
    Task<IReadOnlyList<Guid>> InCompaniesAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken);

    /// <summary>The persons of the given employments.</summary>
    Task<IReadOnlyList<Guid>> PersonsOfAsync(IReadOnlyCollection<Guid> employmentIds, CancellationToken cancellationToken);
}
