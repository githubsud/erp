using System.Numerics;

namespace Erp.UnitTests;

public static class TestData
{
    /// <summary>Builds a valid Qatari IBAN (ISO 13616 check digits) for a 4-letter bank code and 21-character account.</summary>
    public static string QatarIban(string bankCode = "QNBA", string account = "000000000000693123456")
    {
        var bban = bankCode + account;
        var numeric = string.Concat((bban + "QA00").Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));
        var check = 98 - (int)(BigInteger.Parse(numeric) % 97);
        return $"QA{check:D2}{bban}";
    }
}
