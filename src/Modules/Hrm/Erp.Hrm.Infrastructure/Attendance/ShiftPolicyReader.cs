using System.Text.Json;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Policies;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// What the <c>shift</c> policy says about one employment over a period (slice S1.28a Build items 2 and 3): the weekly-off pattern, the
/// minimum rest between shifts when the customer set one, and the grace minutes (shown only; applied by S1.29). "No policy" is a real
/// answer: no weekly-off days are generated and no rest is checked (never a default, ruling Q3, AS-4). <see cref="Error"/> is a
/// localization key when the policy's pattern is incomplete.
/// </summary>
internal sealed record EmploymentShiftPolicy(WeeklyOffPattern? Pattern, int? MinimumRestMinutes, int? GraceMinutes, string? Error, string? PolicyCode, string? NoPolicyReason)
{
    public static EmploymentShiftPolicy None(string? reason) => new(null, null, null, null, null, reason);

    public bool IsOff(DateOnly date) => Pattern?.IsOff(date) ?? false;
}

/// <summary>Reads the <c>shift</c> policy of an employment for a period through <see cref="IPolicyResolver"/> (S1.23a).</summary>
internal sealed class ShiftPolicyReader(IPolicyResolver resolver)
{
    public const string PolicyType = "shift";

    /// <summary>
    /// The policy as one answer for the period, or one answer per day when the policy in force changes inside it (a new version or a
    /// different policy); <c>For</c> picks the answer of a date.
    /// </summary>
    public async Task<PeriodPolicy?> ReadAsync(Guid employmentId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var first = await resolver.ResolveAsync(employmentId, PolicyType, from, cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return null;
        }

        var last = to == from ? first : await resolver.ResolveAsync(employmentId, PolicyType, to, cancellationToken).ConfigureAwait(false);
        if (last is not null && Same(first, last))
        {
            return new PeriodPolicy(Convert(first), null);
        }

        var perDay = new Dictionary<DateOnly, EmploymentShiftPolicy>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var answer = await resolver.ResolveAsync(employmentId, PolicyType, d, cancellationToken).ConfigureAwait(false);
            perDay[d] = answer is null ? EmploymentShiftPolicy.None("no_employment") : Convert(answer);
        }

        return new PeriodPolicy(Convert(first), perDay);
    }

    private static bool Same(PolicyResolution a, PolicyResolution b) => a.Outcome == b.Outcome && a.Policy?.VersionId == b.Policy?.VersionId;

    private static EmploymentShiftPolicy Convert(PolicyResolution resolution)
    {
        if (resolution.Policy is not { } match)
        {
            return EmploymentShiftPolicy.None(resolution.Reason);
        }

        var p = match.Parameters;
        WeeklyOffPattern? pattern = null;
        string? error = null;
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("weeklyOffKind", out _))
        {
            (pattern, error) = WeeklyOffPattern.Parse(p);
        }

        return new EmploymentShiftPolicy(pattern, Int(p, "minRestMinutes"), Int(p, "graceMinutes"), error, match.PolicyCode, null);
    }

    private static int? Int(JsonElement p, string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;
}

/// <summary>The shift policy over a period: one answer for all of it, or one per date.</summary>
internal sealed record PeriodPolicy(EmploymentShiftPolicy Whole, IReadOnlyDictionary<DateOnly, EmploymentShiftPolicy>? PerDay)
{
    public EmploymentShiftPolicy For(DateOnly date) => PerDay is not null && PerDay.TryGetValue(date, out var p) ? p : Whole;
}
