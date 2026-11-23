using Erp.Hrm.Application.Employments;

namespace Erp.UnitTests.People;

/// <summary>The pure rules of employee payment accounts (slice S1.20d): IBAN validation and the split of net pay (FR-HRM-EMP-06).</summary>
public sealed class PayDetailsUnitTests
{
    private static readonly DateOnly Jan = new(2026, 1, 1);

    [Theory]
    [InlineData("PK36SCBL0000001123456702")] // Pakistan, 24
    [InlineData("GB29NWBK60161331926819")] // United Kingdom, 22
    [InlineData("SA0380000000608010167519")] // Saudi Arabia, 24
    [InlineData("AE070331234567890123456")] // United Arab Emirates, 23
    public void FR_HRM_EMP_06_valid_IBANs_pass(string iban) => Assert.Null(Iban.Check(iban));

    [Theory]
    [InlineData("PK37SCBL0000001123456702")] // check digits
    [InlineData("PK36SCBL000000112345670")] // Pakistan needs 24 characters
    [InlineData("PK36SCBL00000011234567021")] // and not 25
    [InlineData("P136SCBL0000001123456702")] // country code letters
    [InlineData("PKX6SCBL0000001123456702")] // check digits are digits
    [InlineData("PK36SCBL-000001123456702")] // letters and digits only
    [InlineData("PK36")] // too short
    public void FR_HRM_EMP_06_invalid_IBANs_are_refused(string iban) => Assert.Equal(Iban.Invalid, Iban.Check(iban));

    [Fact]
    public void FR_HRM_EMP_06_an_IBAN_is_normalized_without_spaces_in_upper_case()
    {
        Assert.Equal("PK36SCBL0000001123456702", Iban.Normalize(" pk36 scbl 0000 0011 2345 6702 "));
        Assert.Null(Iban.Check(Iban.Normalize("gb29 nwbk 6016 1331 9268 19")));
    }

    [Fact]
    public void FR_HRM_EMP_06_FR_HRM_PAY_24_one_remainder_and_at_most_100_percent_on_every_date()
    {
        SplitRow Row(string kind, decimal? value, int priority, DateOnly from, DateOnly? to = null) => new(Guid.NewGuid(), from, to, kind, value, priority);

        Assert.Null(PaySplit.Check([]));
        Assert.Null(PaySplit.Check([Row("remainder", null, 1, Jan)]));
        Assert.Null(PaySplit.Check([Row("remainder", null, 1, Jan), Row("percent", 60, 2, Jan), Row("percent", 40, 3, Jan), Row("amount", 5000, 4, Jan)]));
        Assert.Equal(PaySplit.NoRemainder, PaySplit.Check([Row("percent", 50, 1, Jan)])!.ErrorKey);
        Assert.Equal(PaySplit.TwoRemainders, PaySplit.Check([Row("remainder", null, 1, Jan), Row("remainder", null, 2, Jan)])!.ErrorKey);
        Assert.Equal(PaySplit.PercentOver, PaySplit.Check([Row("remainder", null, 1, Jan), Row("percent", 60, 2, Jan), Row("percent", 40.0001m, 3, Jan)])!.ErrorKey);
        Assert.Equal(PaySplit.PriorityTaken, PaySplit.Check([Row("remainder", null, 1, Jan), Row("amount", 10, 1, Jan)])!.ErrorKey);

        // A remainder that ends while a percent account goes on fails on the day it ends; a hand-over on the same day passes.
        var gap = PaySplit.Check([Row("remainder", null, 1, Jan, Jan.AddDays(30)), Row("percent", 20, 2, Jan)]);
        Assert.Equal(new SplitProblem(PaySplit.NoRemainder, Jan.AddDays(30)), gap);
        Assert.Null(PaySplit.Check([Row("remainder", null, 1, Jan, Jan.AddDays(30)), Row("percent", 20, 2, Jan), Row("remainder", null, 3, Jan.AddDays(30))]));

        // Periods that do not meet are judged on their own dates (priorities may repeat across them).
        Assert.Null(PaySplit.Check([Row("remainder", null, 1, Jan, Jan.AddDays(10)), Row("remainder", null, 1, Jan.AddDays(10))]));
    }

    [Fact]
    public void FR_HRM_EMP_06_a_split_is_described_with_invariant_digits()
    {
        Assert.Equal("remainder", PaySplit.Describe("remainder", null));
        Assert.Equal("percent 12.5", PaySplit.Describe("percent", 12.5000m));
        Assert.Equal("amount 25000", PaySplit.Describe("amount", 25000m));
    }
}
