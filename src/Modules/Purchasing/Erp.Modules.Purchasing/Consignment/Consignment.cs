using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Purchasing.Documents;
using Erp.Modules.Purchasing.Domain;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Purchasing.Consignment;

/// <summary>ApplyToItems: the new commission is copied onto the shop's items now (sales already made keep theirs).</summary>
public sealed record AgreementRequest(Guid SupplierId, CommissionType CommissionType, decimal Commission, DateOnly StartsOn, DateOnly? EndsOn = null,
    int PayoutDay = 5, string? Notes = null, bool IsActive = true, bool ApplyToItems = true);

/// <summary>Owed: the shop's share of sales not settled yet (QAR). OnHand: the shop's bottles we hold now.</summary>
public sealed record AgreementDto(Guid Id, Guid SupplierId, string SupplierCode, string NameEn, string NameAr, string? Phone, string? CrNo,
    CommissionType CommissionType, decimal Commission, DateOnly StartsOn, DateOnly? EndsOn, int PayoutDay, string? Notes, bool IsActive, int ItemCount,
    decimal OnHand, decimal Owed, DateOnly? SettledTo);

/// <summary>Without a commission the items take the agreement's.</summary>
public sealed record ConsignedItemsRequest(IReadOnlyList<Guid> ItemIds, CommissionType? CommissionType = null, decimal? Commission = null);

public sealed record ReleaseItemsRequest(IReadOnlyList<Guid> ItemIds);

public sealed record ConsignedItemDto(Guid ItemId, string Sku, string NameEn, string NameAr, CommissionType CommissionType, decimal Commission,
    decimal? SalesPrice, decimal OnHand);

public sealed record MovementLineRequest(Guid ItemId, decimal Quantity);

public sealed record MovementRequest(Guid SupplierId, Guid WarehouseId, DateOnly Date, string? Notes, IReadOnlyList<MovementLineRequest> Lines);

public sealed record MovementLineDto(Guid ItemId, string Sku, string NameEn, string NameAr, decimal Quantity);

public sealed record MovementDto(Guid Id, string MovementNo, ConsignmentMovementType MovementType, Guid SupplierId, string SupplierNameEn,
    string SupplierNameAr, Guid WarehouseId, DateOnly Date, string? Notes, DocumentStatus Status, IReadOnlyList<MovementLineDto> Lines);

public sealed record SettlementRequest(Guid SupplierId, DateOnly PeriodEnd);

/// <summary>One perfume on a statement: bottles sold and returned, sales, our commission and the shop's share (QAR).</summary>
public sealed record StatementLineDto(Guid ItemId, string Sku, string NameEn, string NameAr, decimal Sold, decimal Returned, decimal Sales,
    decimal Commission, decimal Due);

/// <summary>
/// A partner shop's statement for a period. <see cref="Adjustments"/>: what was owed from before the period (a late return, an invoice dated
/// in an earlier month) so that <see cref="Amount"/> is exactly what the shop is owed up to the period end.
/// </summary>
public sealed record StatementDto(Guid SupplierId, string SupplierCode, string SupplierNameEn, string SupplierNameAr, DateOnly PeriodStart,
    DateOnly PeriodEnd, IReadOnlyList<StatementLineDto> Lines, decimal Sales, decimal Commission, decimal Due, decimal Adjustments, decimal Amount,
    IReadOnlyList<ConsignedItemDto> OnHand, CommissionType CommissionType, decimal CommissionRate, Guid? SettlementId = null,
    string? SettlementNo = null, Guid? SupplierInvoiceId = null, string? InvoiceNo = null, string? InvoiceStatus = null, decimal? Paid = null);

public sealed record SettlementDto(Guid Id, string SettlementNo, Guid SupplierId, string SupplierNameEn, string SupplierNameAr, DateOnly PeriodStart,
    DateOnly PeriodEnd, decimal Sales, decimal Commission, decimal Amount, Guid SupplierInvoiceId, string InvoiceNo, string InvoiceStatus, decimal Paid);

