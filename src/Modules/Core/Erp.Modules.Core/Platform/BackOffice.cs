using Erp.Infrastructure.Web;
using System.Security.Claims;
using System.Security.Cryptography;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Core.Auth;
using Erp.Modules.Core.Domain;
using Erp.Modules.Core.Endpoints;
using Erp.Modules.Core.Provisioning;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Erp.Modules.Core.Platform;

public sealed record AdminLoginRequest(string Email, string Password);

public sealed record AdminLoginResponse(string AccessToken, DateTimeOffset ExpiresAt, Guid AdminId, string Email, string DisplayName);

public sealed record CreateAdminRequest(string Email, string DisplayName, string Password);

public sealed record AdminDto(Guid Id, string Email, string DisplayName, bool IsActive, DateTimeOffset? LastLoginAt);

public sealed record CreateCustomerRequest(string Code, string? NameEn, string? NameAr, string? CrNo, string AdminEmail, string AdminPassword,
    string? AdminNameEn, string? AdminNameAr, string PlanCode, BillingCycle BillingCycle = BillingCycle.Monthly, decimal? Price = null,
    int TrialDays = 0, string? BusinessType = null, string? ContactName = null, string? ContactPhone = null, string? ContactEmail = null,
    string? Area = null, string? Notes = null, string PreferredLanguage = "ar");

public sealed record UpdateCustomerRequest(string? NameEn, string? NameAr, string? BusinessType, string? ContactName, string? ContactPhone,
    string? ContactEmail, string? Area, string? Notes);

public sealed record SubscriptionRequest(Guid PlanId, IReadOnlyList<string>? Modules, BillingCycle BillingCycle, decimal Price, bool AutoSuspend,
    int GraceDays, int? MaxUsers, int? MaxEmployees, DateOnly? NextInvoiceOn, DateOnly? TrialEndsOn, string? Notes);

public sealed record ManualInvoiceRequest(string Description, decimal Amount, DateOnly? IssueDate, DateOnly? DueDate);

public sealed record PaymentRequest(decimal Amount, DateOnly PaidOn, PaymentMethod Method, string? Reference, Guid? InvoiceId, string? Notes);

public sealed record PlanRequest(string Code, string NameEn, string NameAr, decimal MonthlyPrice, decimal AnnualPrice, IReadOnlyList<string> Modules,
    int? MaxUsers, int? MaxEmployees, bool IsActive = true, int SupportResponseHours = 24);

public sealed record PlanDto(Guid Id, string Code, string NameEn, string NameAr, decimal MonthlyPrice, decimal AnnualPrice, IReadOnlyList<string> Modules,
    int? MaxUsers, int? MaxEmployees, bool IsActive, int Customers, int SupportResponseHours);

public sealed record CustomerRow(Guid Id, string Code, string NameEn, string NameAr, string TenantStatus, string? BusinessType, string? ContactName,
    string? ContactPhone, string? Area, string? PlanCode, string? SubscriptionStatus, string? BillingCycle, decimal Price, decimal MonthlyValue,
    decimal Balance, decimal Overdue, DateOnly? OldestOverdueDue, DateOnly? NextInvoiceOn, DateOnly? TrialEndsOn, DateTimeOffset CreatedAt);

public sealed record InvoiceDto(Guid Id, Guid TenantId, string? CustomerCode, string? CustomerNameEn, string? CustomerNameAr, string InvoiceNo,
    DateOnly PeriodStart, DateOnly PeriodEnd, string Description, decimal Amount, decimal PaidAmount, decimal Balance, DateOnly IssueDate,
    DateOnly DueDate, string Status, bool Overdue);

public sealed record PaymentDto(Guid Id, Guid? InvoiceId, string? InvoiceNo, DateOnly PaidOn, decimal Amount, string Method, string? Reference,
    string? RecordedByName, string? Notes);

public sealed record SubscriptionDto(Guid Id, Guid PlanId, string PlanCode, IReadOnlyList<string> Modules, string BillingCycle, decimal Price, string Status,
    DateOnly StartDate, DateOnly? TrialEndsOn, DateOnly NextInvoiceOn, bool AutoSuspend, int GraceDays, int? MaxUsers, int? MaxEmployees, string? Notes);

public sealed record CustomerUsage(int Users, int ActiveUsers, DateTimeOffset? LastLoginAt, string? OwnerEmail);

public sealed record CustomerDetail(CustomerRow Customer, string? Notes, SubscriptionDto? Subscription, IReadOnlyList<InvoiceDto> Invoices,
    IReadOnlyList<PaymentDto> Payments, CustomerUsage Usage, IReadOnlyList<ModuleDto> Modules);

public sealed record BackOfficeSummary(int Customers, int Active, int Trial, int PastDue, int Suspended, int Closed, decimal MonthlyRecurring,
    decimal OpenBalance, decimal Overdue, decimal CollectedThisMonth, int InvoicesDueNext7Days, IReadOnlyList<CountByType> ByBusinessType);

