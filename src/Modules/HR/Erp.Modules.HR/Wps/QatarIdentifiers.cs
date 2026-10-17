using System.Numerics;
using System.Text.RegularExpressions;

namespace Erp.Modules.HR.Wps;

public static partial class QatarIdentifiers
{
    [GeneratedRegex(@"^\d{11}$")]
    private static partial Regex QidPattern();

    [GeneratedRegex(@"^QA\d{2}[A-Z]{4}[A-Z0-9]{21}$")]
    private static partial Regex QatarIbanPattern();

    [GeneratedRegex(@"^[A-Z]{4}$")]
    private static partial Regex BankShortNamePattern();

    public static string Normalize(string value) => value.Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();

    /// <summary>Qatar ID (QID) is 11 digits.</summary>
    public static bool IsValidQid(string? qid) => qid is not null && QidPattern().IsMatch(Normalize(qid));

    /// <summary>Qatari IBAN: QA + 2 check digits + 4-letter bank code + 21 alphanumerics (29 chars), ISO 13616 mod-97 = 1.</summary>
    public static bool IsValidQatarIban(string? iban)
    {
        if (iban is null) return false;
        var value = Normalize(iban);
        return QatarIbanPattern().IsMatch(value) && Mod97(value) == 1;
    }

    /// <summary>The 4-letter bank code embedded in a Qatari IBAN (e.g. QNBA, CBQA, QIIB).</summary>
    public static string? BankCodeFromIban(string? iban) =>
        IsValidQatarIban(iban) ? Normalize(iban!)[4..8] : null;

    public static bool IsValidBankShortName(string? code) => code is not null && BankShortNamePattern().IsMatch(code.Trim().ToUpperInvariant());

    private static int Mod97(string iban)
    {
        var rearranged = iban[4..] + iban[..4];
        var digits = string.Concat(rearranged.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));
        return (int)(BigInteger.Parse(digits) % 97);
    }
}