public sealed record CommissionRowDto(string Month, Guid SupplierId, string SupplierNameEn, string SupplierNameAr, decimal Sold, decimal Sales,
    decimal Commission, decimal Due);

/// <summary>
/// Goods on trust (بضاعة أمانة): partner shops' agreements, the bottles received from them and handed back (zero cost, they stay the shops'
/// property), and the monthly settlement. Sales split the shop's share from our commission when invoiced (Sales); a settlement turns everything
/// owed to a shop up to the period end into one supplier invoice, paid like any other.
/// </summary>
internal sealed class ConsignmentService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    IConsignmentItems consignmentItems, IStockService stock, IPostingService posting, IFinanceQueries finance, IConsignmentSales sales, IClock clock)
{
    public const string ReceiptSource = "consignment_receipt";
    public const string ReturnSource = "consignment_return";
    public const string SettlementSource = "consignment_settlement";

    public async Task<List<AgreementDto>> AgreementsAsync(CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var rows = await (from a in db.Set<ConsignmentAgreement>().AsNoTracking()
                          join s in db.Set<Supplier>() on a.SupplierId equals s.Id
                          where a.CompanyId == companyId
                          orderby s.NameEn
                          select new { a, s }).ToListAsync(ct);
        var settled = await db.Set<ConsignmentSettlement>().AsNoTracking()
            .Where(x => x.CompanyId == companyId && db.Set<SupplierInvoice>().Any(i => i.Id == x.SupplierInvoiceId && i.Status != InvoiceStatus.Reversed))
            .GroupBy(x => x.SupplierId).Select(g => new { g.Key, To = g.Max(x => x.PeriodEnd) }).ToDictionaryAsync(x => x.Key, x => x.To, ct);
        var list = new List<AgreementDto>();
        foreach (var r in rows)
        {
            var items = await ItemsAsync(r.a.SupplierId, ct);
            var owed = -await finance.GetPartyBalanceAsync(AccountRoles.ConsignmentPayable, PartyType.Supplier, r.a.SupplierId, clock.Today, ct);
            list.Add(ToDto(r.a, r.s, items.Count, items.Sum(i => i.OnHand), owed, settled.TryGetValue(r.a.SupplierId, out var to) ? to : null));
        }
        return list;
    }

    public async Task<AgreementDto> AgreementAsync(Guid supplierId, CancellationToken ct) =>
        (await AgreementsAsync(ct)).SingleOrDefault(a => a.SupplierId == supplierId) ?? throw new NotFoundException("Partner shop", supplierId);

