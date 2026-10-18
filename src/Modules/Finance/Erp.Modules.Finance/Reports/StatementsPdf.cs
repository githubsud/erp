using Erp.Infrastructure.Pdf;
using Erp.SharedKernel.Application;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Erp.Modules.Finance.Reports;

/// <summary>Printable income statement and balance sheet (English and Arabic side by side, optional comparison column).</summary>
internal sealed class StatementsPdf(FinancialStatements statements, ICompanyProfileProvider companies, IClock clock)
{
    public async Task<(byte[] Content, string FileName)> ProfitAndLossAsync(DateOnly from, DateOnly to, DateOnly? compareFrom, DateOnly? compareTo,
        CancellationToken ct)
    {
        var r = await statements.ProfitAndLossAsync(from, to, compareFrom, compareTo, ct);
        var compare = r.CompareFrom is not null;
        var bytes = Render(await companies.GetAsync(ct), "Income Statement", "قائمة الدخل", $"{from:dd/MM/yyyy} – {to:dd/MM/yyyy}",
            compare ? $"{r.CompareFrom:dd/MM/yyyy} – {r.CompareTo:dd/MM/yyyy}" : null, col =>
            {
                foreach (var kind in new[] { "income", "cost_of_sales" })
                    foreach (var s in r.Sections.Where(s => s.Kind == kind)) Section(col, s, compare);
                Total(col, "Gross profit", "مجمل الربح", r.GrossProfit, r.CompareGrossProfit, compare);
                foreach (var s in r.Sections.Where(s => s.Kind == "expense")) Section(col, s, compare);
                Total(col, "Net profit / (loss)", "صافي الربح / (الخسارة)", r.NetProfit, r.CompareNetProfit, compare, strong: true);
            });
        return (bytes, $"income_statement_{from:yyyyMMdd}_{to:yyyyMMdd}.pdf");
    }

    public async Task<(byte[] Content, string FileName)> BalanceSheetAsync(DateOnly asOf, DateOnly? compareAsOf, CancellationToken ct)
    {
        var r = await statements.BalanceSheetAsync(asOf, compareAsOf, ct);
        var compare = compareAsOf is not null;
        var bytes = Render(await companies.GetAsync(ct), "Statement of Financial Position", "قائمة المركز المالي", $"{asOf:dd/MM/yyyy}",
            compare ? $"{compareAsOf:dd/MM/yyyy}" : null, col =>
            {
                foreach (var s in r.Sections.Where(s => s.Kind == "asset")) Section(col, s, compare);
                Total(col, "Total assets", "إجمالي الأصول", r.TotalAssets, r.CompareTotalAssets, compare, strong: true);
                foreach (var s in r.Sections.Where(s => s.Kind == "liability")) Section(col, s, compare);
                Total(col, "Total liabilities", "إجمالي الالتزامات", r.TotalLiabilities, r.CompareTotalLiabilities, compare);
                foreach (var s in r.Sections.Where(s => s.Kind == "equity")) Section(col, s, compare);
                Total(col, "Total liabilities and equity", "إجمالي الالتزامات وحقوق الملكية", r.TotalLiabilities + r.TotalEquity,
                    compare ? (r.CompareTotalLiabilities ?? 0) + (r.CompareTotalEquity ?? 0) : null, compare, strong: true);
            });
        return (bytes, $"balance_sheet_{asOf:yyyyMMdd}.pdf");
    }

    private byte[] Render(CompanyProfile company, string titleEn, string titleAr, string period, string? comparePeriod, Action<ColumnDescriptor> body)
    {
        PdfKit.EnsureInitialised();
        return Document.Create(container => container.Page(page =>
        {
            PdfKit.ApplyPage(page);
            page.Header().Element(c => PdfKit.Header(c, company, titleEn, titleAr, period));
            page.Content().PaddingTop(12).Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem(4);
                    row.RelativeItem(1.4f).AlignRight().Text(period).Bold().FontSize(8);
                    if (comparePeriod is not null) row.RelativeItem(1.4f).AlignRight().Text(comparePeriod).FontSize(8).FontColor(PdfKit.Muted);
                    row.RelativeItem(4);
                });
                body(col);
                col.Item().PaddingTop(8).Text("Amounts in Qatari riyals · المبالغ بالريال القطري").FontSize(7).FontColor(PdfKit.Muted);
            });
            page.Footer().Element(c => PdfKit.Footer(c, clock.UtcNow));
        })).GeneratePdf();
    }

    private static void Section(ColumnDescriptor col, StatementSection s, bool compare)
    {
        col.Item().PaddingTop(8).Background(PdfKit.Shade).Padding(4).Row(row =>
        {
            row.RelativeItem().Text(s.NameEn).Bold().FontColor(PdfKit.Primary);
            row.RelativeItem().AlignRight().Text(s.NameAr).Bold().FontColor(PdfKit.Primary);
        });
        foreach (var a in s.Accounts) Line(col, a.NameEn, a.NameAr, a.Amount, a.Compare, compare, false);
        Line(col, $"Total {s.NameEn.ToLowerInvariant()}", $"إجمالي {s.NameAr}", s.Total, s.CompareTotal, compare, true);
    }

    private static void Total(ColumnDescriptor col, string en, string ar, decimal value, decimal? compareValue, bool compare, bool strong = false) =>
        col.Item().PaddingTop(4).BorderTop(strong ? 1.2f : 0.6f).BorderColor(PdfKit.Primary).Element(c =>
            LineContent(c, en, ar, value, compareValue, compare, true, strong));

    private static void Line(ColumnDescriptor col, string en, string ar, decimal value, decimal? compareValue, bool compare, bool bold) =>
        col.Item().BorderBottom(0.4f).BorderColor(PdfKit.Border).Element(c => LineContent(c, en, ar, value, compareValue, compare, bold, false));

    private static void LineContent(IContainer c, string en, string ar, decimal value, decimal? compareValue, bool compare, bool bold, bool strong) =>
        c.PaddingVertical(3).Row(row =>
        {
            var left = row.RelativeItem(4).Text(en);
            var amount = row.RelativeItem(1.4f).AlignRight().Text(Format(value));
            if (compare) row.RelativeItem(1.4f).AlignRight().Text(Format(compareValue ?? 0)).FontColor(PdfKit.Muted);
            var right = row.RelativeItem(4).AlignRight().Text(ar);
            if (!bold) return;
            left.SemiBold();
            right.SemiBold();
            amount.SemiBold();
            if (strong) amount.FontColor(PdfKit.Primary);
        });

    /// <summary>Negative amounts in brackets, as accountants print them.</summary>
    private static string Format(decimal value) => value < 0 ? $"({PdfKit.Amount(-value)})" : PdfKit.Amount(value);
}
