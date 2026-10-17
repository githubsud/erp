using System.Text;

namespace Erp.SharedKernel.Domain;

/// <summary>
/// Amount in words for printed documents (payslips, settlements, cheques, invoices), in English and Arabic,
/// for Qatari riyals (1 riyal = 100 dirhams).
/// </summary>
public static class AmountInWords
{
    private static readonly string[] EnOnes =
    [
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen",
        "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"
    ];

    private static readonly string[] EnTens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    public static string English(decimal amount)
    {
        var (riyals, dirhams) = Split(amount);
        var sb = new StringBuilder();
        sb.Append(riyals == 0 ? "Zero" : EnglishNumber(riyals)).Append(riyals == 1 ? " Qatari Riyal" : " Qatari Riyals");
        if (dirhams > 0)
            sb.Append(" and ").Append(EnglishNumber(dirhams)).Append(dirhams == 1 ? " Dirham" : " Dirhams");
        return sb.Append(" Only").ToString();
    }

    public static string EnglishNumber(long n)
    {
        if (n == 0) return "Zero";
        var parts = new List<string>();
        foreach (var (value, name) in new[] { (1_000_000_000L, "Billion"), (1_000_000L, "Million"), (1_000L, "Thousand") })
        {
            if (n >= value)
            {
                parts.Add($"{EnglishBelowThousand((int)(n / value))} {name}");
                n %= value;
            }
        }
        if (n > 0) parts.Add(EnglishBelowThousand((int)n));
        return string.Join(" ", parts);
    }

    private static string EnglishBelowThousand(int n)
    {
        var parts = new List<string>();
        if (n >= 100)
        {
            parts.Add($"{EnOnes[n / 100]} Hundred");
            n %= 100;
        }
        if (n >= 20)
        {
            parts.Add(n % 10 == 0 ? EnTens[n / 10] : $"{EnTens[n / 10]}-{EnOnes[n % 10]}");
        }
        else if (n > 0)
        {
            parts.Add(EnOnes[n]);
        }
        return string.Join(" ", parts);
    }

    private static readonly string[] ArOnes =
        ["", "واحد", "اثنان", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية", "تسعة", "عشرة",
         "أحد عشر", "اثنا عشر", "ثلاثة عشر", "أربعة عشر", "خمسة عشر", "ستة عشر", "سبعة عشر", "ثمانية عشر", "تسعة عشر"];

    private static readonly string[] ArTens = ["", "", "عشرون", "ثلاثون", "أربعون", "خمسون", "ستون", "سبعون", "ثمانون", "تسعون"];

    private static readonly string[] ArHundreds =
        ["", "مائة", "مائتان", "ثلاثمائة", "أربعمائة", "خمسمائة", "ستمائة", "سبعمائة", "ثمانمائة", "تسعمائة"];

    /// <summary>e.g. 3,500.50 → "فقط ثلاثة آلاف وخمسمائة ريال قطري وخمسون درهماً لا غير".</summary>
    public static string Arabic(decimal amount)
    {
        var (riyals, dirhams) = Split(amount);
        var sb = new StringBuilder("فقط ");
        sb.Append(riyals == 0 ? "صفر" : ArabicNumber(riyals)).Append(" ريال قطري");
        if (dirhams > 0)
            sb.Append(" و").Append(ArabicNumber(dirhams)).Append(dirhams is >= 3 and <= 10 ? " دراهم" : " درهماً");
        return sb.Append(" لا غير").ToString();
    }

    public static string ArabicNumber(long n)
    {
        if (n == 0) return "صفر";
        var groups = new List<string>();
        foreach (var (value, one, two, plural) in new[]
                 {
                     (1_000_000_000L, "مليار", "ملياران", "مليارات"),
                     (1_000_000L, "مليون", "مليونان", "ملايين"),
                     (1_000L, "ألف", "ألفان", "آلاف")
                 })
        {
            if (n < value) continue;
            var count = (int)(n / value);
            n %= value;
            groups.Add(count switch
            {
                1 => one,
                2 => two,
                >= 3 and <= 10 => $"{ArabicBelowThousand(count)} {plural}",
                _ => $"{ArabicBelowThousand(count)} {one}"
            });
        }
        if (n > 0) groups.Add(ArabicBelowThousand((int)n));
        return string.Join(" و", groups);
    }

    private static string ArabicBelowThousand(int n)
    {
        var parts = new List<string>();
        if (n >= 100)
        {
            parts.Add(ArHundreds[n / 100]);
            n %= 100;
        }
        if (n is > 0 and < 20) parts.Add(ArOnes[n]);
        else if (n >= 20) parts.Add(n % 10 == 0 ? ArTens[n / 10] : $"{ArOnes[n % 10]} و{ArTens[n / 10]}");
        return string.Join(" و", parts);
    }

    private static (long Riyals, int Dirhams) Split(decimal amount)
    {
        amount = Math.Abs(Money.Round(amount));
        var riyals = (long)Math.Truncate(amount);
        return (riyals, (int)((amount - riyals) * 100));
    }
}