    public Task<ConsignmentAgreement> SaveAgreementAsync(AgreementRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var supplier = await SupplierAsync(r.SupplierId, token);
            if (r.Commission < 0 || (r.CommissionType == CommissionType.Percent && r.Commission > 100))
                throw new BusinessRuleException("pur.commission_invalid", "The commission must be between 0 and 100%, or a positive amount per bottle.",
                    "يجب أن تكون العمولة بين 0 و100% أو مبلغاً موجباً عن كل قارورة.");
            if (r.PayoutDay is < 1 or > 28)
                throw new BusinessRuleException("pur.payout_day", "Choose a payout day from 1 to 28.", "اختر يوم السداد من 1 إلى 28.");
            if (r.EndsOn is { } end && end < r.StartsOn)
                throw new BusinessRuleException("pur.agreement_dates", "The agreement cannot end before it starts.", "لا يمكن أن ينتهي الاتفاق قبل بدايته.");
            var agreement = await db.Set<ConsignmentAgreement>().SingleOrDefaultAsync(a => a.CompanyId == supplier.CompanyId && a.SupplierId == supplier.Id, token);
            if (agreement is null)
            {
                agreement = new ConsignmentAgreement { CompanyId = supplier.CompanyId, SupplierId = supplier.Id };
                db.Add(agreement);
            }
            agreement.CommissionType = r.CommissionType;
            agreement.Commission = Math.Round(r.Commission, 6, MidpointRounding.AwayFromZero);
            agreement.StartsOn = r.StartsOn;
            agreement.EndsOn = r.EndsOn;
            agreement.PayoutDay = r.PayoutDay;
            agreement.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
            agreement.IsActive = r.IsActive;
            await db.SaveChangesAsync(token);
            if (r.ApplyToItems)
            {
                var ids = await consignmentItems.ItemsOfAsync(supplier.Id, token);
                if (ids.Count > 0) await consignmentItems.SetAsync(ids, Terms(agreement), token);
            }
            return agreement;
        }, ct);

    public async Task<List<ConsignedItemDto>> ItemsAsync(Guid supplierId, CancellationToken ct)
    {
        var ids = await consignmentItems.ItemsOfAsync(supplierId, ct);
        var items = await inventory.GetItemsAsync(ids, ct);
        var list = new List<ConsignedItemDto>();
        foreach (var i in items.Values.OrderBy(i => i.Sku))
            list.Add(new ConsignedItemDto(i.Id, i.Sku, i.NameEn, i.NameAr, i.Consignment!.CommissionType, i.Consignment.Commission, i.SalesPrice,
                await inventory.GetQuantityOnHandAsync(i.Id, ct)));
        return list;
    }

    /// <summary>Puts items under the shop's terms (or changes their commission). An item changes owner only while none of it is in stock.</summary>
    public Task AddItemsAsync(Guid supplierId, ConsignedItemsRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var agreement = await ActiveAgreementAsync(supplierId, token);
            if (r.ItemIds is not { Count: > 0 })
                throw new BusinessRuleException("pur.no_lines", "Choose at least one item.", "اختر صنفاً واحداً على الأقل.");
            var items = await inventory.GetItemsAsync(r.ItemIds, token);
            foreach (var other in items.Values.Where(i => i.Consignment is { } c && c.ConsignorId != supplierId))
                throw new BusinessRuleException("pur.item_other_shop", $"{other.Sku} already belongs to another partner shop.",
                    $"الصنف {other.Sku} مملوك لمحل شريك آخر.");
            var terms = r.CommissionType is { } type && r.Commission is { } value
                ? new ConsignmentTerms(supplierId, type, value)
                : Terms(agreement);
            await consignmentItems.SetAsync(r.ItemIds, terms, token);
            return 0;
        }, ct);

    /// <summary>Items become ours again (only when none of them is in stock: hand the shop's bottles back first).</summary>
    public Task ReleaseItemsAsync(Guid supplierId, ReleaseItemsRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var items = await inventory.GetItemsAsync(r.ItemIds ?? [], token);
            foreach (var i in items.Values.Where(i => i.Consignment?.ConsignorId != supplierId))
                throw new BusinessRuleException("pur.item_not_shop", $"{i.Sku} does not belong to this partner shop.", $"الصنف {i.Sku} لا يخص هذا المحل الشريك.");
            await consignmentItems.SetAsync(items.Keys.ToList(), null, token);
            return 0;
        }, ct);

    public async Task<List<MovementDto>> MovementsAsync(Guid? supplierId, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var list = await db.Set<ConsignmentMovement>().AsNoTracking().Include(m => m.Lines)
            .Where(m => m.CompanyId == companyId && (supplierId == null || m.SupplierId == supplierId))
            .OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.MovementNo).Take(200).ToListAsync(ct);
        return await ToDtosAsync(list, ct);
    }

    public async Task<MovementDto> MovementAsync(Guid id, CancellationToken ct) =>
        (await ToDtosAsync([await FindMovementAsync(id, ct)], ct)).Single();

    /// <summary>Bottles in from the shop (receipt) or back to it (return), at zero cost: stock moves, the ledger does not.</summary>
    public Task<ConsignmentMovement> MoveAsync(ConsignmentMovementType type, MovementRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var agreement = await AgreementOfAsync(r.SupplierId, token);
            if (type == ConsignmentMovementType.Receipt && !agreement.IsActive)
                throw new BusinessRuleException("pur.agreement_inactive", "This partner shop's agreement is not active.", "اتفاق هذا المحل الشريك غير نشط.");
            if (await inventory.GetWarehouseAsync(r.WarehouseId, token) is not { IsActive: true }) throw new NotFoundException("Warehouse", r.WarehouseId);
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Quantity <= 0))
                throw new BusinessRuleException("pur.no_lines", "Add bottles with positive quantities.", "أضف قوارير بكميات موجبة.");
            var items = await inventory.GetItemsAsync(r.Lines.Select(l => l.ItemId), token);
            foreach (var l in r.Lines)
            {
                if (!items.TryGetValue(l.ItemId, out var item)) throw new NotFoundException("Item", l.ItemId);
                if (item.Consignment?.ConsignorId != r.SupplierId)
                    throw new BusinessRuleException("pur.item_not_shop", $"{item.Sku} does not belong to this partner shop.", $"الصنف {item.Sku} لا يخص هذا المحل الشريك.");
            }
            var source = type == ConsignmentMovementType.Receipt ? ReceiptSource : ReturnSource;
            var movement = new ConsignmentMovement
            {
                CompanyId = agreement.CompanyId, MovementType = type, SupplierId = r.SupplierId, WarehouseId = r.WarehouseId, MovementDate = r.Date,
                Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
                MovementNo = await numbering.NextAsync(source, type == ConsignmentMovementType.Receipt ? "GTR" : "GTB", r.Date, ct: token)
            };
            var lineNo = 0;
            foreach (var l in r.Lines)
                movement.Lines.Add(new ConsignmentMovementLine { ConsignmentMovementId = movement.Id, LineNo = ++lineNo, ItemId = l.ItemId, Quantity = l.Quantity });
            db.Add(movement);
            var sign = type == ConsignmentMovementType.Receipt ? 1 : -1;
            await stock.PostAsync(new StockPosting(source, movement.Id, movement.MovementNo, r.Date,
                movement.Lines.Select(l => new StockLine(l.ItemId, r.WarehouseId, sign * l.Quantity, UnitCost: 0, SourceLineId: l.Id)).ToList(),
                IssueExpired: type == ConsignmentMovementType.Return), token);
            await db.SaveChangesAsync(token);
            return movement;
        }, ct);

    public Task<ConsignmentMovement> ReverseMovementAsync(Guid id, ReverseRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var movement = await FindMovementAsync(id, token, tracked: true);
            if (movement.Status != DocumentStatus.Posted)
                throw new ConflictException("pur.not_posted", "This document is already reversed.", "تم عكس هذا المستند مسبقاً.");
            Guard.Required(r.Reason, "reason");
            await stock.ReverseAsync(movement.MovementType == ConsignmentMovementType.Receipt ? ReceiptSource : ReturnSource, movement.Id, r.Date, token);
            movement.Status = DocumentStatus.Reversed;
            await db.SaveChangesAsync(token);
            return movement;
        }, ct);

    /// <summary>The statement a settlement up to <paramref name="periodEnd"/> would make, without making it.</summary>
    public async Task<StatementDto> PreviewAsync(Guid supplierId, DateOnly periodEnd, CancellationToken ct)
    {
        var agreement = await AgreementOfAsync(supplierId, ct);
        var start = await NextPeriodStartAsync(agreement, ct);
        if (periodEnd < start)
            throw new BusinessRuleException("pur.settlement_period", $"This shop is settled up to {start.AddDays(-1):dd/MM/yyyy}; choose a later date.",
                $"تمت تسوية هذا المحل حتى {start.AddDays(-1):dd/MM/yyyy}؛ اختر تاريخاً لاحقاً.");
        var amount = -await finance.GetPartyBalanceAsync(AccountRoles.ConsignmentPayable, PartyType.Supplier, supplierId, periodEnd, ct);
        return await StatementAsync(agreement, start, periodEnd, amount, null, ct);
    }

    /// <summary>
    /// Everything owed to the shop up to the period end becomes one posted supplier invoice: debit what is owed to partner shops, credit
    /// accounts payable. The statement shows the period's sales; anything owed from before shows as adjustments.
    /// </summary>
    public Task<ConsignmentSettlement> SettleAsync(SettlementRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            // One settlement at a time per shop: the agreement row is the lock.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM pur.consignment_agreement WHERE tenant_id = {tenant.RequireTenantId()} AND company_id = {tenant.RequireCompanyId()} AND supplier_id = {r.SupplierId} FOR UPDATE", token);
            var agreement = await AgreementOfAsync(r.SupplierId, token);
            if (r.PeriodEnd > clock.Today)
                throw new BusinessRuleException("pur.settlement_future", "A settlement cannot end after today.", "لا يمكن أن تنتهي التسوية بعد اليوم.");
            var statement = await PreviewAsync(r.SupplierId, r.PeriodEnd, token);
            if (statement.Amount <= 0)
                throw new BusinessRuleException("pur.settlement_nothing", "Nothing is owed to this shop up to that date.", "لا يوجد مستحق لهذا المحل حتى ذلك التاريخ.");
            var supplier = await SupplierAsync(r.SupplierId, token);
            var settlementNo = await numbering.NextAsync(SettlementSource, "CST", r.PeriodEnd, ct: token);
            var invoice = new SupplierInvoice
            {
                CompanyId = supplier.CompanyId, InvoiceNo = await numbering.NextAsync(SupplierInvoiceService.SourceType, "PINV", r.PeriodEnd, ct: token),
                SupplierId = supplier.Id, SupplierRef = settlementNo, InvoiceDate = r.PeriodEnd, DueDate = PayoutDate(r.PeriodEnd, agreement.PayoutDay),
                Total = statement.Amount, TotalBase = statement.Amount, Status = InvoiceStatus.Posted, MatchStatus = MatchStatus.NotApplicable,
                Notes = $"Goods on trust {statement.PeriodStart:dd/MM/yyyy}–{r.PeriodEnd:dd/MM/yyyy} · بضاعة أمانة"
            };
            invoice.Lines.Add(new SupplierInvoiceLine
            {
                SupplierInvoiceId = invoice.Id, LineNo = 1, LineType = InvoiceLineType.Service, Quantity = 1, UnitPrice = statement.Amount, Amount = statement.Amount,
                Description = $"Partner shop statement {settlementNo} · كشف حساب المحل الشريك"
            });
            db.Add(invoice);
            invoice.JournalEntryId = (await posting.PostAsync(new PostingRequest
            {
                SourceType = SupplierInvoiceService.SourceType, SourceId = invoice.Id, SourceNo = invoice.InvoiceNo, PostingDate = r.PeriodEnd,
                MemoEn = $"Goods on trust settlement {settlementNo} — {supplier.NameEn}", MemoAr = $"تسوية بضاعة الأمانة {settlementNo} — {supplier.NameAr}",
                Lines =
                [
                    new PostingLine
                    {
                        AccountRole = AccountRoles.ConsignmentPayable, Side = PostingSide.Debit, Amount = statement.Amount, PartyType = PartyType.Supplier,
                        PartyId = supplier.Id
                    },
                    new PostingLine
                    {
                        AccountRole = AccountRoles.AccountsPayable, Side = PostingSide.Credit, Amount = statement.Amount, PartyType = PartyType.Supplier,
                        PartyId = supplier.Id
                    }
                ]
            }, token)).JournalEntryId;
            var settlement = new ConsignmentSettlement
            {
                CompanyId = supplier.CompanyId, SettlementNo = settlementNo, SupplierId = supplier.Id, PeriodStart = statement.PeriodStart, PeriodEnd = r.PeriodEnd,
                Sales = statement.Sales, Commission = statement.Commission, Amount = statement.Amount, SupplierInvoiceId = invoice.Id
            };
            db.Add(settlement);
            await db.SaveChangesAsync(token);
            return settlement;
        }, ct);

    public async Task<List<SettlementDto>> SettlementsAsync(Guid? supplierId, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        return await (from x in db.Set<ConsignmentSettlement>().AsNoTracking()
                      join s in db.Set<Supplier>() on x.SupplierId equals s.Id
                      join i in db.Set<SupplierInvoice>() on x.SupplierInvoiceId equals i.Id
                      where x.CompanyId == companyId && (supplierId == null || x.SupplierId == supplierId)
                      orderby x.PeriodEnd descending, x.SettlementNo descending
                      select new SettlementDto(x.Id, x.SettlementNo, x.SupplierId, s.NameEn, s.NameAr, x.PeriodStart, x.PeriodEnd, x.Sales, x.Commission,
                          x.Amount, i.Id, i.InvoiceNo, i.Status.ToString(), i.PaidAmount)).Take(200).ToListAsync(ct);
    }

    /// <summary>A settled statement, as it was made (the period's sales) with its invoice and what has been paid.</summary>
    public async Task<StatementDto> SettlementStatementAsync(Guid id, CancellationToken ct)
    {
        var x = await db.Set<ConsignmentSettlement>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == tenant.RequireCompanyId(), ct)
                ?? throw new NotFoundException("Settlement", id);
        var agreement = await AgreementOfAsync(x.SupplierId, ct);
        var invoice = await db.Set<SupplierInvoice>().AsNoTracking().SingleAsync(i => i.Id == x.SupplierInvoiceId, ct);
        return (await StatementAsync(agreement, x.PeriodStart, x.PeriodEnd, x.Amount, x, ct)) with
        {
            SupplierInvoiceId = invoice.Id, InvoiceNo = invoice.InvoiceNo, InvoiceStatus = invoice.Status.ToString(), Paid = invoice.PaidAmount
        };
    }

    /// <summary>Commission by month and partner shop (invoice and credit note dates).</summary>
    public async Task<List<CommissionRowDto>> CommissionsAsync(DateOnly periodStart, DateOnly periodEnd, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var shops = await (from a in db.Set<ConsignmentAgreement>().AsNoTracking()
                           join s in db.Set<Supplier>() on a.SupplierId equals s.Id
                           where a.CompanyId == companyId
                           select s).ToListAsync(ct);
        var rows = new List<CommissionRowDto>();
        foreach (var shop in shops)
        {
            var lines = await sales.GetLinesAsync(shop.Id, periodStart, periodEnd, ct);
            rows.AddRange(lines.GroupBy(l => $"{l.Date:yyyy-MM}").Select(g => new CommissionRowDto(g.Key, shop.Id, shop.NameEn, shop.NameAr,
                g.Sum(l => l.Quantity), g.Sum(l => l.Amount), g.Sum(l => l.Commission), g.Sum(l => l.Amount - l.Commission))));
        }
        return rows.OrderByDescending(r => r.Month).ThenBy(r => r.SupplierNameEn).ToList();
    }

    private async Task<StatementDto> StatementAsync(ConsignmentAgreement agreement, DateOnly start, DateOnly end, decimal amount,
        ConsignmentSettlement? settlement, CancellationToken ct)
    {
        var supplier = await db.Set<Supplier>().AsNoTracking().SingleAsync(s => s.Id == agreement.SupplierId, ct);
        var lines = await sales.GetLinesAsync(agreement.SupplierId, start, end, ct);
        var items = await inventory.GetItemsAsync(lines.Select(l => l.ItemId), ct);
        var rows = lines.GroupBy(l => l.ItemId).Select(g =>
            {
                var item = items[g.Key];
                var amountSum = g.Sum(l => l.Amount);
                var commission = g.Sum(l => l.Commission);
                return new StatementLineDto(g.Key, item.Sku, item.NameEn, item.NameAr, g.Where(l => l.Quantity > 0).Sum(l => l.Quantity),
                    -g.Where(l => l.Quantity < 0).Sum(l => l.Quantity), amountSum, commission, amountSum - commission);
            })
            .OrderBy(r => r.Sku).ToList();
        var due = rows.Sum(r => r.Due);
        return new StatementDto(supplier.Id, supplier.Code, supplier.NameEn, supplier.NameAr, start, end, rows, rows.Sum(r => r.Sales),
            rows.Sum(r => r.Commission), due, amount - due, amount, settlement is null ? await ItemsAsync(supplier.Id, ct) : [], agreement.CommissionType,
            agreement.Commission, settlement?.Id, settlement?.SettlementNo);
    }

    /// <summary>The day after the last settlement that still stands (its invoice not reversed), else the agreement's start.</summary>
    private async Task<DateOnly> NextPeriodStartAsync(ConsignmentAgreement agreement, CancellationToken ct)
    {
        var last = await db.Set<ConsignmentSettlement>().AsNoTracking()
            .Where(x => x.CompanyId == agreement.CompanyId && x.SupplierId == agreement.SupplierId
                        && db.Set<SupplierInvoice>().Any(i => i.Id == x.SupplierInvoiceId && i.Status != InvoiceStatus.Reversed))
            .MaxAsync(x => (DateOnly?)x.PeriodEnd, ct);
        return last?.AddDays(1) ?? agreement.StartsOn;
    }

    /// <summary>The payout day in the month after the period end.</summary>
    internal static DateOnly PayoutDate(DateOnly periodEnd, int payoutDay)
    {
        var next = new DateOnly(periodEnd.Year, periodEnd.Month, 1).AddMonths(1);
        return new DateOnly(next.Year, next.Month, Math.Clamp(payoutDay, 1, 28));
    }

    private static ConsignmentTerms Terms(ConsignmentAgreement a) => new(a.SupplierId, a.CommissionType, a.Commission);

    private async Task<Supplier> SupplierAsync(Guid id, CancellationToken ct) => await PurchaseOrderService.ActiveSupplierAsync(db, tenant, id, ct);

    private async Task<ConsignmentAgreement> AgreementOfAsync(Guid supplierId, CancellationToken ct) =>
        await db.Set<ConsignmentAgreement>().AsNoTracking().SingleOrDefaultAsync(a => a.CompanyId == tenant.RequireCompanyId() && a.SupplierId == supplierId, ct)
        ?? throw new NotFoundException("pur.not_partner_shop", "This supplier is not a partner shop. Save its agreement first.",
            "هذا المورد ليس محلاً شريكاً. احفظ اتفاقه أولاً.");

    private async Task<ConsignmentAgreement> ActiveAgreementAsync(Guid supplierId, CancellationToken ct)
    {
        var agreement = await AgreementOfAsync(supplierId, ct);
        if (!agreement.IsActive)
            throw new BusinessRuleException("pur.agreement_inactive", "This partner shop's agreement is not active.", "اتفاق هذا المحل الشريك غير نشط.");
        return agreement;
    }

    private async Task<ConsignmentMovement> FindMovementAsync(Guid id, CancellationToken ct, bool tracked = false)
    {
        var query = db.Set<ConsignmentMovement>().Include(m => m.Lines).AsQueryable();
        if (!tracked) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(m => m.Id == id && m.CompanyId == tenant.RequireCompanyId(), ct)
               ?? throw new NotFoundException("Goods on trust document", id);
    }

    private async Task<List<MovementDto>> ToDtosAsync(List<ConsignmentMovement> list, CancellationToken ct)
    {
        var supplierIds = list.Select(m => m.SupplierId).Distinct().ToList();
        var suppliers = await db.Set<Supplier>().AsNoTracking().Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var items = await inventory.GetItemsAsync(list.SelectMany(m => m.Lines.Select(l => l.ItemId)), ct);
        return list.Select(m => new MovementDto(m.Id, m.MovementNo, m.MovementType, m.SupplierId, suppliers[m.SupplierId].NameEn, suppliers[m.SupplierId].NameAr,
            m.WarehouseId, m.MovementDate, m.Notes, m.Status, m.Lines.OrderBy(l => l.LineNo).Select(l =>
            {
                var item = items[l.ItemId];
                return new MovementLineDto(l.ItemId, item.Sku, item.NameEn, item.NameAr, l.Quantity);
            }).ToList())).ToList();
    }

    private static AgreementDto ToDto(ConsignmentAgreement a, Supplier s, int itemCount, decimal onHand, decimal owed, DateOnly? settledTo) =>
        new(a.Id, a.SupplierId, s.Code, s.NameEn, s.NameAr, s.Phone, s.CrNo, a.CommissionType, a.Commission, a.StartsOn, a.EndsOn, a.PayoutDay, a.Notes,
            a.IsActive, itemCount, onHand, owed, settledTo);
}

