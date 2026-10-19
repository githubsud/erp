using Erp.Infrastructure.Pdf;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Erp.Modules.Sales.Documents;

internal sealed record PrintLine(string? Sku, string NameEn, string NameAr, string? Note, string? Uom, decimal Quantity, decimal? UnitPrice,
    decimal? DiscountPercent, decimal? Amount);

internal sealed record PrintField(string LabelEn, string? Value, string LabelAr);

/// <summary>Bilingual quotations, delivery notes, invoices, credit notes and customer statements (English left, Arabic right).</summary>
internal sealed class SalesPdfDocuments(ErpDbContext db, ITenantContext tenant, ICompanyProfileProvider companies, IInventoryQueries inventory,
    IFinanceQueries finance, IClock clock)
{
    public async Task<(byte[] Content, string FileName)> QuotationAsync(Guid id, CancellationToken ct)
    {
        var q = await db.Set<Quotation>().AsNoTracking().Include(x => x.Lines)
                    .SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == tenant.RequireCompanyId(), ct)
                ?? throw new NotFoundException("Quotation", id);
        var customer = await CustomerAsync(q.CustomerId, ct);
        var lines = await PricedLinesAsync(q.Lines.OrderBy(l => l.LineNo).ToList(), ct);
        var bytes = Render("Quotation", "عرض سعر", q.QuoteNo, await companies.GetAsync(ct), CustomerFields(customer,
            [
                new("Date", q.QuoteDate.ToString("dd/MM/yyyy"), "التاريخ"),
                new("Valid until", q.ValidUntil.ToString("dd/MM/yyyy"), "صالح حتى")
            ]), lines, true, Totals(lines, q.Total), q.Total, q.Notes,
            [("Prepared by", "إعداد"), ("Customer acceptance", "موافقة العميل")], q.Status == QuotationStatus.Draft);
        return (bytes, $"quotation_{q.QuoteNo}.pdf");
    }

    public async Task<(byte[] Content, string FileName)> DeliveryNoteAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Set<DeliveryNote>().AsNoTracking().Include(x => x.Lines)
                    .SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == tenant.RequireCompanyId(), ct)
                ?? throw new NotFoundException("Delivery note", id);
        var customer = await CustomerAsync(d.CustomerId, ct);
        var order = await db.Set<SalesOrder>().AsNoTracking().SingleAsync(o => o.Id == d.SalesOrderId, ct);
        var orderLines = await db.Set<SalesOrderLine>().AsNoTracking().Where(l => l.SalesOrderId == order.Id).ToDictionaryAsync(l => l.Id, ct);
        var items = await inventory.GetItemsAsync(d.Lines.Select(l => l.ItemId), ct);
        var lines = d.Lines.OrderBy(l => l.LineNo).Select(l => new PrintLine(items[l.ItemId].Sku, items[l.ItemId].NameEn, items[l.ItemId].NameAr,
            orderLines[l.SalesOrderLineId].Description, items[l.ItemId].UomCode, l.Quantity, null, null, null)).ToList();
        var bytes = Render("Delivery Note", "إشعار تسليم", d.DnNo, await companies.GetAsync(ct), CustomerFields(customer,
            [
                new("Date", d.DeliveryDate.ToString("dd/MM/yyyy"), "التاريخ"),
                new("Sales order", order.SoNo, "أمر البيع"),
                new("Customer PO", order.CustomerRef, "أمر شراء العميل"),
                new("Deliver to", d.DeliveryAddress, "عنوان التسليم"),
                new("Vehicle", d.VehicleNo, "المركبة")
            ]), lines, false, [], null, d.Notes,
            [("Delivered by", "سُلّمت بواسطة"), ("Received in good condition by", "استلمت بحالة جيدة بواسطة")], false,
            d.Status == DocumentStatus.Reversed);
        return (bytes, $"delivery_note_{d.DnNo}.pdf");
    }

    public async Task<(byte[] Content, string FileName)> InvoiceAsync(Guid id, CancellationToken ct)
    {
        var i = await db.Set<SalesInvoice>().AsNoTracking().Include(x => x.Lines)
                    .SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == tenant.RequireCompanyId(), ct)
                ?? throw new NotFoundException("Sales invoice", id);
        var customer = await CustomerAsync(i.CustomerId, ct);
        var items = await inventory.GetItemsAsync(i.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value), ct);
        var lines = i.Lines.OrderBy(l => l.LineNo).Select(l => l.ItemId is { } item
            ? new PrintLine(items[item].Sku, items[item].NameEn, items[item].NameAr, l.Description, items[item].UomCode, l.Quantity, l.UnitPrice,
                l.DiscountPercent, l.Amount)
            : new PrintLine(null, l.Description ?? "", l.Description ?? "", null, null, l.Quantity, l.UnitPrice, l.DiscountPercent, l.Amount)).ToList();
        var orderNo = i.SalesOrderId is { } so ? await db.Set<SalesOrder>().Where(o => o.Id == so).Select(o => o.SoNo).SingleAsync(ct) : null;
        List<(string, decimal, string, bool)> totals = [];
        if (i.DiscountTotal > 0)
        {
            totals.Add(("Gross amount", i.Total + i.DiscountTotal, "الإجمالي قبل الخصم", false));
            totals.Add(("Discount", -i.DiscountTotal, "الخصم", false));
        }
        totals.Add(($"Total ({i.Currency})", i.Total, $"الإجمالي ({CurrencyAr(i.Currency)})", true));
        if (i.PaidAmount + i.CreditedAmount > 0)
        {
            totals.Add(("Paid / credited", -(i.PaidAmount + i.CreditedAmount), "المدفوع / المخصوم", false));
            totals.Add(("Balance due", i.OpenAmount, "الرصيد المستحق", true));
        }
        var bytes = Render("Invoice", "فاتورة", i.InvoiceNo, await companies.GetAsync(ct), CustomerFields(customer,
            [
                new("Invoice date", i.InvoiceDate.ToString("dd/MM/yyyy"), "تاريخ الفاتورة"),
                new("Due date", i.DueDate.ToString("dd/MM/yyyy"), "تاريخ الاستحقاق"),
                new("Sales order", orderNo, "أمر البيع"),
                new("Customer PO", i.CustomerRef, "أمر شراء العميل"),
                new("Document ID", i.EInvoiceUuid?.ToString(), "معرّف المستند")
            ]), lines, true, totals, i.Total, i.Notes, [("Issued by", "أصدرها"), ("Received by", "استلمها")], i.Status == InvoiceStatus.Draft,
            i.Status == InvoiceStatus.Reversed);
        return (bytes, $"invoice_{i.InvoiceNo}.pdf");
    }

    public async Task<(byte[] Content, string FileName)> CreditNoteAsync(Guid id, CancellationToken ct)
    {
        var n = await db.Set<CreditNote>().AsNoTracking().Include(x => x.Lines)
                    .SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == tenant.RequireCompanyId(), ct)
                ?? throw new NotFoundException("Credit note", id);
        var customer = await CustomerAsync(n.CustomerId, ct);
        var invoiceNo = await db.Set<SalesInvoice>().Where(x => x.Id == n.SalesInvoiceId).Select(x => x.InvoiceNo).SingleAsync(ct);
        var items = await inventory.GetItemsAsync(n.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value), ct);
        var lines = n.Lines.OrderBy(l => l.LineNo).Select(l =>
        {
            var (en, ar) = l.LineType == CreditNoteLineType.Return ? ("Return", "مرتجع") : ("Allowance", "خصم");
            return l.ItemId is { } item
                ? new PrintLine(items[item].Sku, $"{en}: {items[item].NameEn}", $"{ar}: {items[item].NameAr}", l.Description, items[item].UomCode, l.Quantity,
                    l.UnitPrice, null, l.Amount)
                : new PrintLine(null, $"{en}: {l.Description}", $"{ar}: {l.Description}", null, null, l.Quantity, l.UnitPrice, null, l.Amount);
        }).ToList();
        var bytes = Render("Credit Note", "إشعار دائن", n.CreditNoteNo, await companies.GetAsync(ct), CustomerFields(customer,
            [
                new("Date", n.NoteDate.ToString("dd/MM/yyyy"), "التاريخ"),
                new("Original invoice", invoiceNo, "الفاتورة الأصلية"),
                new("Reason", n.Reason, "السبب")
            ]), lines, true, [($"Total credited ({n.Currency})", n.Total, $"إجمالي المبلغ الدائن ({CurrencyAr(n.Currency)})", true)], n.Total, null,
            [("Issued by", "أصدرها"), ("Approved by", "الاعتماد")], false, n.Status == DocumentStatus.Reversed);
        return (bytes, $"credit_note_{n.CreditNoteNo}.pdf");
    }

    public async Task<(byte[] Content, string FileName)> StatementAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var customer = await CustomerAsync(customerId, ct);
        var ledger = await finance.GetPartyLedgerAsync(AccountRoles.AccountsReceivable, PartyType.Customer, customerId, from, to, ct);
        var company = await companies.GetAsync(ct);
        PdfKit.EnsureInitialised();
        var bytes = Document.Create(container => container.Page(page =>
        {
            PdfKit.ApplyPage(page);
            page.Header().Element(c => PdfKit.Header(c, company, "Statement of Account", "كشف حساب", customer.Code));
            page.Content().PaddingTop(12).Column(col =>
            {
                col.Spacing(10);
                col.Item().Element(c => FieldsTable(c, CustomerFields(customer,
                    [new("Period", $"{from:dd/MM/yyyy} – {to:dd/MM/yyyy}", "الفترة"), new("Credit limit", customer.CreditLimit is { } l ? PdfKit.Amount(l) : null, "حد الائتمان")])));
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(58);
                        c.ConstantColumn(78);
                        c.RelativeColumn(3);
                        c.RelativeColumn(1.2f);
                        c.RelativeColumn(1.2f);
                        c.RelativeColumn(1.3f);
                    });
                    t.Header(h =>
                    {
                        foreach (var (text, right) in new[] { ("Date · التاريخ", false), ("Document · المستند", false), ("Details · البيان", false),
                                     ("Debit · مدين", true), ("Credit · دائن", true), ("Balance · الرصيد", true) })
                        {
                            var cell = h.Cell().Background(PdfKit.Primary).Padding(4);
                            (right ? cell.AlignRight() : cell).Text(text).FontColor(Colors.White).FontSize(8).Bold();
                        }
                    });
                    var balance = ledger.OpeningBalance;
                    Row(t, 0, from.ToString("dd/MM/yyyy"), "", "Opening balance · الرصيد الافتتاحي", null, null, balance);
                    var i = 1;
                    foreach (var l in ledger.Lines)
                    {
                        balance += l.Debit - l.Credit;
                        Row(t, i++, l.PostingDate.ToString("dd/MM/yyyy"), l.SourceNo, $"{l.MemoEn}\n{l.MemoAr}", l.Debit == 0 ? null : l.Debit,
                            l.Credit == 0 ? null : l.Credit, balance);
                    }
                });
                col.Item().AlignRight().Width(260).Background(PdfKit.Shade).Padding(6).Row(r =>
                {
                    r.RelativeItem().Text("Closing balance · الرصيد الختامي").Bold();
                    r.AutoItem().Text(PdfKit.Amount(ledger.ClosingBalance)).Bold().FontColor(PdfKit.Primary);
                });
                col.Item().Text("Please report any difference within 15 days. · يرجى إبلاغنا بأي اختلاف خلال 15 يوماً.").FontSize(8).FontColor(PdfKit.Muted);
            });
            page.Footer().Element(c => PdfKit.Footer(c, clock.UtcNow));
        })).GeneratePdf();
        return (bytes, $"statement_{customer.Code}_{to:yyyyMMdd}.pdf");
    }

    private static void Row(TableDescriptor t, int index, string date, string doc, string details, decimal? debit, decimal? credit, decimal balance)
    {
        var bg = index % 2 == 1 ? PdfKit.Shade : "#FFFFFF";
        t.Cell().Background(bg).Padding(4).Text(date);
        t.Cell().Background(bg).Padding(4).Text(doc);
        t.Cell().Background(bg).Padding(4).Text(details).FontSize(8);
        t.Cell().Background(bg).Padding(4).AlignRight().Text(debit is { } d ? PdfKit.Amount(d) : "");
        t.Cell().Background(bg).Padding(4).AlignRight().Text(credit is { } c ? PdfKit.Amount(c) : "");
        t.Cell().Background(bg).Padding(4).AlignRight().Text(PdfKit.Amount(balance)).SemiBold();
    }

    private async Task<Customer> CustomerAsync(Guid id, CancellationToken ct) =>
        await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Customer", id);

    private async Task<List<PrintLine>> PricedLinesAsync<T>(IReadOnlyList<T> source, CancellationToken ct) where T : IPricedLine
    {
        var items = await inventory.GetItemsAsync(source.Select(l => l.ItemId), ct);
        var units = await inventory.GetUnitsAsync(source.Where(l => l.UomId != null).Select(l => l.ItemId), ct);
        // Print in the unit the customer was quoted in.
        return source.Select(l => new PrintLine(items[l.ItemId].Sku, items[l.ItemId].NameEn, items[l.ItemId].NameAr,
            (l as QuotationLine)?.Description ?? (l as SalesOrderLine)?.Description,
            units.FirstOrDefault(u => u.UomId == l.UomId)?.UomCode ?? items[l.ItemId].UomCode, l.UomQuantity, l.UomPrice, l.DiscountPercent,
            l.Amount)).ToList();
    }

    private static List<(string, decimal, string, bool)> Totals(IReadOnlyList<PrintLine> lines, decimal total)
    {
        var gross = lines.Sum(l => Money.Round(l.Quantity * (l.UnitPrice ?? 0)));
        List<(string, decimal, string, bool)> rows = [];
        if (gross != total)
        {
            rows.Add(("Gross amount", gross, "الإجمالي قبل الخصم", false));
            rows.Add(("Discount", total - gross, "الخصم", false));
        }
        rows.Add(("Total (QAR)", total, "الإجمالي (ريال قطري)", true));
        return rows;
    }

    private static List<PrintField> CustomerFields(Customer c, IEnumerable<PrintField> document) =>
    [
        new("Customer", $"{c.Code} — {c.NameEn}", "العميل"),
        new("Customer (Arabic)", c.NameAr, "اسم العميل"),
        new("Address", c.AddressEn ?? c.AddressAr, "العنوان"),
        new("Tax card no.", c.TaxCardNo, "الرقم الضريبي"),
        new("C.R. no.", c.CrNo, "السجل التجاري"),
        .. document.Where(f => !string.IsNullOrWhiteSpace(f.Value))
    ];

    private byte[] Render(string titleEn, string titleAr, string number, CompanyProfile company, IReadOnlyList<PrintField> fields, IReadOnlyList<PrintLine> lines,
        bool prices, IReadOnlyList<(string En, decimal Value, string Ar, bool Strong)> totals, decimal? words, string? notes,
        IReadOnlyList<(string En, string Ar)> signatures, bool draft, bool reversed = false)
    {
        PdfKit.EnsureInitialised();
        return Document.Create(container => container.Page(page =>
        {
            PdfKit.ApplyPage(page);
            page.Header().Element(c => PdfKit.Header(c, company, titleEn, titleAr, number));
            page.Content().PaddingTop(12).Column(col =>
            {
                col.Spacing(10);
                if (company.Tin is not null)
                    col.Item().Text($"Tax card no. {company.Tin} · الرقم الضريبي {company.Tin}").FontSize(8).FontColor(PdfKit.Muted);
                if (draft) col.Item().Background("#FFF4E5").Padding(6).Text("DRAFT · مسودة").Bold().FontColor("#9A5B00");
                if (reversed) col.Item().Background("#FDECEA").Padding(6).Text("CANCELLED (REVERSED) · ملغى (معكوس)").Bold().FontColor("#9B1C1C");
                col.Item().Element(c => FieldsTable(c, fields));
                col.Item().Element(c => LinesTable(c, lines, prices));
                if (totals.Count > 0) col.Item().Element(c => TotalsTable(c, totals));
                if (words is { } amount)
                    col.Item().Background(PdfKit.Shade).Padding(6).Column(c =>
                    {
                        c.Item().Text(AmountInWords.English(amount)).Italic();
                        c.Item().ContentFromRightToLeft().Text(AmountInWords.Arabic(amount));
                    });
                if (notes is not null) col.Item().Text($"Notes · ملاحظات: {notes}").FontSize(8);
                col.Item().PaddingTop(28).Row(row =>
                {
                    for (var i = 0; i < signatures.Count; i++)
                    {
                        if (i > 0) row.ConstantItem(24);
                        var (en, ar) = signatures[i];
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().LineHorizontal(0.8f).LineColor(PdfKit.Muted);
                            c.Item().PaddingTop(2).Text($"{en} · {ar}").FontSize(8).FontColor(PdfKit.Muted);
                        });
                    }
                });
            });
            page.Footer().Element(c => PdfKit.Footer(c, clock.UtcNow));
        })).GeneratePdf();
    }

    private static void FieldsTable(IContainer container, IReadOnlyList<PrintField> fields) =>
        container.Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn(2);
                c.RelativeColumn(4);
                c.RelativeColumn(2);
            });
            foreach (var f in fields.Where(f => !string.IsNullOrWhiteSpace(f.Value))) PdfKit.Field(t, f.LabelEn, f.Value, f.LabelAr);
        });

    private static void LinesTable(IContainer container, IReadOnlyList<PrintLine> lines, bool prices) =>
        container.Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(22);
                c.RelativeColumn(4);
                c.RelativeColumn(1.1f);
                if (prices)
                {
                    c.RelativeColumn(1.3f);
                    c.RelativeColumn(0.9f);
                    c.RelativeColumn(1.5f);
                }
            });
            t.Header(h =>
            {
                void Head(string text, bool right = false)
                {
                    var cell = h.Cell().Background(PdfKit.Primary).Padding(4);
                    (right ? cell.AlignRight() : cell).Text(text).FontColor(Colors.White).FontSize(8).Bold();
                }
                Head("#");
                Head("Description · البيان");
                Head("Qty · الكمية", true);
                if (prices)
                {
                    Head("Price · السعر", true);
                    Head("Disc. · خصم", true);
                    Head("Amount · المبلغ", true);
                }
            });
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var bg = i % 2 == 1 ? PdfKit.Shade : "#FFFFFF";
                t.Cell().Background(bg).Padding(4).Text((i + 1).ToString());
                t.Cell().Background(bg).Padding(4).Column(c =>
                {
                    c.Item().Text(l.Sku is null ? l.NameEn : $"{l.Sku} — {l.NameEn}");
                    if (l.NameAr != l.NameEn) c.Item().ContentFromRightToLeft().Text(l.NameAr).FontColor(PdfKit.Muted);
                    if (l.Note is not null && l.Note != l.NameEn) c.Item().Text(l.Note).FontSize(8).Italic();
                });
                t.Cell().Background(bg).Padding(4).AlignRight().Text($"{l.Quantity:0.###} {l.Uom}".Trim());
                if (prices)
                {
                    t.Cell().Background(bg).Padding(4).AlignRight().Text(l.UnitPrice is { } p ? p.ToString("N2") : "");
                    t.Cell().Background(bg).Padding(4).AlignRight().Text(l.DiscountPercent is > 0 ? $"{l.DiscountPercent:0.##}%" : "");
                    t.Cell().Background(bg).Padding(4).AlignRight().Text(l.Amount is { } a ? PdfKit.Amount(a) : "");
                }
            }
        });

    private static void TotalsTable(IContainer container, IReadOnlyList<(string En, decimal Value, string Ar, bool Strong)> rows) =>
        container.AlignRight().Width(300).Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn(2);
                c.RelativeColumn(1.4f);
                c.RelativeColumn(2);
            });
            foreach (var (en, value, ar, strong) in rows)
            {
                var bg = strong ? PdfKit.Shade : "#FFFFFF";
                var left = t.Cell().Background(bg).Padding(4).Text(en);
                var mid = t.Cell().Background(bg).Padding(4).AlignRight().Text(PdfKit.Amount(value));
                var right = t.Cell().Background(bg).Padding(4).AlignRight().Text(ar);
                if (!strong) continue;
                left.Bold();
                mid.Bold().FontColor(PdfKit.Primary);
                right.Bold();
            }
        });

    private static string CurrencyAr(string code) => code switch
    {
        "QAR" => "ريال قطري", "USD" => "دولار أمريكي", "EUR" => "يورو", "GBP" => "جنيه إسترليني", "SAR" => "ريال سعودي", "AED" => "درهم إماراتي",
        "KWD" => "دينار كويتي", "OMR" => "ريال عماني", "BHD" => "دينار بحريني", "INR" => "روبية هندية", "CNY" => "يوان صيني", _ => code
    };
}