public sealed record CountByType(string BusinessType, int Count);

public sealed record ResetPasswordResponse(string Login, string TemporaryPassword);

/// <summary>Platform administrators: sign-in (a token without a tenant, carrying the platform-admin claim) and the admin list.</summary>
/// <summary>Checked on every back-office call, cached for half a minute.</summary>
internal sealed class PlatformAdminStatus(ErpDbContext db, IMemoryCache cache) : IPlatformAdminStatus
{
    public async Task<bool> IsActiveAsync(Guid adminId, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"platform-admin:{adminId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await db.Set<PlatformAdmin>().AsNoTracking().AnyAsync(a => a.Id == adminId && a.IsActive, ct);
        });
}

internal sealed class PlatformAuthService(ErpDbContext db, JwtOptions jwt, IClock clock, IPasswordHasher<PlatformAdmin> hasher)
{
    public const string Claim = PlatformAdminAccess.Claim;
    private const int MaxFailures = 5;

    public async Task<AdminLoginResponse> LoginAsync(AdminLoginRequest r, CancellationToken ct)
    {
        var email = (r.Email ?? "").Trim().ToLowerInvariant();
        var admin = await db.Set<PlatformAdmin>().SingleOrDefaultAsync(a => a.Email == email, ct);
        var invalid = new BusinessRuleException("auth.invalid_credentials", "Email or password is incorrect.", "البريد الإلكتروني أو كلمة المرور غير صحيحة.");
        if (admin is null || !admin.IsActive)
        {
            // Same work as a real check, so the answer time does not tell which emails are administrators.
            hasher.HashPassword(new PlatformAdmin { Email = email, DisplayName = "" }, r.Password ?? "");
            throw invalid;
        }
        if (admin.LockoutUntil > clock.UtcNow)
            throw new BusinessRuleException("auth.locked_out", "Too many failed attempts. Try again later.", "محاولات فاشلة كثيرة. حاول مرة أخرى لاحقاً.");
        if (hasher.VerifyHashedPassword(admin, admin.PasswordHash, r.Password ?? "") == PasswordVerificationResult.Failed)
        {
            admin.FailedLoginCount++;
            if (admin.FailedLoginCount >= MaxFailures)
            {
                admin.LockoutUntil = clock.UtcNow.AddMinutes(15);
                admin.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync(ct);
            throw invalid;
        }
        admin.FailedLoginCount = 0;
        admin.LastLoginAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        var expires = clock.UtcNow.AddMinutes(jwt.AccessTokenMinutes * 4);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer, Audience = jwt.Audience, Expires = expires.UtcDateTime, NotBefore = clock.UtcNow.UtcDateTime,
            SigningCredentials = new SigningCredentials(jwt.CreateKey(), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, admin.Id.ToString()), new Claim(JwtRegisteredClaimNames.Email, admin.Email),
                new Claim(Claim, "1")])
        });
        return new AdminLoginResponse(token, expires, admin.Id, admin.Email, admin.DisplayName);
    }

    public async Task<PlatformAdmin> CreateAsync(CreateAdminRequest r, CancellationToken ct)
    {
        var email = Guard.Required(r.Email, "email").Trim().ToLowerInvariant();
        if ((r.Password ?? "").Length < TenantProvisioningService.MinPasswordLength)
            throw new BusinessRuleException("auth.weak_password", $"Password must be at least {TenantProvisioningService.MinPasswordLength} characters.",
                $"يجب ألا تقل كلمة المرور عن {TenantProvisioningService.MinPasswordLength} أحرف.");
        if (await db.Set<PlatformAdmin>().AnyAsync(a => a.Email == email, ct))
            throw new ConflictException("platform.admin_exists", "This administrator already exists.", "هذا المسؤول موجود مسبقاً.");
        var admin = new PlatformAdmin { Email = email, DisplayName = Guard.Required(r.DisplayName, "displayName").Trim(), CreatedAt = clock.UtcNow };
        admin.PasswordHash = hasher.HashPassword(admin, r.Password!);
        db.Add(admin);
        await db.SaveChangesAsync(ct);
        return admin;
    }

    public static Guid AdminId(HttpContext http) => PlatformAdminAccess.AdminId(http);
}

/// <summary>
/// The vendor's back-office over all customers: subscriptions and the modules they license, invoices and payments, suspension.
/// Platform tables are read directly; anything inside a customer's own data runs in a scope bound to that tenant.
/// </summary>
internal sealed class BackOfficeService(ErpDbContext db, IClock clock, IServiceScopeFactory scopes, IMemoryCache cache)
{
    public const int PaymentTermsDays = 7;

