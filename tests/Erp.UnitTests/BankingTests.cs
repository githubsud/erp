using System.Text;
using Erp.Modules.Finance.Banking;
using Erp.SharedKernel.Domain;

namespace Erp.UnitTests;

public sealed class BankStatementParserTests
{
    private static ParsedStatement Csv(string text) => BankStatementParser.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), "statement.csv");

    [Fact]
    public void Finds_the_header_below_a_title_block_and_reads_debit_and_credit_columns()
    {
        var parsed = Csv("""
            Qatar National Bank,,,,,
            Account statement 0012-345678-001,,,,,
            Date,Description,Reference,Debit,Credit,Balance
            01/10/2026,Opening balance,,,,"100,000.00"
            02/10/2026,CHQ 000123 Doha Building,000123,"1,092.00",,"98,908.00"
            03/10/2026,TT from Al Wakra,TT-77812,,"5,000.00","103,908.00"
            ,Total,,"1,092.00","5,000.00",
            """);
        parsed.Errors.ShouldBeEmpty();
        parsed.Lines.Count.ShouldBe(2);
        parsed.Lines[0].Amount.ShouldBe(-1092m);
        parsed.Lines[0].Reference.ShouldBe("000123");
        parsed.Lines[0].Date.ShouldBe(new DateOnly(2026, 10, 2));
        parsed.Lines[1].Amount.ShouldBe(5000m);
        parsed.Lines[1].Balance.ShouldBe(103_908m);
    }

    [Fact]
    public void Reads_a_signed_amount_column_with_arabic_headers()
    {
        var parsed = Csv("التاريخ;البيان;المبلغ\n2026-10-05;رسوم بنكية;-25.00\n2026-10-06;إيداع نقدي;1500\n");
        parsed.Lines.Select(l => l.Amount).ShouldBe([-25m, 1500m]);
        parsed.Lines[0].Description.ShouldBe("رسوم بنكية");
    }

    [Theory]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("(1,234.50)", -1234.50)]
    [InlineData("1,234.50 DR", -1234.50)]
    [InlineData("1,234.50 CR", 1234.50)]
    [InlineData("QAR 99", 99)]
    [InlineData("250-", -250)]
    [InlineData("", 0)]
    public void Understands_bank_amount_formats(string text, decimal expected)
    {
        BankStatementParser.TryAmount(text, out var amount).ShouldBeTrue();
        amount.ShouldBe(expected);
    }

    [Theory]
    [InlineData("02/10/2026")]
    [InlineData("2026-10-02")]
    [InlineData("02-Oct-2026")]
    [InlineData("2 Oct 2026")]
    public void Understands_bank_date_formats(string text)
    {
        BankStatementParser.TryDate(text, out var date).ShouldBeTrue();
        date.ShouldBe(new DateOnly(2026, 10, 2));
    }

    [Fact]
    public void A_file_without_recognisable_columns_is_refused() =>
        Should.Throw<BusinessRuleException>(() => Csv("When,What,How much\n1,2,3\n")).Code.ShouldBe("bank.no_header");

    [Fact]
    public void Unreadable_rows_are_reported_not_imported()
    {
        var parsed = Csv("Date,Amount\n01/10/2026,100\nyesterday,50\n02/10/2026,abc\n");
        parsed.Lines.Count.ShouldBe(1);
        parsed.Errors.Count.ShouldBe(2);
    }
}

public sealed class BankMatcherTests
{
    private static readonly DateOnly D = new(2026, 10, 10);

    [Fact]
    public void Matches_equal_amounts_within_the_date_window()
    {
        var line = new StatementLineToMatch(Guid.NewGuid(), D, -500m, "CHQ 7781", "7781");
        var near = new BankCandidate(Guid.NewGuid(), D.AddDays(-3), -500m, "SPAY-2026-000004 Payment to supplier");
        var far = new BankCandidate(Guid.NewGuid(), D.AddDays(-20), -500m, "SPAY-2026-000001");
        BankMatcher.Propose([line], [near, far]).ShouldBe([(line.LineId, near.JournalLineId)]);
    }

    [Fact]
    public void A_reference_breaks_a_tie_and_ambiguity_is_left_alone()
    {
        var line = new StatementLineToMatch(Guid.NewGuid(), D, 1000m, "TT", "TT-90112");
        var a = new BankCandidate(Guid.NewGuid(), D, 1000m, "RCPT-2026-000005 Receipt TT-90112");
        var b = new BankCandidate(Guid.NewGuid(), D, 1000m, "RCPT-2026-000006 Receipt");
        BankMatcher.Propose([line], [a, b]).ShouldBe([(line.LineId, a.JournalLineId)]);

        var vague = new StatementLineToMatch(Guid.NewGuid(), D, 1000m, "Deposit", null);
        BankMatcher.Propose([vague], [b, new BankCandidate(Guid.NewGuid(), D, 1000m, "other")]).ShouldBeEmpty();
    }

    [Fact]
    public void A_ledger_line_is_used_once()
    {
        var first = new StatementLineToMatch(Guid.NewGuid(), D, 75m, null, null);
        var second = new StatementLineToMatch(Guid.NewGuid(), D.AddDays(1), 75m, null, null);
        var only = new BankCandidate(Guid.NewGuid(), D, 75m, "JV");
        BankMatcher.Propose([first, second], [only]).Count.ShouldBe(1);
    }
}
