using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Sales.Pos;

/// <summary>A manager's approval PIN for cashiers' discounts (hashed like a password).</summary>
public sealed class PosApprover : TenantEntity
{
    public Guid UserId { get; init; }
    [Sensitive] public required string PinHash { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>A manager approved one sale of a shift, up to <see cref="MaxDiscount"/> off the item list's prices.</summary>
public sealed class PosApproval : TenantEntity
{
    public Guid ShiftId { get; init; }
    public Guid ApproverUserId { get; init; }
    public decimal MaxDiscount { get; init; }
}

internal sealed class PosApproverConfiguration : IEntityTypeConfiguration<PosApprover>
{
    public void Configure(EntityTypeBuilder<PosApprover> b) => b.ToTable("pos_approver", "sal");
}

internal sealed class PosApprovalConfiguration : IEntityTypeConfiguration<PosApproval>
{
    public void Configure(EntityTypeBuilder<PosApproval> b) => b.ToTable("pos_approval", "sal");
}

public sealed record PosPolicyDto(decimal MaxDiscountPercent, bool CanOverride);

public sealed record SetPinRequest(string? Pin);

public sealed record ApproverDto(Guid UserId, string Name);

/// <summary><see cref="Discount"/>: how much the sale takes off the item list's prices, as shown to the manager.</summary>
public sealed record ApprovalRequest(Guid ShiftId, Guid ApproverUserId, string? Pin, decimal Discount);

public sealed record ApprovalDto(Guid Id, string ApproverName);

/// <summary>
/// The till's price and discount limits. The item list's price is the price: a line sold for less than the cashier limit allows
/// (by discount or a lower price) needs the "override" permission or a manager's approval, typed with the manager's PIN on the
/// cashier's device. Approvals need a connection, so the till blocks such a discount while offline and a queued sale is not refused later.
/// </summary>
internal sealed class PosLimitService(ErpDbContext db, ITenantContext tenant, IPermissionService permissions, IUserDirectory users, IClock clock)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);
    private static readonly PasswordHasher<PosApprover> Hasher = new();

    public async Task<decimal> MaxDiscountPercentAsync(CancellationToken ct) =>
        await db.Set<SalesSettings>().AsNoTracking().Where(s => s.CompanyId == tenant.RequireCompanyId()).Select(s => s.PosMaxDiscountPercent).SingleAsync(ct);

    public async Task<bool> CanOverrideAsync(Guid userId, CancellationToken ct)
    {
        var granted = await permissions.GetPermissionsAsync(userId, ct);
        return granted.Contains(DefaultRoles.Wildcard) || granted.Contains(SalesPermissions.PosOverride);
    }

    /// <summary>What a sale takes off the item list's prices, and whether any line goes past the cashier limit.</summary>
    public static (decimal Cut, bool OverLimit) Measure(IEnumerable<PosSaleLine> lines, IReadOnlyDictionary<Guid, ItemInfo> items, decimal maxPercent)
    {
        decimal cut = 0;
        var over = false;
        foreach (var l in lines)
        {
            if (items[l.ItemId].SalesPrice is not { } list) continue;
            var full = Money.Round(l.Quantity * list);
            var floor = Money.Round(full * (100m - maxPercent) / 100m);
            if (l.Amount < floor) over = true;
            cut += Math.Max(0, full - l.Amount);
        }
        return (cut, over);
    }