    public async Task<BackOfficeSummary> SummaryAsync(CancellationToken ct)
    {
        var rows = await CustomersAsync(null, null, ct);
        var today = clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var collected = await db.Set<SubscriptionPayment>().AsNoTracking().Where(p => p.PaidOn >= monthStart).SumAsync(p => (decimal?)p.Amount, ct) ?? 0;
        var dueSoon = await db.Set<SubscriptionInvoice>().AsNoTracking()
            .CountAsync(i => i.Status == SubscriptionInvoiceStatus.Open && i.DueDate >= today && i.DueDate <= today.AddDays(7), ct);
        var live = rows.Where(r => r.TenantStatus != nameof(TenantStatus.Closed)).ToList();
        return new BackOfficeSummary(rows.Count, live.Count(r => r.SubscriptionStatus == nameof(SubscriptionStatus.Active)),
            live.Count(r => r.SubscriptionStatus == nameof(SubscriptionStatus.Trial)), live.Count(r => r.SubscriptionStatus == nameof(SubscriptionStatus.PastDue)),
            live.Count(r => r.SubscriptionStatus == nameof(SubscriptionStatus.Suspended) || r.TenantStatus == nameof(TenantStatus.Suspended)),
            rows.Count(r => r.TenantStatus == nameof(TenantStatus.Closed)),
            live.Where(r => r.SubscriptionStatus is nameof(SubscriptionStatus.Active) or nameof(SubscriptionStatus.PastDue)).Sum(r => r.MonthlyValue),
            rows.Sum(r => r.Balance), rows.Sum(r => r.Overdue), collected, dueSoon,
            live.GroupBy(r => r.BusinessType ?? "other").Select(g => new CountByType(g.Key, g.Count())).OrderByDescending(x => x.Count).ToList());
    }

