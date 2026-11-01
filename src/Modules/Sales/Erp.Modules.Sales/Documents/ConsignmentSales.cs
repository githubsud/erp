using Erp.Infrastructure.Persistence;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Documents;

/// <summary>Sales and returns of a partner shop's goods, in QAR, for its statement.</summary>
internal sealed class ConsignmentSales(ErpDbContext db, ITenantContext tenant) : IConsignmentSales
{
    public async Task<IReadOnlyList<ConsignmentSaleLine>> GetLinesAsync(Guid consignorId, DateOnly periodStart, DateOnly periodEnd, CancellationToken ct = default)
    {
        var companyId = tenant.RequireCompanyId();
        var sold = await (from l in db.Set<SalesInvoiceLine>().AsNoTracking()
                          join i in db.Set<SalesInvoice>() on l.SalesInvoiceId equals i.Id
                          where i.CompanyId == companyId && l.ConsignorId == consignorId && i.InvoiceDate >= periodStart && i.InvoiceDate <= periodEnd
                                && (i.Status == InvoiceStatus.Posted || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Paid)
                          select new { i.InvoiceDate, i.InvoiceNo, l.ItemId, l.Quantity, l.Amount, i.ExchangeRate, l.ConsignmentCommission }).ToListAsync(ct);
        var returned = await (from l in db.Set<CreditNoteLine>().AsNoTracking()
                              join n in db.Set<CreditNote>() on l.CreditNoteId equals n.Id
                              where n.CompanyId == companyId && l.ConsignorId == consignorId && n.NoteDate >= periodStart && n.NoteDate <= periodEnd
                                    && n.Status == DocumentStatus.Posted
                              select new { n.NoteDate, n.CreditNoteNo, l.ItemId, l.LineType, l.Quantity, l.Amount, n.ExchangeRate, l.ConsignmentCommission })
            .ToListAsync(ct);
        return sold.Select(x => new ConsignmentSaleLine(x.InvoiceDate, x.InvoiceNo, x.ItemId!.Value, x.Quantity, SalesRules.ToBase(x.Amount, x.ExchangeRate),
                x.ConsignmentCommission ?? 0))
            .Concat(returned.Select(x => new ConsignmentSaleLine(x.NoteDate, x.CreditNoteNo, x.ItemId!.Value,
                x.LineType == CreditNoteLineType.Return ? -x.Quantity : 0, -SalesRules.ToBase(x.Amount, x.ExchangeRate), -(x.ConsignmentCommission ?? 0))))
            .OrderBy(x => x.Date).ThenBy(x => x.DocumentNo).ToList();
    }
}
