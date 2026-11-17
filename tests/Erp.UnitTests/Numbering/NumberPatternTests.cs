using Erp.Kernel.Domain.Numbering;

namespace Erp.UnitTests.Numbering;

/// <summary>Slice S1.08 ruling Q4 and Q5: pattern tokens, validation, periods, rendering and the reverse match of Q9 (FR-KRN-NUM-01).</summary>
public sealed class NumberPatternTests
{
    private static readonly PatternContext Company = new(HasCompany: true, HasBranch: true, CompanyCodeLength: 3, BranchCodeLength: 3);

    private static NumberContext At(int year, int month, int day = 15, int fiscalStart = 7) => new(new DateOnly(year, month, day), fiscalStart, "ACM", "KHI");

    [Theory]
    [InlineData("EMP-{seq:5}", 42, "EMP-00042")]
    [InlineData("LTR-{yyyy}-{seq:5}", 17, "LTR-2026-00017")]
    [InlineData("{yy}{mm}/{seq:3}", 7, "2609/007")]
    [InlineData("PR-{yyyy}{mm}-{seq:2}", 3, "PR-202609-03")]
    [InlineData("INV/{fy}/{seq:4}", 12, "INV/2026-27/0012")]
    [InlineData("{company}-{branch}-{seq:1}", 9, "ACM-KHI-9")]
    [InlineData("X{seq:2}", 12345, "X12345")]
    public void FR_KRN_NUM_01_every_token_renders_with_Latin_digits(string pattern, long value, string expected) =>
        Assert.Equal(expected, NumberPattern.Parse(pattern).Render(value, At(2026, 9)));

    [Fact]
    public void FR_KRN_NUM_01_the_fiscal_year_label_follows_the_start_month()
    {
        Assert.Equal("2025-26", NumberPattern.FiscalYearLabel(new DateOnly(2026, 6, 30), 7));
        Assert.Equal("2026-27", NumberPattern.FiscalYearLabel(new DateOnly(2026, 7, 1), 7));
        Assert.Equal("2099-00", NumberPattern.FiscalYearLabel(new DateOnly(2100, 1, 1), 7));
        Assert.Equal("2026", NumberPattern.FiscalYearLabel(new DateOnly(2026, 12, 31), 1));
    }

    [Fact]
    public void FR_KRN_NUM_01_the_period_is_decided_by_the_business_date()
    {
        Assert.Equal("all", NumberPattern.ScopeKey(ResetRules.Never, new DateOnly(2026, 1, 1), 7));
        Assert.Equal("2026", NumberPattern.ScopeKey(ResetRules.Yearly, new DateOnly(2026, 12, 31), 7));
        Assert.Equal("2027", NumberPattern.ScopeKey(ResetRules.Yearly, new DateOnly(2027, 1, 1), 7));
        Assert.Equal("2026-02", NumberPattern.ScopeKey(ResetRules.Monthly, new DateOnly(2026, 2, 28), 7));
        Assert.Equal("fy2025", NumberPattern.ScopeKey(ResetRules.FiscalYearly, new DateOnly(2026, 6, 30), 7));
        Assert.Equal("fy2026", NumberPattern.ScopeKey(ResetRules.FiscalYearly, new DateOnly(2026, 7, 1), 7));
    }