    public async Task<List<CustomerRow>> CustomersAsync(string? search, string? status, CancellationToken ct)
    {
        var today = clock.Today;
        var q = from t in db.Set<Tenant>().AsNoTracking()
                join s in db.Set<Subscription>() on t.Id equals s.TenantId into ss
                from s in ss.DefaultIfEmpty()
                join p in db.Set<Plan>() on s.PlanId equals p.Id into ps
                from p in ps.DefaultIfEmpty()
                select new { t, s, PlanCode = p == null ? null : p.Code };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(x => EF.Functions.ILike(x.t.Code, $"%{term}%") || EF.Functions.ILike(x.t.NameEn, $"%{term}%") || x.t.NameAr.Contains(term)
                             || (x.t.ContactPhone != null && x.t.ContactPhone.Contains(term)));
        }
        var list = await q.OrderBy(x => x.t.Code).ToListAsync(ct);
        var open = (await db.Set<SubscriptionInvoice>().AsNoTracking().Where(i => i.Status == SubscriptionInvoiceStatus.Open).ToListAsync(ct))
            .GroupBy(i => i.TenantId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = list.Select(x =>
        {
            var invoices = open.GetValueOrDefault(x.t.Id) ?? [];
            var overdue = invoices.Where(i => i.DueDate < today).ToList();
            var monthly = x.s is null ? 0 : x.s.BillingCycle == BillingCycle.Annual ? Math.Round(x.s.Price / 12, 2) : x.s.Price;
            return new CustomerRow(x.t.Id, x.t.Code, x.t.NameEn, x.t.NameAr, x.t.Status.ToString(), x.t.BusinessType, x.t.ContactName, x.t.ContactPhone,
                x.t.Area, x.PlanCode, x.s?.Status.ToString(), x.s?.BillingCycle.ToString(), x.s?.Price ?? 0, monthly, invoices.Sum(i => i.Balance),
                overdue.Sum(i => i.Balance), overdue.Count == 0 ? null : overdue.Min(i => i.DueDate), x.s?.NextInvoiceOn, x.s?.TrialEndsOn, x.t.CreatedAt);
        }).ToList();
        if (!string.IsNullOrWhiteSpace(status))
            rows = rows.Where(r => string.Equals(r.SubscriptionStatus, status, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(r.TenantStatus, status, StringComparison.OrdinalIgnoreCase)
                                   || (status == "overdue" && r.Overdue > 0)).ToList();
        return rows;
    }

    public async Task<CustomerDetail> DetailAsync(Guid tenantId, CancellationToken ct)
    {
        var row = (await CustomersAsync(null, null, ct)).SingleOrDefault(r => r.Id == tenantId) ?? throw new NotFoundException("Customer", tenantId);
        var tenant = await db.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
        var s = await db.Set<Subscription>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var planCode = s is null ? null : await db.Set<Plan>().Where(p => p.Id == s.PlanId).Select(p => p.Code).SingleAsync(ct);
        var invoices = await InvoicesAsync(tenantId, null, ct);
        var payments = await (from p in db.Set<SubscriptionPayment>().AsNoTracking()
                              join i in db.Set<SubscriptionInvoice>() on p.InvoiceId equals i.Id into ii
                              from i in ii.DefaultIfEmpty()
                              join a in db.Set<PlatformAdmin>() on p.RecordedBy equals a.Id into aa
                              from a in aa.DefaultIfEmpty()
                              where p.TenantId == tenantId
                              orderby p.PaidOn descending, p.CreatedAt descending
                              select new PaymentDto(p.Id, p.InvoiceId, i == null ? null : i.InvoiceNo, p.PaidOn, p.Amount, p.Method.ToString(), p.Reference,
                                  a == null ? null : a.DisplayName, p.Notes)).ToListAsync(ct);
        var usage = await InTenantAsync(tenantId, async (sdb, token) =>
        {
            var users = await sdb.Set<AppUser>().AsNoTracking().ToListAsync(token);
            var ownerEmail = await (from u in sdb.Set<AppUser>()
                                    join ur in sdb.Set<UserRole>() on u.Id equals ur.UserId
                                    join r in sdb.Set<Role>() on ur.RoleId equals r.Id
                                    where r.Code == DefaultRoles.Owner
                                    orderby u.CreatedAt
                                    select u.Email).FirstOrDefaultAsync(token);
            return new CustomerUsage(users.Count, users.Count(u => u.IsActive), users.Max(u => u.LastLoginAt), ownerEmail);
        }, ct);
        var licensed = ModuleCodes.Parse(s?.Modules ?? string.Join(',', ModuleCodes.Optional));
        var modules = PlatformEndpoints.Names.Select(n => new ModuleDto(n.Code, n.En, n.Ar, licensed.Contains(n.Code),
            ModuleCodes.Requires.GetValueOrDefault(n.Code) ?? [], licensed.Contains(n.Code))).ToList();
        return new CustomerDetail(row, tenant.Notes, s is null ? null : ToDto(s, planCode!), invoices, payments, usage, modules);
    }

    public async Task<Guid> CreateCustomerAsync(CreateCustomerRequest r, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
        var result = await provisioning.ProvisionAsync(new ProvisionTenantRequest(r.Code, r.NameEn, r.NameAr, r.CrNo, null, r.AdminEmail, r.AdminPassword,
            r.AdminNameEn, r.AdminNameAr, PreferredLanguage: r.PreferredLanguage, PlanCode: r.PlanCode, BillingCycle: r.BillingCycle, Price: r.Price,
            TrialDays: r.TrialDays, BusinessType: r.BusinessType, ContactName: r.ContactName, ContactPhone: r.ContactPhone,
            ContactEmail: r.ContactEmail, Area: r.Area, Notes: r.Notes), ct);
        await RunBillingAsync(result.TenantId, ct);
        return result.TenantId;
    }

    public async Task UpdateCustomerAsync(Guid id, UpdateCustomerRequest r, CancellationToken ct)
    {
        var t = await TenantAsync(id, ct);
        var (en, ar) = Bilingual.Require(r.NameEn ?? t.NameEn, r.NameAr ?? t.NameAr);
        t.NameEn = en;
        t.NameAr = ar;
        t.BusinessType = Clean(r.BusinessType);
        t.ContactName = Clean(r.ContactName);
        t.ContactPhone = Clean(r.ContactPhone);
        t.ContactEmail = Clean(r.ContactEmail)?.ToLowerInvariant();
        t.Area = Clean(r.Area);
        t.Notes = Clean(r.Notes);
        t.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Plan, licensed modules (the plan's unless a custom list is given), price and billing rules.</summary>
    public async Task SaveSubscriptionAsync(Guid tenantId, SubscriptionRequest r, CancellationToken ct)
    {
        await TenantAsync(tenantId, ct);
        var plan = await db.Set<Plan>().SingleOrDefaultAsync(p => p.Id == r.PlanId, ct) ?? throw new NotFoundException("Plan", r.PlanId);
        var modules = r.Modules is null ? ModuleCodes.Parse(plan.Modules) : ModuleCodes.Parse(string.Join(',', r.Modules));
        foreach (var m in modules.ToList())
            foreach (var required in ModuleCodes.Requires.GetValueOrDefault(m) ?? [])
                if (!modules.Contains(required))
                    throw new BusinessRuleException("platform.module_required", $"{m} needs {required}.", $"الوحدة {m} تحتاج إلى {required}.");
        if (r.Price < 0 || r.GraceDays is < 0 or > 180)
            throw new BusinessRuleException("platform.subscription_values", "Check the price and grace days.", "تحقق من السعر وأيام السماح.");
        var s = await db.Set<Subscription>().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct)
                ?? (Subscription)db.Add(new Subscription
                {
                    TenantId = tenantId, Modules = "", StartDate = clock.Today, NextInvoiceOn = r.NextInvoiceOn ?? clock.Today, CreatedAt = clock.UtcNow,
                    Status = SubscriptionStatus.Active
                }).Entity;
        s.PlanId = plan.Id;
        s.Modules = string.Join(',', ModuleCodes.Optional.Where(modules.Contains));
        s.BillingCycle = r.BillingCycle;
        s.Price = Money.Round(r.Price);
        s.AutoSuspend = r.AutoSuspend;
        s.GraceDays = r.GraceDays;
        s.MaxUsers = r.MaxUsers;
        s.MaxEmployees = r.MaxEmployees;
        if (r.NextInvoiceOn is { } next) s.NextInvoiceOn = next;
        s.TrialEndsOn = r.TrialEndsOn;
        s.Notes = Clean(r.Notes);
        if (s.Status == SubscriptionStatus.Cancelled) s.Status = SubscriptionStatus.Active;
        s.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        ModuleAccess.InvalidateTenant(cache, tenantId);
        await RunBillingAsync(tenantId, ct);
    }

    /// <summary>Manual suspension (whatever the billing says), reactivation, or closing the account (no sign-in at all).</summary>
    public async Task SetStatusAsync(Guid tenantId, TenantStatus status, CancellationToken ct)
    {
        var t = await TenantAsync(tenantId, ct);
        t.Status = status;
        t.UpdatedAt = clock.UtcNow;
        var s = await db.Set<Subscription>().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (s is not null && status == TenantStatus.Closed) s.Status = SubscriptionStatus.Cancelled;
        if (s is not null && status == TenantStatus.Active && s.Status == SubscriptionStatus.Cancelled) s.Status = SubscriptionStatus.Active;
        await db.SaveChangesAsync(ct);
        ModuleAccess.InvalidateTenant(cache, tenantId);
    }

    public Task<SubscriptionInvoice> AddInvoiceAsync(Guid tenantId, ManualInvoiceRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            await TenantAsync(tenantId, token);
            if (r.Amount <= 0) throw new BusinessRuleException("platform.invoice_amount", "Enter an amount.", "أدخل المبلغ.");
            var issue = r.IssueDate ?? clock.Today;
            var invoice = await NewInvoiceAsync(tenantId, issue, issue, Guard.Required(r.Description, "description").Trim(), Money.Round(r.Amount),
                r.DueDate ?? issue.AddDays(PaymentTermsDays), token);
            await db.SaveChangesAsync(token);
            await UpdateStatusAsync(tenantId, token);
            return invoice;
        }, ct);

    public async Task VoidInvoiceAsync(Guid invoiceId, CancellationToken ct)
    {
        var invoice = await db.Set<SubscriptionInvoice>().SingleOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice", invoiceId);
        if (invoice.PaidAmount > 0)
            throw new BusinessRuleException("platform.invoice_paid", "A paid or part-paid invoice cannot be voided.", "لا يمكن إلغاء فاتورة مدفوعة أو مدفوعة جزئياً.");
        invoice.Status = SubscriptionInvoiceStatus.Void;
        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await UpdateStatusAsync(invoice.TenantId, ct);
    }

    /// <summary>Applies the payment to the chosen invoice, or to the oldest open invoices first; then the account status is recalculated.</summary>
    public Task<SubscriptionPayment> RecordPaymentAsync(Guid tenantId, Guid adminId, PaymentRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            await TenantAsync(tenantId, token);
            var amount = Money.Round(r.Amount);
            if (amount <= 0) throw new BusinessRuleException("platform.payment_amount", "Enter the amount received.", "أدخل المبلغ المستلم.");
            var open = await db.Set<SubscriptionInvoice>().Where(i => i.TenantId == tenantId && i.Status == SubscriptionInvoiceStatus.Open
                                                                    && (r.InvoiceId == null || i.Id == r.InvoiceId))
                .OrderBy(i => i.DueDate).ThenBy(i => i.InvoiceNo).ToListAsync(token);
            var balance = open.Sum(i => i.Amount - i.PaidAmount);
            if (open.Count == 0 || amount > balance)
                throw new BusinessRuleException("platform.payment_exceeds", $"Only {balance:N2} is open; issue an invoice for advance payments.",
                    $"المستحق {balance:N2} فقط؛ أصدر فاتورة للدفعات المقدمة.");
            var payment = new SubscriptionPayment
            {
                TenantId = tenantId, InvoiceId = open.Count == 1 || r.InvoiceId is not null ? open[0].Id : null, PaidOn = r.PaidOn, Amount = amount,
                Method = r.Method, Reference = Clean(r.Reference), RecordedBy = adminId, Notes = Clean(r.Notes), CreatedAt = clock.UtcNow
            };
            db.Add(payment);
            var left = amount;
            foreach (var invoice in open)
            {
                if (left == 0) break;
                var take = Math.Min(left, invoice.Amount - invoice.PaidAmount);
                invoice.PaidAmount += take;
                left -= take;
                if (invoice.PaidAmount == invoice.Amount) invoice.Status = SubscriptionInvoiceStatus.Paid;
                invoice.UpdatedAt = clock.UtcNow;
            }
            await db.SaveChangesAsync(token);
            await UpdateStatusAsync(tenantId, token);
            return payment;
        }, ct);

