using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace AngularCrudApi.Auth;

/// <summary>Bound from the "Jwt" configuration section.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "enterprise-core-api";
    public string Audience { get; set; } = "enterprise-core-app";

    /// <summary>
    /// HMAC-SHA256 key, at least 32 bytes. NEVER commit it: use `dotnet user-secrets`
    /// locally and an environment variable (Jwt__SigningKey) or a vault in production.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    public string RefreshCookieName { get; set; } = "ec_refresh";

    /// <summary>
    /// The cookie is only sent to the auth endpoints. Must match the API's route prefix.
    /// </summary>
    public string RefreshCookiePath { get; set; } = "/api/auth";

    /// <summary>
    /// Strict works when SPA and API are same-site (localhost:5173 -> localhost:7077, or
    /// app.example.com -> api.example.com). Use None (+ HTTPS) only for cross-site deployments.
    /// </summary>
    public SameSiteMode RefreshCookieSameSite { get; set; } = SameSiteMode.Strict;
}

/// <summary>The symmetric signing key, resolved once at startup.</summary>
public sealed class JwtSigningKey
{
    private JwtSigningKey(SymmetricSecurityKey key) => Key = key;

    public SymmetricSecurityKey Key { get; }

    public static JwtSigningKey Create(JwtOptions options, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(options.SigningKey))
        {
            var bytes = Encoding.UTF8.GetBytes(options.SigningKey);
            if (bytes.Length < 32)
            {
                throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes (256 bits).");
            }
            return new JwtSigningKey(new SymmetricSecurityKey(bytes));
        }

        if (environment.IsDevelopment())
        {
            // Zero-setup local runs: an ephemeral key. Access tokens become invalid on restart,
            // which is harmless because the SPA silently refreshes using the DB-backed refresh cookie.
            Console.WriteLine("warn: Jwt:SigningKey is not configured; using an ephemeral development key.");
            return new JwtSigningKey(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)));
        }

        throw new InvalidOperationException(
            "Jwt:SigningKey is not configured. Set it with an environment variable (Jwt__SigningKey) or a secret store.");
    }
}
