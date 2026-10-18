using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Inventory.Contracts;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Purchasing.Documents;

public enum PurchaseRequestStatus { Draft, PendingApproval, Approved, Ordered, Cancelled }

public sealed class PurchaseRequest : TenantEntity
{
    public Guid CompanyId { get; init; }
    public required string PrNo { get; init; }
    public DateOnly RequestDate { get; set; }
    public DateOnly? RequiredDate { get; set; }
    public Guid WarehouseId { get; set; }
    public PurchaseRequestStatus Status { get; set; }
    public decimal EstimatedTotal { get; set; }
    public string? Notes { get; set; }
    public Guid RequestedBy { get; init; }
    public Guid? PurchaseOrderId { get; set; }
    public List<PurchaseRequestLine> Lines { get; init; } = [];
}

public sealed class PurchaseRequestLine : TenantEntity
{
    public Guid PurchaseRequestId { get; init; }
    public int LineNo { get; init; }
    public Guid ItemId { get; init; }
    public string? Description { get; init; }
    public decimal Quantity { get; init; }
    public decimal EstimatedPrice { get; init; }
    public Guid? UomId { get; init; }
}

internal sealed class PurchaseRequestConfiguration : IEntityTypeConfiguration<PurchaseRequest>
{
    public void Configure(EntityTypeBuilder<PurchaseRequest> b)
    {
        b.ToTable("purchase_request", "pur");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseRequestId);
    }
}

internal sealed class PurchaseRequestLineConfiguration : IEntityTypeConfiguration<PurchaseRequestLine>
{
    public void Configure(EntityTypeBuilder<PurchaseRequestLine> b)
    {
        b.ToTable("purchase_request_line", "pur");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.EstimatedPrice).AsQuantity();
    }
}

/// <summary>Quantity and price are in <see cref="UomId"/> when given, otherwise in the item's base unit.</summary>
public sealed record RequestLineRequest(Guid ItemId, string? Description, decimal Quantity, decimal EstimatedPrice, Guid? UomId = null);

public sealed record PurchaseRequestRequest(DateOnly RequestDate, DateOnly? RequiredDate, Guid WarehouseId, string? Notes, IReadOnlyList<RequestLineRequest> Lines);

public sealed record ConvertRequestRequest(Guid SupplierId, DateOnly? OrderDate);

public sealed record RequestLineDto(int LineNo, Guid ItemId, string Sku, string ItemNameEn, string ItemNameAr, string? Description, decimal Quantity,
    decimal EstimatedPrice, Guid? UomId, decimal Amount);

public sealed record PurchaseRequestDto(Guid Id, string PrNo, DateOnly RequestDate, DateOnly? RequiredDate, Guid WarehouseId, string Status,
    decimal EstimatedTotal, string? Notes, Guid RequestedBy, Guid? PurchaseOrderId, IReadOnlyList<RequestLineDto> Lines);

