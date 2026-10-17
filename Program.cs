using System.Threading.RateLimiting;
using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Endpoint;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Infrastructure
// ---------------------------------------------------------------------------
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Every error (validation, 401/403/404, unhandled exceptions) is RFC 7807 ProblemDetails,
// which the React client maps onto form fields automatically.
builder.Services.AddProblemDetails();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// CORS: the SPA sends the refresh cookie (withCredentials), so origins must be explicit.
// AllowAnyOrigin() + AllowCredentials() is rejected by browsers.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("SpaClient", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .WithExposedHeaders("Location"));
});

// ---------------------------------------------------------------------------
// Authentication: short-lived JWT access token + rotating httpOnly refresh cookie
// ---------------------------------------------------------------------------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var signingKey = JwtSigningKey.Create(jwtOptions, builder.Environment);
builder.Services.AddSingleton(signingKey);
builder.Services.AddScoped<TokenService>();
builder.Services.Configure<SignupOptions>(builder.Configuration.GetSection(SignupOptions.SectionName));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep claim names exactly as issued ("sub", "permission"...), no WS-* remapping.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey.Key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AppClaims.Name,
            RoleClaimType = AppClaims.Role,
        };
    });

// One policy per permission scope; wildcards ("employees:*", "*") are honoured by PermissionHandler.
builder.Services.AddPermissionPolicies(Permissions.All);

// Brute-force protection for the login endpoint.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline (order matters)
// ---------------------------------------------------------------------------
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // In Development the SPA calls http://localhost:7077 directly; a redirect would break CORS preflights.
    app.UseHttpsRedirection();
}

app.UseRouting();
app.UseCors("SpaClient");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapEmployeeEndpoints();
app.MapCountryEndpoints();
app.MapStateEndpoints();
app.MapCityEndpoints();
app.MapUserEndpoints();
app.MapLookupEndpoints();

// Development: create auth tables / unique email index and seed demo users (see appsettings.Development.json).
await DatabaseBootstrapper.InitializeAsync(app);

app.Run();
