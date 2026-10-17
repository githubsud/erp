using System.Globalization;
using Erp.SharedKernel.Application;
using QuestPDF;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Erp.Infrastructure.Pdf;

/// <summary>
/// Shared building blocks for bilingual (English | Arabic) A4 documents. Uses QuestPDF under its Community
/// licence (free for organisations below USD 1M annual revenue — review the licence as the business grows)
/// and the embedded IBM Plex Sans Arabic font (SIL Open Font Licence), which covers Arabic and Latin and is
/// shaped correctly (joined letters, right-to-left).
/// </summary>
public static class PdfKit
{
    public const string FontFamily = "IBM Plex Sans Arabic";
    private static readonly Lock InitLock = new();
    private static bool _initialised;

    public static readonly string Primary = "#0F5C4D";
    public static readonly string Muted = "#5F6B6A";
    public static readonly string Border = "#D5DCDA";
    public static readonly string Shade = "#F1F5F4";

    public static void EnsureInitialised()
    {
        if (_initialised) return;
        lock (InitLock)
        {
            if (_initialised) return;
            Settings.License = LicenseType.Community;
            Settings.UseEnvironmentFonts = false;
            var assembly = typeof(PdfKit).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                FontManager.RegisterFont(stream);
            }
            _initialised = true;
        }
    }

    public static void ApplyPage(PageDescriptor page)
    {
        page.Size(PageSizes.A4);
        page.Margin(28);
        page.DefaultTextStyle(t => t.FontFamily(FontFamily).FontSize(9).FontColor("#1D2524"));
    }

    /// <summary>Company letterhead: English on the left, Arabic on the right, document title in both languages.</summary>
    public static void Header(IContainer container, CompanyProfile company, string titleEn, string titleAr, string? reference)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(company.NameEn).Bold().FontSize(13).FontColor(Primary);
                    if (company.CrNo is not null) c.Item().Text($"C.R. {company.CrNo}").FontColor(Muted);
                    if (company.AddressEn is not null) c.Item().Text(company.AddressEn).FontColor(Muted);
                });
                row.RelativeItem().ContentFromRightToLeft().Column(c =>
                {
                    c.Item().Text(company.NameAr).Bold().FontSize(13).FontColor(Primary);
                    if (company.CrNo is not null) c.Item().Text($"س.ت. {company.CrNo}").FontColor(Muted);
                    if (company.AddressAr is not null) c.Item().Text(company.AddressAr).FontColor(Muted);
                });
            });
            col.Item().PaddingVertical(8).LineHorizontal(1.2f).LineColor(Primary);
            col.Item().Row(row =>
            {
                row.RelativeItem().Text(titleEn).Bold().FontSize(12);
                row.AutoItem().Text(reference ?? "").FontColor(Muted);
                row.RelativeItem().AlignRight().Text(titleAr).Bold().FontSize(12);
            });
        });
    }

    /// <summary>A row of "English label · value · Arabic label".</summary>
    public static void Field(TableDescriptor table, string labelEn, string? value, string labelAr)
    {
        table.Cell().BorderBottom(0.5f).BorderColor(Border).PaddingVertical(3).Text(labelEn).FontColor(Muted);
        table.Cell().BorderBottom(0.5f).BorderColor(Border).PaddingVertical(3).AlignCenter().Text(value ?? "—").SemiBold();
        table.Cell().BorderBottom(0.5f).BorderColor(Border).PaddingVertical(3).AlignRight().Text(labelAr).FontColor(Muted);
    }

    public static void Footer(IContainer container, DateTimeOffset generatedAt) =>
        container.Row(row =>
        {
            row.RelativeItem().Text($"Generated {generatedAt.ToOffset(TimeSpan.FromHours(3)):yyyy-MM-dd HH:mm} (Doha)").FontSize(7).FontColor(Muted);
            row.RelativeItem().AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(7).FontColor(Muted));
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });

    public static string Amount(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
}