    /// <summary>A new temporary password for the customer's owner account (they must change it at next sign-in).</summary>
    public Task<ResetPasswordResponse> ResetOwnerPasswordAsync(Guid tenantId, CancellationToken ct) =>
        InTenantAsync(tenantId, async (sdb, token) =>
        {
            var hasher = new PasswordHasher<AppUser>();
            var owner = await (from u in sdb.Set<AppUser>()
                               join ur in sdb.Set<UserRole>() on u.Id equals ur.UserId
                               join r in sdb.Set<Role>() on ur.RoleId equals r.Id
                               where r.Code == DefaultRoles.Owner
                               orderby u.CreatedAt
                               select u).FirstOrDefaultAsync(token) ?? throw new NotFoundException("Owner user", tenantId);
            var password = TemporaryPassword();
            owner.PasswordHash = hasher.HashPassword(owner, password);
            owner.MustChangePassword = true;
            owner.FailedLoginCount = 0;
            owner.LockoutUntil = null;
            owner.IsActive = true;
            await sdb.SaveChangesAsync(token);
            return new ResetPasswordResponse(owner.Email ?? owner.Username ?? "", password);
        }, ct);

    public async Task<List<InvoiceDto>> InvoicesAsync(Guid? tenantId, string? status, CancellationToken ct)
    {
        var today = clock.Today;
        var q = from i in db.Set<SubscriptionInvoice>().AsNoTracking()
                join t in db.Set<Tenant>() on i.TenantId equals t.Id
                select new { i, t.Code, t.NameEn, t.NameAr };
        if (tenantId is not null) q = q.Where(x => x.i.TenantId == tenantId);
        if (status == "open") q = q.Where(x => x.i.Status == SubscriptionInvoiceStatus.Open);
        if (status == "overdue") q = q.Where(x => x.i.Status == SubscriptionInvoiceStatus.Open && x.i.DueDate < today);
        if (status == "paid") q = q.Where(x => x.i.Status == SubscriptionInvoiceStatus.Paid);
        var list = await q.OrderByDescending(x => x.i.IssueDate).ThenByDescending(x => x.i.InvoiceNo).Take(500).ToListAsync(ct);
        return list.Select(x => new InvoiceDto(x.i.Id, x.i.TenantId, x.Code, x.NameEn, x.NameAr, x.i.InvoiceNo, x.i.PeriodStart, x.i.PeriodEnd,
            x.i.Description, x.i.Amount, x.i.PaidAmount, x.i.Balance, x.i.IssueDate, x.i.DueDate, x.i.Status.ToString(),
            x.i.Status == SubscriptionInvoiceStatus.Open && x.i.DueDate < today)).ToList();
    }

