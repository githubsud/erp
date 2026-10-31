using System.Threading.RateLimiting;
using Erp.Infrastructure.Web;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Commerce.Storefront;

/// <summary>
/// The storefront is anonymous and a checkout creates a customer and a sales order, so each device (by address, as for sign-in) gets
/// a budget per minute: browsing (Commerce:BrowseRequestsPerMinute, default 240), cart changes (Commerce:CartChangesPerMinute, 60) and
/// checkouts (Commerce:CheckoutsPerMinute, 5).
/// </summary>
internal static class StorefrontLimits
{
    public const string Browse = "storefront-browse";
    public const string Cart = "storefront-cart";
    public const string Checkout = "storefront-checkout";

    public static IServiceCollection AddStorefrontLimits(this IServiceCollection services, IConfiguration configuration) =>
        services.Configure<RateLimiterOptions>(options =>
        {
            Add(options, Browse, configuration.GetValue("Commerce:BrowseRequestsPerMinute", 240));
            Add(options, Cart, configuration.GetValue("Commerce:CartChangesPerMinute", 60));
            Add(options, Checkout, configuration.GetValue("Commerce:CheckoutsPerMinute", 5));
        });

    private static void Add(RateLimiterOptions options, string policy, int perMinute) =>
        options.AddPolicy(policy, http => RateLimitPartition.GetFixedWindowLimiter($"{policy}:{SignInRateLimit.ClientAddress(http)}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, perMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
}
