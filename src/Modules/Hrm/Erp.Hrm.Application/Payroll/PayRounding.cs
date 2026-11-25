namespace Erp.Hrm.Application.Payroll;

/// <summary>
/// Rounding of the payroll engine (slice S1.35 ruling Q3): every working value is decimal and kept to four places, rounded half away from zero
/// (numeric(19,4)); a result line is rounded to the currency's minor unit by the run's mode (setting <c>hrm.pay.rounding_mode</c>, frozen in the
/// snapshot). No floating point anywhere.
/// </summary>
public static class PayRounding
{
    public const string HalfAwayFromZero = "half_away_from_zero";
    public const string HalfEven = "half_even";

    /// <summary>The working precision: four places, half away from zero.</summary>
    public static decimal Internal(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>The .NET mode of a setting value (an unknown value is refused when the setting is saved; the default is half away from zero).</summary>
    public static MidpointRounding Mode(string? setting) => setting == HalfEven ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero;

    /// <summary>An amount of a result line: to the currency's minor unit by the run's mode.</summary>
    public static decimal Line(decimal value, int decimals, MidpointRounding mode) => decimal.Round(value, Math.Clamp(decimals, 0, 4), mode);
}