    /// <summary>Issues every invoice that has fallen due (catching up missed periods) and recalculates statuses. Daily, or on demand.</summary>
    public async Task<int> RunBillingAsync(Guid? tenantId, CancellationToken ct)
    {
        var today = clock.Today;
        var subscriptions = await db.Set<Subscription>().Where(s => (tenantId == null || s.TenantId == tenantId) && s.Status != SubscriptionStatus.Cancelled)
            .ToListAsync(ct);
        var issued = 0;
        foreach (var s in subscriptions)
        {
            await db.InTransactionAsync(async token =>
            {
                var plan = await db.Set<Plan>().AsNoTracking().SingleAsync(p => p.Id == s.PlanId, token);
                while (s.NextInvoiceOn <= today)
                {
                    var end = BillingRules.PeriodEnd(s.NextInvoiceOn, s.BillingCycle);
                    if (s.Price > 0 && !await db.Set<SubscriptionInvoice>().AnyAsync(i => i.TenantId == s.TenantId && i.PeriodStart == s.NextInvoiceOn
                                                                                          && i.PeriodEnd == end, token))
                    {
                        await NewInvoiceAsync(s.TenantId, s.NextInvoiceOn, end,
                            $"{plan.NameEn} subscription {s.NextInvoiceOn:dd/MM/yyyy}–{end:dd/MM/yyyy} / اشتراك {plan.NameAr}", s.Price,
                            s.NextInvoiceOn.AddDays(PaymentTermsDays), token);
                        issued++;
                    }
                    s.NextInvoiceOn = end.AddDays(1);
                }
                s.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(token);
                await UpdateStatusAsync(s.TenantId, token);
                return 0;
            }, ct);
        }
        return issued;
    }