    /// <summary>Lets a sale past the cashier limit through, or refuses it. Records the approving manager on the sale.</summary>
    public async Task AuthorizeAsync(PosSale sale, IReadOnlyDictionary<Guid, ItemInfo> items, Guid? approvalId, CancellationToken ct)
    {
        var max = await MaxDiscountPercentAsync(ct);
        var (cut, over) = Measure(sale.Lines, items, max);
        if (!over || await CanOverrideAsync(tenant.ActingUserId, ct)) return;
        if (approvalId is null)
            throw new BusinessRuleException("sal.pos_needs_approval", $"A discount of more than {max:0.##}% needs a manager's approval.",
                $"الخصم الذي يزيد على {max:0.##}% يحتاج موافقة المدير.");
        var approval = await db.Set<PosApproval>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == approvalId, ct)
                       ?? throw new NotFoundException("Approval", approvalId);
        if (approval.ShiftId != sale.ShiftId || await db.Set<PosSale>().AnyAsync(s => s.ApprovalId == approval.Id, ct))
            throw new BusinessRuleException("sal.pos_approval_used", "This approval is for another sale. Ask the manager again.",
                "هذه الموافقة لبيع آخر. اطلب موافقة المدير مرة أخرى.");
        if (cut > approval.MaxDiscount)
            throw new BusinessRuleException("sal.pos_approval_short", $"The manager approved {approval.MaxDiscount:N2} off; this sale takes {cut:N2} off.",
                $"وافق المدير على خصم {approval.MaxDiscount:N2}، وهذا البيع يخصم {cut:N2}.");
        sale.ApprovalId = approval.Id;
        sale.ApprovedBy = approval.ApproverUserId;
    }

    public async Task<PosPolicyDto> PolicyAsync(CancellationToken ct) =>
        new(await MaxDiscountPercentAsync(ct), await CanOverrideAsync(tenant.ActingUserId, ct));

    public async Task<bool> HasPinAsync(CancellationToken ct) => await db.Set<PosApprover>().AnyAsync(a => a.UserId == tenant.ActingUserId, ct);

    public async Task SetPinAsync(string? pin, CancellationToken ct)
    {
        CheckPin(pin);
        var userId = tenant.ActingUserId;
        var row = await db.Set<PosApprover>().SingleOrDefaultAsync(a => a.UserId == userId, ct) ?? db.Add(new PosApprover { UserId = userId, PinHash = "" }).Entity;
        row.PinHash = Hasher.HashPassword(row, pin!);
        row.FailedAttempts = 0;
        row.LockedUntil = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>4 to 6 digits, not one repeated digit nor a run like 1234 or 9876.</summary>
    internal static void CheckPin(string? pin)
    {
        if (pin is not { Length: >= 4 and <= 6 } || !pin.All(char.IsAsciiDigit))
            throw new BusinessRuleException("sal.pos_pin_format", "The PIN must be 4 to 6 digits.", "يجب أن يتكون الرمز السري من 4 إلى 6 أرقام.");
        var steps = pin.Zip(pin.Skip(1), (a, b) => b - a).Distinct().ToList();
        if (steps is [0] or [1] or [-1])
            throw new BusinessRuleException("sal.pos_pin_weak", "This PIN is too easy to guess.", "هذا الرمز السري سهل التخمين.");
    }

    /// <summary>Managers who can approve at the till: they set a PIN and still hold the override permission.</summary>
    public async Task<IReadOnlyList<ApproverDto>> ApproversAsync(CancellationToken ct)
    {
        var ids = await db.Set<PosApprover>().AsNoTracking().Select(a => a.UserId).ToListAsync(ct);
        var allowed = new List<Guid>();
        foreach (var id in ids)
            if (await CanOverrideAsync(id, ct)) allowed.Add(id);
        var names = await users.NamesAsync(allowed, ct);
        return allowed.Select(id => new ApproverDto(id, names.GetValueOrDefault(id, "?"))).OrderBy(a => a.Name).ToList();
    }

    public async Task<ApprovalDto> ApproveAsync(ApprovalRequest r, CancellationToken ct)
    {
        var shift = await (from s in db.Set<PosShift>()
                           join g in db.Set<PosRegister>() on s.RegisterId equals g.Id
                           where s.Id == r.ShiftId && g.CompanyId == tenant.RequireCompanyId()
                           select s).AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Shift", r.ShiftId);
        if (shift.Status != ShiftStatus.Open) throw new ConflictException("sal.shift_closed", "This shift is closed.", "هذه الوردية مغلقة.");
        if (r.Discount < 0) throw new BusinessRuleException("sal.discount", "The discount cannot be negative.", "لا يمكن أن يكون الخصم سالباً.");
        var approver = await db.Set<PosApprover>().SingleOrDefaultAsync(a => a.UserId == r.ApproverUserId, ct);
        if (approver is null || !await CanOverrideAsync(approver.UserId, ct))
            throw new BusinessRuleException("sal.pos_not_approver", "This person cannot approve discounts.", "هذا الشخص لا يمكنه اعتماد الخصومات.");
        if (approver.LockedUntil > clock.UtcNow)
        {
            var minutes = (int)Math.Ceiling((approver.LockedUntil.Value - clock.UtcNow).TotalMinutes);
            throw new BusinessRuleException("sal.pos_pin_locked", $"Too many wrong PINs. Try again in {minutes} minute(s).",
                $"محاولات خاطئة كثيرة. حاول مرة أخرى بعد {minutes} دقيقة.");
        }
        if (Hasher.VerifyHashedPassword(approver, approver.PinHash, r.Pin ?? "") == PasswordVerificationResult.Failed)
        {
            // Saved before refusing: guessing a 4-digit PIN must hit the lock.
            if (++approver.FailedAttempts >= MaxAttempts)
            {
                approver.FailedAttempts = 0;
                approver.LockedUntil = clock.UtcNow + LockTime;
            }
            await db.SaveChangesAsync(ct);
            throw new BusinessRuleException("sal.pos_wrong_pin", "Wrong PIN.", "الرمز السري غير صحيح.");
        }
        approver.FailedAttempts = 0;
        approver.LockedUntil = null;
        var approval = db.Add(new PosApproval { ShiftId = shift.Id, ApproverUserId = approver.UserId, MaxDiscount = Money.Round(r.Discount) }).Entity;
        await db.SaveChangesAsync(ct);
        var names = await users.NamesAsync([approver.UserId], ct);
        return new ApprovalDto(approval.Id, names.GetValueOrDefault(approver.UserId, "?"));
    }
}

internal static class PosLimitEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sales/pos").WithTags("Point of sale").RequireAuthorization();
        g.MapGet("/policy", (PosLimitService s, CancellationToken ct) => s.PolicyAsync(ct)).RequirePermission(SalesPermissions.PosSell);
        g.MapGet("/approvers", (PosLimitService s, CancellationToken ct) => s.ApproversAsync(ct)).RequirePermission(SalesPermissions.PosSell);
        g.MapPost("/approvals", (ApprovalRequest r, PosLimitService s, CancellationToken ct) => s.ApproveAsync(r, ct))
            .RequirePermission(SalesPermissions.PosSell);
        g.MapGet("/my-pin", async (PosLimitService s, CancellationToken ct) => new { hasPin = await s.HasPinAsync(ct) })
            .RequirePermission(SalesPermissions.PosOverride);
        g.MapPut("/my-pin", async (SetPinRequest r, PosLimitService s, CancellationToken ct) =>
        {
            await s.SetPinAsync(r.Pin, ct);
            return new { hasPin = true };
        }).RequirePermission(SalesPermissions.PosOverride);
    }
}
