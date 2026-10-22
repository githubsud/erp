using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Erp.Infrastructure.Web;

/// <summary>
/// Browser protections on every response: no type sniffing (an uploaded "picture" can never run as a page), no framing by other sites,
/// and on the web app's page a content security policy that only runs our own scripts — so text someone typed can never become code,
/// and stolen sign-in tokens are much harder to obtain.
/// </summary>
public static partial class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, string? indexHtmlPath)
    {
        var policy = ContentSecurityPolicy(indexHtmlPath is not null && File.Exists(indexHtmlPath) ? File.ReadAllText(indexHtmlPath) : null);
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "same-origin";
                // Field staff check in with their location; nothing uses the camera or microphone directly.
                headers["Permissions-Policy"] = "geolocation=(self), camera=(), microphone=(), payment=(), usb=()";
                if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                    headers.ContentSecurityPolicy = policy;
                return Task.CompletedTask;
            });
            await next();
        });
    }

    /// <summary>
    /// Scripts only from our own address, plus the small inline scripts the build puts in index.html (allowed by their hash, read at start).
    /// Styles may be inline: Angular adds component styles that way. Receipts print from a same-page frame, which this policy also covers.
    /// </summary>
    public static string ContentSecurityPolicy(string? indexHtml)
    {
        var hashes = indexHtml is null
            ? []
            : InlineScript().Matches(indexHtml).Select(m => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value)))}'");
        return string.Join("; ",
            "default-src 'self'",
            $"script-src {string.Join(' ', ["'self'", .. hashes])}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob:",
            "font-src 'self' data:",
            "connect-src 'self'",
            "frame-src 'self' blob:",
            "worker-src 'self'",
            "manifest-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'");
    }

    [GeneratedRegex(@"<script>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex InlineScript();
}
