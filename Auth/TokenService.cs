using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AngularCrudApi.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AngularCrudApi.Auth;

/// <summary>Issues access tokens (JWT) and opaque refresh tokens.</summary>
public sealed class TokenService(IOptions<JwtOptions> options, JwtSigningKey signingKey)
{
    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);

    public string CreateAccessToken(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(AppClaims.Subject, user.Id.ToString()),
            new(AppClaims.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (!string.IsNullOrWhiteSpace(user.Email)) claims.Add(new Claim(AppClaims.Email, user.Email));
        claims.AddRange(user.GetRoles().Select(role => new Claim(AppClaims.Role, role)));
        claims.AddRange(user.GetPermissions().Select(permission => new Claim(AppClaims.Permission, permission)));

        var now = DateTime.UtcNow;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(_options.AccessTokenMinutes),
            SigningCredentials = new SigningCredentials(signingKey.Key, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>A 384-bit random, URL-safe token. Only its hash is persisted.</summary>
    public static string CreateRefreshToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(48));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