    [Theory]
    [InlineData("", "never", "errors.numbering.pattern_empty")]
    [InlineData("EMP-", "never", "errors.numbering.pattern_seq_count")]
    [InlineData("{seq:3}-{seq:3}", "never", "errors.numbering.pattern_seq_count")]
    [InlineData("A{seq:0}", "never", "errors.numbering.pattern_seq_digits")]
    [InlineData("A{seq:13}", "never", "errors.numbering.pattern_seq_digits")]
    [InlineData("A{seq}", "never", "errors.numbering.pattern_token_unknown")]
    [InlineData("A{day}{seq:3}", "never", "errors.numbering.pattern_token_unknown")]
    [InlineData("A{seq:3", "never", "errors.numbering.pattern_brace")]
    [InlineData("A}{seq:3}", "never", "errors.numbering.pattern_brace")]
    [InlineData("A{{seq:3}}", "never", "errors.numbering.pattern_brace")]
    [InlineData("A\t{seq:3}", "never", "errors.numbering.pattern_control_character")]
    [InlineData("A-{seq:3}", "yearly", "errors.numbering.pattern_year_needed")]
    [InlineData("A-{fy}-{seq:3}", "yearly", "errors.numbering.pattern_year_needed")]
    [InlineData("A-{yyyy}-{seq:3}", "monthly", "errors.numbering.pattern_month_needed")]
    [InlineData("A-{mm}-{seq:3}", "monthly", "errors.numbering.pattern_month_needed")]
    [InlineData("A-{yyyy}-{seq:3}", "fiscal_yearly", "errors.numbering.pattern_fiscal_year_needed")]
    [InlineData("A-{seq:3}", "weekly", "errors.numbering.reset_rule_unknown")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ-ABCDEFGH-{seq:12}", "never", "errors.numbering.pattern_too_long")]
    public void FR_KRN_NUM_01_invalid_patterns_are_refused_with_a_clear_message(string pattern, string reset, string error)
    {
        var ex = Assert.Throws<NumberPatternException>(() => NumberPattern.Validate(pattern, reset, Company));
        Assert.Equal(error, ex.ErrorKey);
    }

    [Fact]
    public void FR_KRN_NUM_01_company_and_branch_tokens_only_where_the_document_type_has_them()
    {
        Assert.Equal("errors.numbering.pattern_company_not_allowed",
            Assert.Throws<NumberPatternException>(() => NumberPattern.Validate("{company}-{seq:3}", "never", new PatternContext(false, false))).ErrorKey);
        Assert.Equal("errors.numbering.pattern_branch_not_allowed",
            Assert.Throws<NumberPatternException>(() => NumberPattern.Validate("{branch}-{seq:3}", "never", new PatternContext(true, false))).ErrorKey);
        Assert.Equal("errors.numbering.pattern_too_long",
            Assert.Throws<NumberPatternException>(() => NumberPattern.Validate("{company}-{seq:5}", "never", new PatternContext(true, false, CompanyCodeLength: 35))).ErrorKey);
        Assert.True(NumberPattern.Validate("{company}-{branch}-{yyyy}-{seq:5}", "yearly", Company).UsesBranch);
    }

    [Fact]
    public void FR_KRN_NUM_01_a_number_over_40_characters_is_refused_when_rendered()
    {
        var pattern = NumberPattern.Parse("ABCDEFGHIJKLMNOPQRSTUVWXYZ-{seq:12}");
        Assert.Equal(39, pattern.Render(1, At(2026, 1)).Length);
        Assert.Equal("errors.numbering.number_too_long", Assert.Throws<NumberPatternException>(() => pattern.Render(99_999_999_999_999, At(2026, 1))).ErrorKey);
    }

    [Theory]
    [InlineData("EMP-{seq:5}", "never", "EMP-00042", true, 42L, "all")]
    [InlineData("EMP-{seq:5}", "never", "emp-00042", true, 42L, "all")]
    [InlineData("EMP-{seq:5}", "never", "EMP-123456", true, 123456L, "all")]
    [InlineData("EMP-{seq:5}", "never", "EMP-042", false, 0L, "")]
    [InlineData("EMP-{seq:5}", "never", "E-00042", false, 0L, "")]
    [InlineData("LV-{yyyy}-{seq:6}", "yearly", "LV-2025-000010", true, 10L, "2025")]
    [InlineData("PR-{yyyy}{mm}-{seq:2}", "monthly", "PR-202613-01", false, 0L, "")]
    [InlineData("PR-{yyyy}{mm}-{seq:2}", "monthly", "PR-202602-07", true, 7L, "2026-02")]
    [InlineData("INV/{fy}/{seq:4}", "fiscal_yearly", "INV/2024-25/0003", true, 3L, "fy2024")]
    [InlineData("INV/{fy}/{seq:4}", "fiscal_yearly", "INV/2024-26/0003", false, 0L, "")]
    public void FR_HRM_EMP_04_a_typed_number_is_matched_against_the_pattern_and_its_period(string pattern, string reset, string number, bool matches, long value, string scope)
    {
        var ok = NumberPattern.Parse(pattern).TryMatch(number, reset, 7, "ACM", null, new DateOnly(2026, 11, 17), out var v, out var s);
        Assert.Equal(matches, ok);
        Assert.Equal(value, ok ? v : 0);
        Assert.Equal(scope, ok ? s : string.Empty);
    }
}
