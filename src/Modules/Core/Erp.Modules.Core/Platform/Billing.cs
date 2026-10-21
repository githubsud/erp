using Erp.Modules.Core.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Core.Platform;

// Platform (vendor) back-office: no tenant_id, no RLS, managed only by platform administrators.

public enum SubscriptionStatus { Trial, Active, PastDue, Suspended, Cancelled }

public enum BillingCycle { Monthly, Annual }

public enum SubscriptionInvoiceStatus { Open, Paid, Void }

public enum PaymentMethod { BankTransfer, Cash, Card, Cheque, Fawran, Other }

public sealed class PlatformAdmin : Entity
{
    public required string Email { get; init; }
    public required string DisplayName { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class Plan : Entity
{
    public required string Code { get; init; }
    public required string NameEn { get; set; }
    public required string NameAr { get; set; }
    public decimal MonthlyPrice { get; set; }
    public decimal AnnualPrice { get; set; }
    public required string Modules { get; set; }
    public int? MaxUsers { get; set; }
    public int? MaxEmployees { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Hours within which a support request gets its first answer.</summary>
    public int SupportResponseHours { get; set; } = 24;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class Subscription : Entity
{
    public Guid TenantId { get; init; }
    public Guid PlanId { get; set; }
    public required string Modules { get; set; }
    public BillingCycle BillingCycle { get; set; }
    public decimal Price { get; set; }
    public SubscriptionStatus Status { get; set; }
    public DateOnly StartDate { get; init; }
    public DateOnly? TrialEndsOn { get; set; }
    public DateOnly NextInvoiceOn { get; set; }
    public bool AutoSuspend { get; set; } = true;
    public int GraceDays { get; set; } = 14;
    public int? MaxUsers { get; set; }
    public int? MaxEmployees { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class SubscriptionInvoice : Entity
{
    public Guid TenantId { get; init; }
    public required string InvoiceNo { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public required string Description { get; init; }
    public decimal Amount { get; init; }
    public decimal PaidAmount { get; set; }
    public DateOnly IssueDate { get; init; }
    public DateOnly DueDate { get; init; }
    public SubscriptionInvoiceStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public decimal Balance => Status == SubscriptionInvoiceStatus.Void ? 0 : Amount - PaidAmount;
}

public sealed class SubscriptionPayment : Entity
{
    public Guid TenantId { get; init; }
    public Guid? InvoiceId { get; init; }
    public DateOnly PaidOn { get; init; }
    public decimal Amount { get; init; }
    public PaymentMethod Method { get; init; }
    public string? Reference { get; init; }
    public Guid RecordedBy { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

internal sealed class PlatformAdminConfiguration : IEntityTypeConfiguration<PlatformAdmin>
{
    public void Configure(EntityTypeBuilder<PlatformAdmin> b) => b.ToTable("platform_admin", "core");
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b) => b.ToTable("plan", "core");
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b) => b.ToTable("subscription", "core");
}

internal sealed class SubscriptionInvoiceConfiguration : IEntityTypeConfiguration<SubscriptionInvoice>
{
    public void Configure(EntityTypeBuilder<SubscriptionInvoice> b)
    {
        b.ToTable("subscription_invoice", "core");
        b.Ignore(x => x.Balance);
    }
}

internal sealed class SubscriptionPaymentConfiguration : IEntityTypeConfiguration<SubscriptionPayment>
{
    public void Configure(EntityTypeBuilder<SubscriptionPayment> b) => b.ToTable("subscription_payment", "core");
}

public static class BillingRules
{
    /// <summary>The period an invoice issued on <paramref name="start"/> covers.</summary>
    public static DateOnly PeriodEnd(DateOnly start, BillingCycle cycle) =>
        (cycle == BillingCycle.Annual ? start.AddYears(1) : start.AddMonths(1)).AddDays(-1);

    /// <summary>
    /// The billing status today from the oldest unpaid invoice: active (or trial) while nothing is overdue, past due once an invoice is
    /// late, suspended when it is more than <paramref name="graceDays"/> late and auto-suspend is on. A manual suspension by the
    /// platform is kept on the tenant, not here; a cancelled subscription stays cancelled.
    /// </summary>
    public static SubscriptionStatus StatusFor(SubscriptionStatus current, DateOnly today, DateOnly? oldestUnpaidDue, bool autoSuspend, int graceDays,
        DateOnly? trialEndsOn)
    {
        if (current == SubscriptionStatus.Cancelled) return current;
        if (oldestUnpaidDue is not { } due || due >= today)
            return trialEndsOn is { } trial && today <= trial ? SubscriptionStatus.Trial : SubscriptionStatus.Active;
        return autoSuspend && today.DayNumber - due.DayNumber > graceDays ? SubscriptionStatus.Suspended : SubscriptionStatus.PastDue;
    }
}

/// <summary>Creates the subscription of a newly provisioned customer (all modules, free, when no plan is given — e.g. demos and tests).</summary>
internal static class SubscriptionFactory
{
    public static async Task<Subscription> CreateAsync(DbContext db, Guid tenantId, string? planCode, BillingCycle cycle, decimal? price, int trialDays,
        DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        var plan = await db.Set<Plan>().SingleOrDefaultAsync(p => p.Code == (planCode ?? "ENTERPRISE"), ct)
                   ?? throw new BusinessRuleException("platform.plan", "Unknown plan.", "باقة غير معروفة.");
        var free = planCode is null;
        var subscription = new Subscription
        {
            TenantId = tenantId, PlanId = plan.Id, Modules = plan.Modules, BillingCycle = cycle,
            Price = free ? 0 : price ?? (cycle == BillingCycle.Annual ? plan.AnnualPrice : plan.MonthlyPrice),
            Status = trialDays > 0 ? SubscriptionStatus.Trial : SubscriptionStatus.Active, StartDate = today,
            TrialEndsOn = trialDays > 0 ? today.AddDays(trialDays) : null, NextInvoiceOn = trialDays > 0 ? today.AddDays(trialDays) : today,
            AutoSuspend = !free, MaxUsers = plan.MaxUsers, MaxEmployees = plan.MaxEmployees, CreatedAt = now
        };
        db.Add(subscription);
        return subscription;
    }
}
