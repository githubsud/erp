using Erp.Infrastructure.Pdf;
using Erp.Infrastructure.Persistence;
using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Erp.Modules.HR.Documents;

internal sealed record PdfLine(string NameEn, string NameAr, decimal? Quantity, decimal Amount);

/// <summary>Bilingual payslips and final settlement statements (English left, Arabic right, amounts in words in both).</summary>
internal sealed class HrPdfDocuments(ErpDbContext db, ITenantContext tenant, ICompanyProfileProvider companies, IClock clock)
{
    private static readonly string[] ArabicMonths =
        ["يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر"];

    private static readonly string[] EnglishMonths =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>One payslip, or every payslip of the run (one per page) when <paramref name="payslipId"/> is null.</summary>
    public async Task<(byte[] Content, string FileName)> PayslipsAsync(Guid runId, Guid? payslipId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.CompanyId == tenant.RequireCompanyId(), ct)
            ?? throw new NotFoundException("Payroll run", runId);
        var payslips = await db.Set<Payslip>().AsNoTracking().Include(p => p.Lines)
            .Where(p => p.PayrollRunId == runId && (payslipId == null || p.Id == payslipId))
            .OrderBy(p => p.EmpNo).ToListAsync(ct);
        if (payslips.Count == 0) throw new NotFoundException("Payslip", payslipId?.ToString() ?? runId.ToString());

