using System.Text.Json.Serialization;
using Kontor.Api.Data;
using Kontor.Api.Endpoints;
using Kontor.Api.Services;
using Kontor.Domain.Sanctions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

builder.Services.AddOptions<KontorOptions>().BindConfiguration(KontorOptions.Section)
    .Validate(o => Kontor.Domain.Banking.Iban.TryParse(o.NostroIban, out _, out _), "Kontor:NostroIban is not a valid IBAN.")
    .Validate(o => o.BankCode.Length == 8 && o.BankCode.All(char.IsAsciiDigit), "Kontor:BankCode must have 8 digits.")
    .ValidateOnStart();
builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section)
    .Validate(o => o.Authority is not null || o.Key is not null, "Configure Auth:Authority (identity provider) or, for development, Auth:SigningKey (32+ characters).")
    .ValidateOnStart();

// SQLite runs without any setup; PostgreSQL is the production choice (Database:Provider = postgres)
builder.Services.AddDbContext<KontorDb>((sp, o) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connection = config.GetConnectionString("Default");
    if (string.Equals(config["Database:Provider"], "postgres", StringComparison.OrdinalIgnoreCase))
        o.UseNpgsql(connection ?? throw new InvalidOperationException("ConnectionStrings:Default is required for PostgreSQL."));
    else
        o.UseSqlite(connection ?? "Data Source=kontor.db");
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var path = sp.GetRequiredService<IOptions<KontorOptions>>().Value.SanctionsListPath;
    path = string.IsNullOrWhiteSpace(path) ? Path.Combine(AppContext.BaseDirectory, "Data", "demo-sanctions.xml") : path;
    using var file = File.OpenRead(path);
    var list = SanctionsList.LoadEuXml(file);
    sp.GetRequiredService<ILogger<SanctionsList>>().LogInformation("Sanctions list {Path}: {Count} entries", path, list.Parties.Count);
    return list;
});
builder.Services.AddScoped<AuditTrail>();
builder.Services.AddScoped<UnitOfWork>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<SepaService>();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<KontorDb>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>, TimeProvider>((bearer, authOptions, time) =>
    {
        var auth = authOptions.Value;
        bearer.MapInboundClaims = false;
        bearer.Audience = auth.Audience;
        if (auth.Authority is not null)
            bearer.Authority = auth.Authority; // keys and issuer come from the provider's discovery document
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = auth.Authority is null ? auth.Issuer : null,
            ValidAudience = auth.Audience,
            IssuerSigningKey = auth.Authority is null ? auth.Key : null,
            ValidAlgorithms = auth.Authority is null ? [SecurityAlgorithms.HmacSha256] : [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
            NameClaimType = "sub",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30),
            // Lifetime is checked against the injected clock, so tests (and nothing else) can move time
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires > now.AddSeconds(-30) && (notBefore is null || notBefore <= now.AddSeconds(30));
            },
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Roles.Clerk, p => p.RequireRole(Roles.Clerk))
    .AddPolicy(Roles.Approver, p => p.RequireRole(Roles.Approver))
    .AddPolicy(Roles.Compliance, p => p.RequireRole(Roles.Compliance))
    .AddPolicy(Roles.Operations, p => p.RequireRole(Roles.Operations))
    .AddPolicy(Roles.Auditor, p => p.RequireRole(Roles.Auditor))
    .AddPolicy(Policies.ApproverOrCompliance, p => p.RequireRole(Roles.Approver, Roles.Compliance))
    .AddPolicy(Policies.OperationsOrAuditor, p => p.RequireRole(Roles.Operations, Roles.Auditor));

var app = builder.Build();
await DatabaseSetup.InitializeAsync(app.Services);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        // The dashboard is plain files from this server: no inline scripts, no third-party code
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; form-action 'self'";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store"; // account data must not sit in caches
        return Task.CompletedTask;
    });
    await next();
});
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var devTools = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Kontor:EnableDevTools");
if (devTools)
{
    app.MapOpenApi().AllowAnonymous();
    app.MapDevTools();
}
app.MapHealthChecks("/health").AllowAnonymous();
app.MapKontor();

app.Run();

public partial class Program;
