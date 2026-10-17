using Erp.Modules.HR.Wps;

namespace Erp.UnitTests;

public sealed class QatarIdentifierTests
{
    [Fact]
    public void Valid_qatari_iban_passes_mod97()
    {
        var iban = TestData.QatarIban();
        iban.Length.ShouldBe(29);
        QatarIdentifiers.IsValidQatarIban(iban).ShouldBeTrue();
        QatarIdentifiers.IsValidQatarIban(iban.Insert(4, " ").ToLowerInvariant()).ShouldBeTrue();
        QatarIdentifiers.BankCodeFromIban(iban).ShouldBe("QNBA");
    }

    [Theory]
    [InlineData("QA00QNBA000000000000693123456")] // wrong check digits
    [InlineData("QA58QNBA00000000000069312345")]   // too short
    [InlineData("AE070331234567890123456")]        // not Qatari
    [InlineData("")]
    public void Invalid_ibans_are_rejected(string iban) => QatarIdentifiers.IsValidQatarIban(iban).ShouldBeFalse();

    [Theory]
    [InlineData("28435612345", true)]
    [InlineData("2843561234", false)]
    [InlineData("2843561234A", false)]
    public void Qid_is_eleven_digits(string qid, bool valid) => QatarIdentifiers.IsValidQid(qid).ShouldBe(valid);
}

public sealed class SifGeneratorTests
{
    private static readonly SifHeader Header = new("12345", "12345", null, "QNBA", TestData.QatarIban(), 2026, 10,
        new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(3)));

    private static SifRecord Record(string name, decimal basic, decimal housing, decimal deductions = 0) =>
        new("28435612345", null, name, "CBQA", TestData.QatarIban("CBQA", "000000000001234567890"), 30, basic + housing - deductions,
            basic, 0, 0, deductions, housing, 0, 0, 0, null);

    [Fact]
    public void Version1_file_has_header_records_and_totals()
    {
        var records = new[] { Record("Ramesh Kumar", 3000, 1000), Record("Maria, Santos", 2500, 800, 100) };
        SifGenerator.Validate(Header, records).ShouldBeEmpty();

        var file = SifGenerator.Generate(Header, records, "1");
        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(5);
        lines[1].ShouldBe($"12345,20261102,0930,12345,,QNBA,{TestData.QatarIban()},202610,7200.00,2,1");
        lines[2].Split(',').Length.ShouldBe(22);
        lines[3].ShouldStartWith("1,28435612345,,RAMESH KUMAR,CBQA,");
        lines[4].ShouldContain("MARIA SANTOS"); // delimiters removed from names
        lines[4].Split(',').Length.ShouldBe(22);
        file.TotalSalaries.ShouldBe(7200m);
        file.RecordCount.ShouldBe(2);
        file.FileName.ShouldBe("SIF_12345_QNBA_20261102_0930.csv");
        file.Sha256.Length.ShouldBe(64);
    }

    [Fact]
    public void Version0_folds_allowances_into_extra_income()
    {
        var file = SifGenerator.Generate(Header, [Record("Ali", 3000, 1000)], "0");
        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Split(',').Length.ShouldBe(10);
        var fields = lines[3].Split(',');
        fields.Length.ShouldBe(15);
        fields[8].ShouldBe("4000.00"); // net
        fields[9].ShouldBe("3000.00"); // basic
        fields[11].ShouldBe("1000.00"); // extra income includes housing
    }

    [Fact]
    public void Validation_reports_each_problem()
    {
        var bad = new SifRecord(null, null, "No Id", "CB", "QA00XXXX", 30, 1000, 900, 0, 0, 0, 0, 0, 0, 0, null);
        var errors = SifGenerator.Validate(Header with { EmployerEid = "" }, [bad]);
        errors.ShouldContain(e => e.Field == "Employer EID");
        errors.ShouldContain(e => e.Field == "Employee QID");
        errors.ShouldContain(e => e.Field == "Employee Bank Short Name");
        errors.ShouldContain(e => e.Field == "Employee Account");
        errors.ShouldContain(e => e.Field == "Net Salary"); // 900 ≠ 1000
    }
}