/// <summary>Purchase requests: draft → submitted (approval when a rule applies) → approved → converted into a draft purchase order.</summary>
internal sealed class PurchaseRequestService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IInventoryQueries inventory,
    IApprovalService approvals, PurchaseOrderService orders, IClock clock)
{
    public const string SourceType = "purchase_request";

    public Task<PurchaseRequest> SaveAsync(Guid? id, PurchaseRequestRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            PurchaseRequest request;
            if (id is null)
            {
                request = new PurchaseRequest
                {
                    CompanyId = tenant.RequireCompanyId(), PrNo = await numbering.NextAsync(SourceType, "PR", r.RequestDate, ct: token),
                    RequestedBy = tenant.ActingUserId, Status = PurchaseRequestStatus.Draft
                };
                db.Add(request);
            }
            else
            {
                request = await FindAsync(id.Value, token);
                if (request.Status != PurchaseRequestStatus.Draft)
                    throw new ConflictException("pur.pr_not_draft", "Only draft requests can be edited.", "يمكن تعديل الطلبات المسودة فقط.");
                db.RemoveRange(request.Lines);
                request.Lines.Clear();
                await db.SaveChangesAsync(token);
            }

            var warehouse = await inventory.GetWarehouseAsync(r.WarehouseId, token);
            if (warehouse is not { IsActive: true }) throw new NotFoundException("Warehouse", r.WarehouseId);
            if (r.Lines is not { Count: > 0 })
                throw new BusinessRuleException("pur.no_lines", "Add at least one line.", "أضف سطراً واحداً على الأقل.");
            if (r.RequiredDate is { } required && required < r.RequestDate)
                throw new BusinessRuleException("pur.pr_dates", "The required date cannot be before the request date.", "لا يمكن أن يسبق تاريخ الحاجة تاريخ الطلب.");
            var items = await inventory.GetItemsAsync(r.Lines.Select(l => l.ItemId), token);
            var units = await inventory.GetUnitsAsync(r.Lines.Select(l => l.ItemId), token);

            request.RequestDate = r.RequestDate;
            request.RequiredDate = r.RequiredDate;
            request.WarehouseId = r.WarehouseId;
            request.Notes = SupplierService.Clean(r.Notes);
            var no = 0;
            foreach (var l in r.Lines)
            {
                if (!items.TryGetValue(l.ItemId, out var item) || !item.IsActive) throw new NotFoundException("Item", l.ItemId);
                if (l.Quantity <= 0 || l.EstimatedPrice < 0)
                    throw new BusinessRuleException("pur.line_values", "Quantities must be positive and prices not negative.", "يجب أن تكون الكميات موجبة والأسعار غير سالبة.");
                ItemUnits.Resolve(units, item, l.UomId, "pur");
                var line = new PurchaseRequestLine
                {
                    PurchaseRequestId = request.Id, LineNo = ++no, ItemId = l.ItemId, Description = SupplierService.Clean(l.Description),
                    Quantity = l.Quantity, EstimatedPrice = l.EstimatedPrice, UomId = l.UomId
                };
                request.Lines.Add(line);
                db.Add(line);
            }
            request.EstimatedTotal = request.Lines.Sum(l => Money.Round(l.Quantity * l.EstimatedPrice));
            await db.SaveChangesAsync(token);
            return request;
        }, ct);

    public Task<PurchaseRequest> SubmitAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var request = await FindAsync(id, token);
            if (request.Status != PurchaseRequestStatus.Draft)
                throw new ConflictException("pur.pr_not_draft", "Only draft requests can be submitted.", "يمكن إرسال الطلبات المسودة فقط.");
            var outcome = await approvals.SubmitAsync(new ApprovalSubmission(SourceType, request.Id, request.PrNo, request.EstimatedTotal,
                $"{request.Lines.Count} line(s) · est. QAR {request.EstimatedTotal:N2}", $"{request.Lines.Count} سطر · تقديرياً {request.EstimatedTotal:N2} ريال"),
                token);
            request.Status = outcome == ApprovalOutcome.Approved ? PurchaseRequestStatus.Approved : PurchaseRequestStatus.PendingApproval;
            await db.SaveChangesAsync(token);
            return request;
        }, ct);

    public Task<PurchaseRequest> CancelAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var request = await FindAsync(id, token);
            if (request.Status is PurchaseRequestStatus.Ordered or PurchaseRequestStatus.Cancelled)
                throw new ConflictException("pur.pr_closed", "This request is already ordered or cancelled.", "تم طلب أو إلغاء هذا الطلب مسبقاً.");
            await approvals.WithdrawAsync(SourceType, id, token);
            request.Status = PurchaseRequestStatus.Cancelled;
            await db.SaveChangesAsync(token);
            return request;
        }, ct);

    /// <summary>Creates a draft purchase order with the request's lines at the estimated prices, for the buyer to adjust and submit.</summary>
    public Task<Guid> ConvertAsync(Guid id, ConvertRequestRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var request = await FindAsync(id, token);
            if (request.Status != PurchaseRequestStatus.Approved)
                throw new ConflictException("pur.pr_not_approved", "Only approved requests can be turned into an order.", "يمكن تحويل الطلبات المعتمدة فقط إلى أمر شراء.");
            var order = await orders.CreateAsync(new OrderRequest(r.SupplierId, r.OrderDate ?? clock.Today, request.RequiredDate, request.WarehouseId,
                $"From {request.PrNo}" + (request.Notes is null ? "" : $" — {request.Notes}"),
                request.Lines.OrderBy(l => l.LineNo).Select(l => new OrderLineRequest(l.ItemId, l.Description, l.Quantity, l.EstimatedPrice, UomId: l.UomId)).ToList()),
                token);
            request.Status = PurchaseRequestStatus.Ordered;
            request.PurchaseOrderId = order.Id;
            await db.SaveChangesAsync(token);
            return order.Id;
        }, ct);

    internal async Task OnDecidedAsync(Guid id, bool approved, CancellationToken ct)
    {
        var request = await FindAsync(id, ct);
        if (request.Status != PurchaseRequestStatus.PendingApproval) return;
        request.Status = approved ? PurchaseRequestStatus.Approved : PurchaseRequestStatus.Draft;
        await db.SaveChangesAsync(ct);
    }

    internal async Task<PurchaseRequest> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<PurchaseRequest>().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Purchase request", id);
}