        var employeeIds = payslips.Select(p => p.EmployeeId).ToList();
        var titles = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => (e.JobTitleEn, e.JobTitleAr), ct);
        var company = await companies.GetAsync(ct);
        var period = $"{EnglishMonths[run.Month - 1]} {run.Year}";
        var periodAr = $"{ArabicMonths[run.Month - 1]} {run.Year}";

        PdfKit.EnsureInitialised();
        var bytes = Document.Create(container =>
        {
            foreach (var p in payslips)
                container.Page(page =>
                {
                    PdfKit.ApplyPage(page);
                    page.Header().Element(c => PdfKit.Header(c, company, $"Payslip — {period}", $"قسيمة راتب — {periodAr}", run.RunNo));
                    page.Content().PaddingTop(12).Column(col =>
                    {
                        col.Spacing(12);
                        var (titleEn, titleAr) = titles.GetValueOrDefault(p.EmployeeId);
                        col.Item().Table(t =>
                        {
                            ThreeColumns(t);
                            PdfKit.Field(t, "Employee No.", p.EmpNo, "الرقم الوظيفي");
                            PdfKit.Field(t, "Name", p.NameEn, "الاسم");
                            PdfKit.Field(t, "Name (Arabic)", p.NameAr, "الاسم بالعربية");
                            PdfKit.Field(t, "Job title", JoinTitle(titleEn, titleAr), "المسمى الوظيفي");
                            PdfKit.Field(t, "Paid days", p.PaidDays.ToString("0.##"), "أيام العمل المدفوعة");
                            if (p.UnpaidDays > 0) PdfKit.Field(t, "Unpaid leave days", p.UnpaidDays.ToString("0.##"), "أيام إجازة بدون راتب");
                            if (p.OvertimeHours > 0) PdfKit.Field(t, "Overtime hours", p.OvertimeHours.ToString("0.##"), "ساعات العمل الإضافي");
                            PdfKit.Field(t, "Paid to", $"{p.BankShortName} {MaskIban(p.Iban)}".Trim(), "طريقة الصرف");
                        });

                        var earnings = p.Lines.Where(l => l.Kind == ComponentKind.Earning).OrderBy(l => l.LineNo)
                            .Select(l => new PdfLine(l.NameEn, l.NameAr, l.Quantity, l.Amount)).ToList();
                        var deductions = p.Lines.Where(l => l.Kind == ComponentKind.Deduction).OrderBy(l => l.LineNo)
                            .Select(l => new PdfLine(l.NameEn, l.NameAr, l.Quantity, l.Amount)).ToList();
                        col.Item().Element(c => LinesTable(c, "Earnings", "المستحقات", earnings));
                        if (deductions.Count > 0) col.Item().Element(c => LinesTable(c, "Deductions", "الاستقطاعات", deductions));
                        col.Item().Element(c => Totals(c, p.Gross, p.Deductions, p.Net, "Net salary", "صافي الراتب"));
                        col.Item().Element(c => Words(c, p.Net));
                    });
                    page.Footer().Element(c => PdfKit.Footer(c, clock.UtcNow));
                });
        }).GeneratePdf();

        var name = payslips.Count == 1 ? $"payslip_{run.Year}{run.Month:D2}_{payslips[0].EmpNo}.pdf" : $"payslips_{run.Year}{run.Month:D2}.pdf";
        return (bytes, name);
    }

    public async Task<(byte[] Content, string FileName)> SettlementAsync(Guid settlementId, CancellationToken ct)
    {
        var s = await db.Set<FinalSettlement>().AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == settlementId && x.CompanyId == tenant.RequireCompanyId(), ct)
            ?? throw new NotFoundException("Final settlement", settlementId);
        var e = await db.Set<Employee>().AsNoTracking().SingleAsync(x => x.Id == s.EmployeeId, ct);
        var company = await companies.GetAsync(ct);
        var (reasonEn, reasonAr) = ReasonText(s.Reason);
        var years = s.ServiceDays / 365m;

        PdfKit.EnsureInitialised();
        var bytes = Document.Create(container => container.Page(page =>
        {
            PdfKit.ApplyPage(page);
            page.Header().Element(c => PdfKit.Header(c, company, "Final Settlement Statement", "بيان المخالصة النهائية", s.SettlementNo));
            page.Content().PaddingTop(12).Column(col =>
            {
                col.Spacing(12);
                if (s.Status == SettlementStatus.Draft)
                    col.Item().Background("#FFF4E5").Padding(6).Text("DRAFT — not yet posted · مسودة — لم تُرحّل بعد").Bold().FontColor("#9A5B00");

                col.Item().Table(t =>
                {
                    ThreeColumns(t);
                    PdfKit.Field(t, "Employee No.", e.EmpNo, "الرقم الوظيفي");
                    PdfKit.Field(t, "Name", e.NameEn, "الاسم");
                    PdfKit.Field(t, "Name (Arabic)", e.NameAr, "الاسم بالعربية");
                    PdfKit.Field(t, "Joining date", s.JoiningDate.ToString("dd/MM/yyyy"), "تاريخ الالتحاق");
                    PdfKit.Field(t, "Last working day", s.LastWorkingDay.ToString("dd/MM/yyyy"), "آخر يوم عمل");
                    PdfKit.Field(t, "Service", $"{s.ServiceDays} days ({years:0.00} years)", "مدة الخدمة");
                    if (s.ExcludedDays > 0) PdfKit.Field(t, "Unpaid leave excluded", $"{s.ExcludedDays} days", "إجازة بدون راتب مستبعدة");
                    PdfKit.Field(t, "Reason", $"{reasonEn} · {reasonAr}", "سبب انتهاء الخدمة");
                    PdfKit.Field(t, "Gratuity basis", $"QAR {PdfKit.Amount(s.BasisWage)} × {s.GratuityDaysPerYear:0.##} days / year", "أساس احتساب المكافأة");
                    if (!s.EligibleForGratuity)
                        PdfKit.Field(t, "Gratuity", "Not eligible (minimum service not completed)", "غير مستحقة (لم تكتمل مدة الخدمة)");
                });

                var earnings = s.Lines.Where(l => l.Kind == ComponentKind.Earning).OrderBy(l => l.LineNo)
                    .Select(l => new PdfLine(l.NameEn, l.NameAr, l.Quantity, l.Amount)).ToList();
                var deductions = s.Lines.Where(l => l.Kind == ComponentKind.Deduction).OrderBy(l => l.LineNo)
                    .Select(l => new PdfLine(l.NameEn, l.NameAr, l.Quantity, l.Amount)).ToList();
                col.Item().Element(c => LinesTable(c, "Dues", "المستحقات", earnings));
                if (deductions.Count > 0) col.Item().Element(c => LinesTable(c, "Deductions", "الاستقطاعات", deductions));
                col.Item().Element(c => Totals(c, s.TotalEarnings, s.TotalDeductions, s.NetPayable, "Net amount payable", "صافي المبلغ المستحق"));
                col.Item().Element(c => Words(c, s.NetPayable));

                col.Item().PaddingTop(6).Row(row =>
                {
                    row.RelativeItem().Text(
                        "I acknowledge that I have received all my wages, end of service gratuity and other dues for my entire period of service, " +
                        "and I have no further claims against the company.").FontSize(8);
                    row.ConstantItem(16);
                    row.RelativeItem().ContentFromRightToLeft().Text(
                        "أقر بأنني استلمت جميع أجوري ومكافأة نهاية الخدمة وكافة مستحقاتي الأخرى عن كامل مدة خدمتي، وليس لي أي مطالبات أخرى تجاه الشركة.")
                        .FontSize(8);
                });
                col.Item().PaddingTop(28).Row(row =>
                {
                    Signature(row.RelativeItem(), "Employee signature", "توقيع الموظف");
                    row.ConstantItem(24);
                    Signature(row.RelativeItem(), "Prepared by (HR)", "إعداد (الموارد البشرية)");
                    row.ConstantItem(24);
                    Signature(row.RelativeItem(), "Approved by", "الاعتماد");
                });
            });
            page.Footer().Element(c => PdfKit.Footer(c, clock.UtcNow));
        })).GeneratePdf();

        return (bytes, $"final_settlement_{s.SettlementNo}.pdf");
    }

    private static void ThreeColumns(TableDescriptor t) => t.ColumnsDefinition(c =>
    {
        c.RelativeColumn(2);
        c.RelativeColumn(3);
        c.RelativeColumn(2);
    });

    private static void LinesTable(IContainer container, string titleEn, string titleAr, IReadOnlyList<PdfLine> lines)
    {
        container.Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn(3);
                c.RelativeColumn(1);
                c.RelativeColumn(1.4f);
                c.RelativeColumn(3);
            });
            t.Header(h =>
            {
                h.Cell().Background(PdfKit.Primary).Padding(4).Text(titleEn).Bold().FontColor(Colors.White);
                h.Cell().Background(PdfKit.Primary).Padding(4).AlignCenter().Text("Qty · العدد").FontColor(Colors.White).FontSize(8);
                h.Cell().Background(PdfKit.Primary).Padding(4).AlignRight().Text("QAR · ريال").FontColor(Colors.White).FontSize(8);
                h.Cell().Background(PdfKit.Primary).Padding(4).AlignRight().Text(titleAr).Bold().FontColor(Colors.White);
            });
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                string bg = i % 2 == 1 ? PdfKit.Shade : "#FFFFFF";
                t.Cell().Background(bg).Padding(4).Text(l.NameEn);
                t.Cell().Background(bg).Padding(4).AlignCenter().Text(l.Quantity is { } q ? q.ToString("0.##") : "");
                t.Cell().Background(bg).Padding(4).AlignRight().Text(PdfKit.Amount(l.Amount));
                t.Cell().Background(bg).Padding(4).AlignRight().Text(l.NameAr);
            }
        });
    }

    private static void Totals(IContainer container, decimal gross, decimal deductions, decimal net, string netEn, string netAr)
    {
        container.AlignRight().Width(300).Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn(2);
                c.RelativeColumn(1.4f);
                c.RelativeColumn(2);
            });
            void Row(string en, decimal value, string ar, bool strong)
            {
                string bg = strong ? PdfKit.Shade : "#FFFFFF";
                var left = t.Cell().Background(bg).Padding(4).Text(en);
                var mid = t.Cell().Background(bg).Padding(4).AlignRight().Text(PdfKit.Amount(value));
                var right = t.Cell().Background(bg).Padding(4).AlignRight().Text(ar);
                if (strong)
                {
                    left.Bold();
                    mid.Bold().FontColor(PdfKit.Primary);
                    right.Bold();
                }
            }
            Row("Total earnings", gross, "إجمالي المستحقات", false);
            Row("Total deductions", deductions, "إجمالي الاستقطاعات", false);
            Row(netEn, net, netAr, true);
        });
    }

    private static void Words(IContainer container, decimal amount) =>
        container.Background(PdfKit.Shade).Padding(6).Column(c =>
        {
            c.Item().Text(AmountInWords.English(amount)).Italic();
            c.Item().ContentFromRightToLeft().Text(AmountInWords.Arabic(amount));
        });

    private static void Signature(IContainer container, string en, string ar) =>
        container.Column(c =>
        {
            c.Item().LineHorizontal(0.8f).LineColor(PdfKit.Muted);
            c.Item().PaddingTop(2).Text($"{en} · {ar}").FontSize(8).FontColor(PdfKit.Muted);
        });

    private static string? JoinTitle(string? en, string? ar) =>
        en is null && ar is null ? null : en is null ? ar : ar is null ? en : $"{en} · {ar}";

    private static string MaskIban(string? iban) => iban is { Length: > 4 } ? $"•••• {iban[^4..]}" : "";

    private static (string En, string Ar) ReasonText(SettlementReason reason) => reason switch
    {
        SettlementReason.Resignation => ("Resignation", "استقالة"),
        SettlementReason.Termination => ("Termination by employer", "إنهاء من صاحب العمل"),
        SettlementReason.EndOfContract => ("End of contract", "انتهاء العقد"),
        SettlementReason.Retirement => ("Retirement", "تقاعد"),
        SettlementReason.Death => ("Death", "وفاة"),
        _ => (reason.ToString(), reason.ToString())
    };
}
