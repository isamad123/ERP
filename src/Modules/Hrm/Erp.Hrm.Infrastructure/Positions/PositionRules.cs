using System.Text.Json;
using Erp.Hrm.Contracts.Positions;
using Erp.Hrm.Domain.Generated;
using Erp.Kernel.Contracts.Settings;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>Pure rules of positions (slice S1.22a): the status shown on a date and the company's planning model.</summary>
public static class PositionRules
{
    public const string Planned = "planned";
    public const string Open = "open";
    public const string Filled = "filled";
    public const string Frozen = "frozen";
    public const string Closed = "closed";

    /// <summary>
    /// The status of a position row on a date (Build item 3): frozen and closed are kept; before its first day it is planned; it is
    /// filled when its holders' FTE reaches the budget (a budget of 0 is never "filled"); otherwise open.
    /// </summary>
    public static string EffectiveStatus(string stored, DateOnly validFrom, DateOnly asOf, decimal budgetedFte, decimal filledFte) =>
        stored is Frozen or Closed ? stored
        : asOf < validFrom ? Planned
        : budgetedFte > 0 && filledFte >= budgetedFte ? Filled
        : Open;

    /// <summary>The company's model from the setting value (headcount when it holds anything else).</summary>
    public static string Model(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() == OrgModels.Position ? OrgModels.Position : OrgModels.Headcount;

    public static async Task<string> ModelAsync(ISettings settings, Guid companyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Model((await settings.GetAsync(OrgModels.SettingKey, new SettingContext(companyId, null), ct).ConfigureAwait(false)).Value);
    }

    /// <summary>A position is its code: the row of the code in force on a date, from the rows of that code.</summary>
    public static Position? InForce(IEnumerable<Position> rowsOfCode, DateOnly asOf) =>
        rowsOfCode.FirstOrDefault(p => p.ValidFrom <= asOf && (p.ValidTo is null || p.ValidTo > asOf));
}