internal sealed class PurchaseRequestApproval(PurchaseRequestService requests) : IApprovalDocument
{
    public string DocType => PurchaseRequestService.SourceType;
    public string NameEn => "Purchase request";
    public string NameAr => "طلب شراء";
    public IReadOnlyList<ApprovalStepRule> DefaultSteps => [];
    public Task OnApprovedAsync(Guid docId, CancellationToken ct) => requests.OnDecidedAsync(docId, true, ct);
    public Task OnRejectedAsync(Guid docId, string? comment, CancellationToken ct) => requests.OnDecidedAsync(docId, false, ct);
}

internal static class PurchaseRequestEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/purchasing/requests").WithTags("Purchasing").RequireAuthorization();

        g.MapGet("", async (ErpDbContext db, ITenantContext t, IInventoryQueries inv, CancellationToken ct, string? status = null) =>
        {
            var q = db.Set<PurchaseRequest>().AsNoTracking().Include(x => x.Lines).Where(x => x.CompanyId == t.RequireCompanyId());
            if (Enum.TryParse<PurchaseRequestStatus>(status, true, out var st)) q = q.Where(x => x.Status == st);
            return await ToDtosAsync(await q.OrderByDescending(x => x.RequestDate).ThenByDescending(x => x.PrNo).Take(300).ToListAsync(ct), inv, ct);
        }).RequirePermission(PurchasingPermissions.RequestsManage);
        g.MapGet("/{id:guid}", async (Guid id, PurchaseRequestService s, IInventoryQueries inv, CancellationToken ct) =>
            (await ToDtosAsync([await s.FindAsync(id, ct)], inv, ct)).Single()).RequirePermission(PurchasingPermissions.RequestsManage);
        g.MapPost("", async (PurchaseRequestRequest r, PurchaseRequestService s, IInventoryQueries inv, CancellationToken ct) =>
            (await ToDtosAsync([await s.SaveAsync(null, r, ct)], inv, ct)).Single()).RequirePermission(PurchasingPermissions.RequestsManage);
        g.MapPut("/{id:guid}", async (Guid id, PurchaseRequestRequest r, PurchaseRequestService s, IInventoryQueries inv, CancellationToken ct) =>
            (await ToDtosAsync([await s.SaveAsync(id, r, ct)], inv, ct)).Single()).RequirePermission(PurchasingPermissions.RequestsManage);
        g.MapPost("/{id:guid}/submit", async (Guid id, PurchaseRequestService s, IInventoryQueries inv, CancellationToken ct) =>
            (await ToDtosAsync([await s.SubmitAsync(id, ct)], inv, ct)).Single()).RequirePermission(PurchasingPermissions.RequestsManage);
        g.MapPost("/{id:guid}/cancel", async (Guid id, PurchaseRequestService s, IInventoryQueries inv, CancellationToken ct) =>
            (await ToDtosAsync([await s.CancelAsync(id, ct)], inv, ct)).Single()).RequirePermission(PurchasingPermissions.RequestsManage);
        g.MapPost("/{id:guid}/convert", async (Guid id, ConvertRequestRequest r, PurchaseRequestService s, CancellationToken ct) =>
            new { PurchaseOrderId = await s.ConvertAsync(id, r, ct) }).RequirePermission(PurchasingPermissions.OrdersManage);
    }

    private static async Task<List<PurchaseRequestDto>> ToDtosAsync(List<PurchaseRequest> list, IInventoryQueries inv, CancellationToken ct)
    {
        var items = await inv.GetItemsAsync(list.SelectMany(x => x.Lines).Select(l => l.ItemId).Distinct(), ct);
        return list.Select(x => new PurchaseRequestDto(x.Id, x.PrNo, x.RequestDate, x.RequiredDate, x.WarehouseId, x.Status.ToString(), x.EstimatedTotal,
            x.Notes, x.RequestedBy, x.PurchaseOrderId,
            x.Lines.OrderBy(l => l.LineNo).Select(l =>
            {
                var item = items.GetValueOrDefault(l.ItemId);
                return new RequestLineDto(l.LineNo, l.ItemId, item?.Sku ?? "", item?.NameEn ?? "", item?.NameAr ?? "", l.Description, l.Quantity,
                    l.EstimatedPrice, l.UomId, Money.Round(l.Quantity * l.EstimatedPrice));
            }).ToList())).ToList();
    }
}
