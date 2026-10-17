using System.Security.Claims;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Http;

namespace Erp.Infrastructure.Tenancy;

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public Guid? CompanyId { get; private set; }
    public Guid? UserId { get; private set; }

    public Guid ActingUserId => UserId ?? SystemUser.Id;

    public Guid RequireTenantId() =>
        TenantId ?? throw new ForbiddenException("No tenant is associated with this request.");

    public Guid RequireCompanyId() =>
        CompanyId ?? throw new ForbiddenException("No company is associated with this request.");

    public void Set(Guid tenantId, Guid? companyId, Guid? userId)
    {
        TenantId = tenantId;
        CompanyId = companyId;
        UserId = userId;
    }
}

public static class ErpClaims
{
    public const string Tenant = "tid";
    public const string Company = "cid";
    public const string Language = "lang";
}

/// <summary>Binds the tenant from the validated JWT. Runs after authentication, before authorization.</summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, ITenantContext tenant)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true
            && Guid.TryParse(user.FindFirstValue(ErpClaims.Tenant), out var tenantId)
            && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var userId))
        {
            Guid? companyId = Guid.TryParse(user.FindFirstValue(ErpClaims.Company), out var c) ? c : null;
            tenant.Set(tenantId, companyId, userId);
        }

        return next(context);
    }
}
