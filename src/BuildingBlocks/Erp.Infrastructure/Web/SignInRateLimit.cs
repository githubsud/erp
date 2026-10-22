using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Infrastructure.Web;

/// <summary>
/// Slows down password guessing: each device may try to sign in a limited number of times a minute (Security:SignInAttemptsPerMinute,
/// default 10), on top of the per-account lockout. Behind a proxy on the same computer (Tailscale serve, Cloudflare Tunnel) every request
/// comes from localhost, so the device is taken from the proxy's X-Forwarded-For header, which is trusted only from localhost.
/// </summary>
public static class SignInRateLimit
{
    public const string Policy = "sign-in";

    public static IServiceCollection AddSignInRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        var perMinute = configuration.GetValue("Security:SignInAttemptsPerMinute", 10);
        return services.AddRateLimiter(options =>
        {
            options.AddPolicy(Policy, http => RateLimitPartition.GetFixedWindowLimiter(ClientAddress(http), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, perMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    status = 429,
                    title = "Too many sign-in attempts. Wait a minute and try again.",
                    code = "auth.too_many_attempts",
                    message_ar = "محاولات دخول كثيرة. انتظر دقيقة ثم حاول مرة أخرى."
                }, ct);
            };
        });
    }

    public static string ClientAddress(HttpContext http)
    {
        var remote = http.Connection.RemoteIpAddress;
        if (remote is null || IPAddress.IsLoopback(remote))
        {
            var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
            // The proxy appends the address it saw last.
            var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (last is not null && IPAddress.TryParse(last, out var client)) return client.ToString();
        }
        return remote?.ToString() ?? "unknown";
    }
}
