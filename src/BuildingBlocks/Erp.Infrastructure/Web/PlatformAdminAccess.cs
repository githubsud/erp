using System.Security.Claims;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Infrastructure.Web;

/// <summary>Whether a platform administrator may still use the back-office (implemented by Core, which owns the table).</summary>
public interface IPlatformAdminStatus
{
    Task<bool> IsActiveAsync(Guid adminId, CancellationToken ct);
}

/// <summary>Platform (vendor) administrators sign in to the back-office with a token carrying <see cref="Claim"/> = 1 and no tenant.</summary>
public static class PlatformAdminAccess
{
    public const string Claim = "pa";

    public static Guid AdminId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out var id) && http.User.FindFirstValue(Claim) == "1" ? id : throw new ForbiddenException();

    /// <summary>Only platform administrators may call the group's endpoints; a deactivated one is refused even with a token still valid.</summary>
    public static RouteGroupBuilder RequirePlatformAdmin(this RouteGroupBuilder group) =>
        group.RequireAuthorization().AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var id = AdminId(http);
            if (!await http.RequestServices.GetRequiredService<IPlatformAdminStatus>().IsActiveAsync(id, http.RequestAborted))
                throw new ForbiddenException();
            return await next(context);
        });
}
