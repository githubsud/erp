using Erp.Infrastructure.Web;
using Erp.SharedKernel.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Erp.Modules.Core.Platform;

internal static class DashboardEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Each module's tiles that the user may see; a module that fails is left out instead of breaking the page.
        app.MapGet("/api/core/dashboard", async (IEnumerable<IDashboardProvider> providers, IPermissionService permissions, ILoggerFactory logs,
            CancellationToken ct) =>
        {
            var tiles = new List<DashboardTile>();
            foreach (var p in providers)
            {
                if (!await permissions.HasAsync(p.Permission, ct)) continue;
                try
                {
                    tiles.AddRange(await p.GetTilesAsync(ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logs.CreateLogger("Dashboard").LogWarning(ex, "Dashboard provider {Provider} failed", p.GetType().Name);
                }
            }
            return tiles;
        }).WithTags("Platform").RequireAuthorization();
    }
}
