using System.Globalization;
using Erp.Api;
using Erp.Infrastructure;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Tenancy;
using Erp.Infrastructure.Web;
using Erp.Modules.Core.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

// Container health probe: `dotnet Erp.Api.dll --healthcheck` exits 0 when /health answers.
if (args.Contains("--healthcheck"))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";
    try { return (await http.GetAsync($"http://localhost:{port}/health")).IsSuccessStatusCode ? 0 : 1; }
    catch { return 1; }
}

// Documents and messages format dates/numbers explicitly (dd/MM/yyyy, N2); the server's regional settings must not change them.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// As a Windows service (scripts/install.ps1) the working folder is System32: files are found next to the program instead.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null
});
builder.Host.UseWindowsService(o => o.ServiceName = "ErpApi");
// An installed computer keeps its passwords and keys outside the program folder, so updates never overwrite them.
if (builder.Configuration["ConfigFile"] is { Length: > 0 } configFile)
    builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: false);

var modules = ModuleCatalog.All;
builder.Services.AddErpInfrastructure(builder.Configuration, modules);
foreach (var module in modules)
    module.RegisterServices(builder.Services, builder.Configuration);

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.CreateKey(),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub"
        };
    });
builder.Services.AddAuthorization();

// The web app may be served from another origin (e.g. Cloudflare Pages); list allowed origins in Cors:AllowedOrigins.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("Content-Disposition")));
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// Release step: `dotnet Erp.Api.dll --migrate-only` applies migrations with the owner connection and exits.
if (args.Contains("--migrate-only"))
{
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
    return 0;
}
if (app.Configuration.GetValue<bool>($"{DatabaseOptions.Section}:{nameof(DatabaseOptions.MigrateOnStartup)}"))
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();

// The development keys are in the source code: anything encrypted with them (QID, passport, IBAN) is not really protected.
if (!app.Environment.IsDevelopment() && app.Configuration["Security:FieldEncryptionKey"] == "ZGV2LW9ubHktZmllbGQta2V5LTMyLWJ5dGVzLWxvbmc=")
    app.Logger.LogWarning("Security:FieldEncryptionKey is the public development key. Use it for demo data only; a real shop needs its own keys.");

app.UseExceptionHandler();
app.UseStatusCodePages();

// One computer in a shop (reached through Tailscale) serves the built web app too, so phones see one https address.
// Web:Root is the `ng build` output; when it is missing the app is served elsewhere (ng serve, Cloudflare Pages).
var webRoot = Path.GetFullPath(app.Configuration["Web:Root"] ?? "../Erp.Web/dist/erp-web/browser", app.Environment.ContentRootPath);
var serveWeb = File.Exists(Path.Combine(webRoot, "index.html"));
app.UseSecurityHeaders(serveWeb ? Path.Combine(webRoot, "index.html") : null);
if (serveWeb)
{
    var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = files,
        ServeUnknownFileTypes = false,
        // Hashed bundles never change; the page and the service worker's files must be checked on every load or updates never arrive.
        OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = c.File.Name is "index.html" or "ngsw.json" or "ngsw-worker.js" or "manifest.webmanifest"
            ? "no-cache" : "public, max-age=604800"
    });
}
// Explicit, after the files: otherwise routing runs first, picks the index.html fallback and static files are skipped.
app.UseRouting();
app.UseRateLimiter();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseMiddleware<Erp.Infrastructure.Web.ModuleGateMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => o.WithTitle("Qatar SME ERP API"));
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
foreach (var module in modules)
    module.MapEndpoints(app);
// Deep links (/sales/pos) load the app; unknown /api addresses stay 404.
if (serveWeb)
    app.MapFallbackToFile("{*path:regex(^(?!api/|scalar|openapi).*$)}", "index.html",
        new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot), OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

await app.RunAsync();
return 0;

public partial class Program;
