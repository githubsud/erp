using System.Text.Json;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Commerce.Domain;
using Erp.Modules.Commerce.Storefront;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Commerce.Admin;

/// <summary>
/// An online order for the staff queue. Stage: New (to deliver), ToCollect (delivered and invoiced, cash still to receive), Done or
/// Cancelled. Delivery, invoice and receipt are the usual Sales documents of <see cref="SalesOrderId"/>.
/// </summary>
public sealed record OnlineOrderDto(Guid Id, string OrderNo, DateTimeOffset PlacedAt, string CustomerName, string Phone, string Address, string? AddressNotes,
    decimal? Latitude, decimal? Longitude, decimal Subtotal, decimal DeliveryFee, decimal Total, string PaymentMethod, Guid SalesOrderId, string SoNo,
    Guid CustomerId, string Stage, bool FullyDelivered, bool FullyInvoiced, decimal Outstanding, IReadOnlyList<OrderLineDto> Lines);

internal static class OnlineOrdersAdmin
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/commerce").WithTags("Commerce").RequireAuthorization();

        // open: new and to collect (default); done; all.
        g.MapGet("/orders", async (ErpDbContext db, IOnlineOrderIntake orders, ITenantContext tenant, CancellationToken ct, string stage = "open") =>
        {
            var checkouts = await db.Set<Checkout>().AsNoTracking().Where(c => c.CompanyId == tenant.RequireCompanyId())
                .OrderByDescending(c => c.CreatedAt).Take(500).ToListAsync(ct);
            var statuses = await orders.GetStatusAsync(checkouts.Select(c => c.SalesOrderId), ct);
            var list = checkouts.Select(c => ToDto(c, statuses.GetValueOrDefault(c.SalesOrderId))).ToList();
            return stage switch
            {
                "done" => list.Where(o => o.Stage is "Done" or "Cancelled").ToList(),
                "all" => list,
                _ => list.Where(o => o.Stage is "New" or "ToCollect").ToList()
            };
        }).RequirePermission(CommercePermissions.OrderView);
    }

    private static OnlineOrderDto ToDto(Checkout c, OnlineOrderStatus? s)
    {
        var stage = c.Status == CheckoutStatus.Cancelled || s is null || s.Status is "Cancelled" ? "Cancelled"
            : !s.FullyDelivered || !s.FullyInvoiced ? "New"
            : s.Outstanding > 0 ? "ToCollect"
            : "Done";
        var lines = JsonSerializer.Deserialize<List<OrderLineDto>>(c.PricedSnapshot, JsonSerializerOptions.Web) ?? [];
        return new OnlineOrderDto(c.Id, c.OrderNo, c.CreatedAt, c.CustomerName, c.Phone, c.Address, c.AddressNotes, c.Latitude, c.Longitude, c.Subtotal,
            c.DeliveryFee, c.Total, c.PaymentMethod.ToString(), c.SalesOrderId, s?.SoNo ?? "", s?.CustomerId ?? Guid.Empty, stage, s?.FullyDelivered ?? false,
            s?.FullyInvoiced ?? false, s?.Outstanding ?? 0m, lines);
    }
}