    public async Task<List<PlanDto>> PlansAsync(CancellationToken ct)
    {
        var counts = await db.Set<Subscription>().AsNoTracking().Where(s => s.Status != SubscriptionStatus.Cancelled).GroupBy(s => s.PlanId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return (await db.Set<Plan>().AsNoTracking().OrderBy(p => p.MonthlyPrice).ToListAsync(ct))
            .Select(p => new PlanDto(p.Id, p.Code, p.NameEn, p.NameAr, p.MonthlyPrice, p.AnnualPrice, ModuleCodes.Parse(p.Modules).ToList(), p.MaxUsers,
                p.MaxEmployees, p.IsActive, counts.GetValueOrDefault(p.Id), p.SupportResponseHours)).ToList();
    }

    public async Task<Guid> SavePlanAsync(Guid? id, PlanRequest r, CancellationToken ct)
    {
        if (r.MonthlyPrice < 0 || r.AnnualPrice < 0) throw new BusinessRuleException("platform.plan_price", "Prices cannot be negative.", "لا يمكن أن تكون الأسعار سالبة.");
        if (r.SupportResponseHours is < 1 or > 720)
            throw new BusinessRuleException("platform.plan_response", "Support response time must be 1 to 720 hours.", "يجب أن يكون وقت الرد على الدعم من 1 إلى 720 ساعة.");
        var modules = string.Join(',', ModuleCodes.Optional.Where(ModuleCodes.Parse(string.Join(',', r.Modules)).Contains));
        var plan = id is null
            ? (Plan)db.Add(new Plan { Code = Guard.Required(r.Code, "code").Trim().ToUpperInvariant(), NameEn = r.NameEn, NameAr = r.NameAr, Modules = modules,
                CreatedAt = clock.UtcNow }).Entity
            : await db.Set<Plan>().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Plan", id);
        (plan.NameEn, plan.NameAr) = Bilingual.Require(r.NameEn, r.NameAr);
        plan.MonthlyPrice = Money.Round(r.MonthlyPrice);
        plan.AnnualPrice = Money.Round(r.AnnualPrice);
        plan.Modules = modules;
        plan.MaxUsers = r.MaxUsers;
        plan.MaxEmployees = r.MaxEmployees;
        plan.IsActive = r.IsActive;
        plan.SupportResponseHours = r.SupportResponseHours;
        plan.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return plan.Id;
    }

    private async Task UpdateStatusAsync(Guid tenantId, CancellationToken ct)
    {
        var s = await db.Set<Subscription>().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (s is null) return;
        var oldest = await db.Set<SubscriptionInvoice>().Where(i => i.TenantId == tenantId && i.Status == SubscriptionInvoiceStatus.Open)
            .MinAsync(i => (DateOnly?)i.DueDate, ct);
        var status = BillingRules.StatusFor(s.Status, clock.Today, oldest, s.AutoSuspend, s.GraceDays, s.TrialEndsOn);
        if (status != s.Status)
        {
            s.Status = status;
            s.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            ModuleAccess.InvalidateTenant(cache, tenantId);
        }
    }

    private async Task<SubscriptionInvoice> NewInvoiceAsync(Guid tenantId, DateOnly start, DateOnly end, string description, decimal amount, DateOnly due,
        CancellationToken ct)
    {
        var next = await db.Database.SqlQuery<long>($"SELECT nextval('core.subscription_invoice_no') AS \"Value\"").SingleAsync(ct);
        var invoice = new SubscriptionInvoice
        {
            TenantId = tenantId, InvoiceNo = $"SUB-{next}", PeriodStart = start, PeriodEnd = end, Description = description, Amount = amount,
            IssueDate = start, DueDate = due, Status = SubscriptionInvoiceStatus.Open, CreatedAt = clock.UtcNow
        };
        db.Add(invoice);
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<ErpDbContext, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, null);
        return await work(scope.ServiceProvider.GetRequiredService<ErpDbContext>(), ct);
    }

    private async Task<Tenant> TenantAsync(Guid id, CancellationToken ct) =>
        await db.Set<Tenant>().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Customer", id);

    private static SubscriptionDto ToDto(Subscription s, string planCode) => new(s.Id, s.PlanId, planCode, ModuleCodes.Parse(s.Modules).ToList(),
        s.BillingCycle.ToString(), s.Price, s.Status.ToString(), s.StartDate, s.TrialEndsOn, s.NextInvoiceOn, s.AutoSuspend, s.GraceDays, s.MaxUsers,
        s.MaxEmployees, s.Notes);

