using System.Security.Claims;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Infrastructure.Web;

/// <summary>Platform (vendor) administrators sign in to the back-office with a token carrying <see cref="Claim"/> = 1 and no tenant.</summary>
public static class PlatformAdminAccess
{
    public const string Claim = "pa";

    public static Guid AdminId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out var id) && http.User.FindFirstValue(Claim) == "1" ? id : throw new ForbiddenException();

    /// <summary>Only platform administrators may call the group's endpoints.</summary>
    public static RouteGroupBuilder RequirePlatformAdmin(this RouteGroupBuilder group) =>
        group.RequireAuthorization().AddEndpointFilter(async (context, next) =>
        {
            AdminId(context.HttpContext);
            return await next(context);
        });
}
