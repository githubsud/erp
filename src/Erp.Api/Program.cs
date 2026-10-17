using Erp.Api;
using Erp.Infrastructure;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Tenancy;
using Erp.Modules.Core.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

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

app.Run();

public partial class Program;