    private static string TemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        return string.Concat(Enumerable.Range(0, 12).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>Daily after 06:00 Qatar time: issue due subscription invoices and move overdue accounts to past due / suspended.</summary>
internal sealed class BillingJob(IServiceScopeFactory scopes, IClock clock, ILogger<BillingJob> logger) : BackgroundService
{
    private DateOnly _lastRun = DateOnly.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                if (clock.UtcNow.ToOffset(TimeSpan.FromHours(3)).Hour >= 6 && clock.Today > _lastRun)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<BackOfficeService>().RunBillingAsync(null, stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<SupportService>().CloseSolvedAsync(stoppingToken);
                    _lastRun = clock.Today;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Billing job failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

internal static class BackOfficeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // First administrator (and recovery): protected by the platform API key, like tenant provisioning.
        app.MapPost("/api/platform/admins", async (CreateAdminRequest r, PlatformAuthService s, CancellationToken ct) =>
        {
            var a = await s.CreateAsync(r, ct);
            return new AdminDto(a.Id, a.Email, a.DisplayName, a.IsActive, a.LastLoginAt);
        }).WithTags("Platform").AddEndpointFilter(CoreEndpoints.RequirePlatformKeyFilter);
        app.MapPost("/api/backoffice/auth/login", (AdminLoginRequest r, PlatformAuthService s, CancellationToken ct) => s.LoginAsync(r, ct))
            .WithTags("Back-office").AllowAnonymous().RequireRateLimiting(SignInRateLimit.Policy);

        var g = app.MapGroup("/api/backoffice").WithTags("Back-office").RequirePlatformAdmin();
        g.MapGet("/summary", (BackOfficeService s, CancellationToken ct) => s.SummaryAsync(ct));
        g.MapGet("/customers", (BackOfficeService s, CancellationToken ct, string? search = null, string? status = null) => s.CustomersAsync(search, status, ct));
        g.MapGet("/customers/{id:guid}", (Guid id, BackOfficeService s, CancellationToken ct) => s.DetailAsync(id, ct));
        g.MapPost("/customers", async (CreateCustomerRequest r, BackOfficeService s, CancellationToken ct) => await s.DetailAsync(await s.CreateCustomerAsync(r, ct), ct));
        g.MapPut("/customers/{id:guid}", async (Guid id, UpdateCustomerRequest r, BackOfficeService s, CancellationToken ct) =>
        {
            await s.UpdateCustomerAsync(id, r, ct);
            return await s.DetailAsync(id, ct);
        });
        g.MapPut("/customers/{id:guid}/subscription", async (Guid id, SubscriptionRequest r, BackOfficeService s, CancellationToken ct) =>
        {
            await s.SaveSubscriptionAsync(id, r, ct);
            return await s.DetailAsync(id, ct);
        });
        foreach (var (action, status) in new[] { ("suspend", TenantStatus.Suspended), ("reactivate", TenantStatus.Active), ("close", TenantStatus.Closed) })
            g.MapPost($"/customers/{{id:guid}}/{action}", async (Guid id, BackOfficeService s, CancellationToken ct) =>
            {
                await s.SetStatusAsync(id, status, ct);
                return await s.DetailAsync(id, ct);
            });
        g.MapPost("/customers/{id:guid}/invoices", async (Guid id, ManualInvoiceRequest r, BackOfficeService s, CancellationToken ct) =>
        {
            await s.AddInvoiceAsync(id, r, ct);
            return await s.DetailAsync(id, ct);
        });
        g.MapPost("/customers/{id:guid}/payments", async (Guid id, PaymentRequest r, BackOfficeService s, HttpContext http, CancellationToken ct) =>
        {
            await s.RecordPaymentAsync(id, PlatformAuthService.AdminId(http), r, ct);
            return await s.DetailAsync(id, ct);
        });
        g.MapPost("/customers/{id:guid}/reset-owner-password", (Guid id, BackOfficeService s, CancellationToken ct) => s.ResetOwnerPasswordAsync(id, ct));
        g.MapGet("/invoices", (BackOfficeService s, CancellationToken ct, string? status = null) => s.InvoicesAsync(null, status, ct));
        g.MapPost("/invoices/{id:guid}/void", async (Guid id, BackOfficeService s, CancellationToken ct) =>
        {
            await s.VoidInvoiceAsync(id, ct);
            return Results.NoContent();
        });
        g.MapPost("/billing/run", async (BackOfficeService s, CancellationToken ct) => new { Issued = await s.RunBillingAsync(null, ct) });
        g.MapGet("/plans", (BackOfficeService s, CancellationToken ct) => s.PlansAsync(ct));
        g.MapPost("/plans", (PlanRequest r, BackOfficeService s, CancellationToken ct) => s.SavePlanAsync(null, r, ct));
        g.MapPut("/plans/{id:guid}", (Guid id, PlanRequest r, BackOfficeService s, CancellationToken ct) => s.SavePlanAsync(id, r, ct));
        g.MapGet("/modules", () => PlatformEndpoints.Names.Select(n => new { n.Code, NameEn = n.En, NameAr = n.Ar,
            Requires = ModuleCodes.Requires.GetValueOrDefault(n.Code) ?? [] }));
        g.MapGet("/admins", async (ErpDbContext db, CancellationToken ct) =>
            await db.Set<PlatformAdmin>().AsNoTracking().OrderBy(a => a.Email)
                .Select(a => new AdminDto(a.Id, a.Email, a.DisplayName, a.IsActive, a.LastLoginAt)).ToListAsync(ct));
        g.MapPost("/admins", async (CreateAdminRequest r, PlatformAuthService s, CancellationToken ct) =>
        {
            var a = await s.CreateAsync(r, ct);
            return new AdminDto(a.Id, a.Email, a.DisplayName, a.IsActive, a.LastLoginAt);
        });
    }
}