internal static class ConsignmentEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/purchasing/consignment").WithTags("Goods on trust").RequireAuthorization();

        g.MapGet("/shops", (ConsignmentService s, CancellationToken ct) => s.AgreementsAsync(ct)).RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/shops/{supplierId:guid}", (Guid supplierId, ConsignmentService s, CancellationToken ct) => s.AgreementAsync(supplierId, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPut("/shops", async (AgreementRequest r, ConsignmentService s, CancellationToken ct) =>
            await s.AgreementAsync((await s.SaveAgreementAsync(r, ct)).SupplierId, ct)).RequirePermission(PurchasingPermissions.ConsignmentManage);
        g.MapGet("/shops/{supplierId:guid}/items", (Guid supplierId, ConsignmentService s, CancellationToken ct) => s.ItemsAsync(supplierId, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/shops/{supplierId:guid}/items", async (Guid supplierId, ConsignedItemsRequest r, ConsignmentService s, CancellationToken ct) =>
        {
            await s.AddItemsAsync(supplierId, r, ct);
            return await s.ItemsAsync(supplierId, ct);
        }).RequirePermission(PurchasingPermissions.ConsignmentManage);
        g.MapPost("/shops/{supplierId:guid}/items/release", async (Guid supplierId, ReleaseItemsRequest r, ConsignmentService s, CancellationToken ct) =>
        {
            await s.ReleaseItemsAsync(supplierId, r, ct);
            return await s.ItemsAsync(supplierId, ct);
        }).RequirePermission(PurchasingPermissions.ConsignmentManage);

        g.MapGet("/movements", (ConsignmentService s, CancellationToken ct, Guid? supplierId = null) => s.MovementsAsync(supplierId, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/movements/{id:guid}", (Guid id, ConsignmentService s, CancellationToken ct) => s.MovementAsync(id, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/receipts", async (MovementRequest r, ConsignmentService s, CancellationToken ct) =>
            await s.MovementAsync((await s.MoveAsync(ConsignmentMovementType.Receipt, r, ct)).Id, ct)).RequirePermission(PurchasingPermissions.ConsignmentManage);
        g.MapPost("/returns", async (MovementRequest r, ConsignmentService s, CancellationToken ct) =>
            await s.MovementAsync((await s.MoveAsync(ConsignmentMovementType.Return, r, ct)).Id, ct)).RequirePermission(PurchasingPermissions.ConsignmentManage);
        g.MapPost("/movements/{id:guid}/reverse", async (Guid id, ReverseRequest r, ConsignmentService s, CancellationToken ct) =>
            await s.MovementAsync((await s.ReverseMovementAsync(id, r, ct)).Id, ct)).RequirePermission(PurchasingPermissions.ConsignmentManage);

        g.MapGet("/statement", (Guid supplierId, DateOnly periodEnd, ConsignmentService s, CancellationToken ct) => s.PreviewAsync(supplierId, periodEnd, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/settlements", (ConsignmentService s, CancellationToken ct, Guid? supplierId = null) => s.SettlementsAsync(supplierId, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapGet("/settlements/{id:guid}", (Guid id, ConsignmentService s, CancellationToken ct) => s.SettlementStatementAsync(id, ct))
            .RequirePermission(PurchasingPermissions.SuppliersRead);
        g.MapPost("/settlements", async (SettlementRequest r, ConsignmentService s, CancellationToken ct) =>
            await s.SettlementStatementAsync((await s.SettleAsync(r, ct)).Id, ct)).RequirePermission(PurchasingPermissions.InvoicesPost);

        g.MapGet("/commissions", (DateOnly periodStart, DateOnly periodEnd, ConsignmentService s, CancellationToken ct) =>
            s.CommissionsAsync(periodStart, periodEnd, ct)).RequirePermission(PurchasingPermissions.ReportsRead);
    }
}
