using System.Globalization;
using Erp.Api;
using Erp.Infrastructure;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Tenancy;
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

var builder = WebApplication.CreateBuilder(args);

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

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => o.WithTitle("Qatar SME ERP API"));
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
foreach (var module in modules)
    module.MapEndpoints(app);

await app.RunAsync();
return 0;

public partial class Program;
