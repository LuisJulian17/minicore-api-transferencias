using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MiniCore.Domain;

namespace MiniCore.Api.Auth;

public static class AppRoles
{
    public const string Operator = "Operator";
    public const string Auditor = "Auditor";
}

public static class AuthSetup
{
    public static IServiceCollection AddMiniCoreAuth(this IServiceCollection services,
        IConfiguration config, IHostEnvironment env)
    {
        var keyText = config["Jwt:SigningKey"];
        byte[] keyBytes;

        if (string.IsNullOrWhiteSpace(keyText))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException(
                    "Falta Jwt:SigningKey. Configúrala por variable de entorno (Jwt__SigningKey) o por el gestor de secretos.");
            keyBytes = RandomNumberGenerator.GetBytes(48);
        }
        else
        {
            keyBytes = Encoding.UTF8.GetBytes(keyText);
            if (keyBytes.Length < 32)
                throw new InvalidOperationException("Jwt:SigningKey debe tener al menos 32 caracteres.");
        }

        var issuer = config["Jwt:Issuer"] ?? "minicore-dev";
        var audience = config["Jwt:Audience"] ?? "minicore-api";
        var signingKey = new SymmetricSecurityKey(keyBytes);

        services.AddSingleton(new DevTokenIssuer(signingKey, issuer, audience));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false; 
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                };
            });

        services.AddAuthorization();
        return services;
    }

    public static Caller ToCaller(this ClaimsPrincipal user)
    {
        var clientId = user.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(clientId)) throw Errors.Unauthorized();

        return new Caller(clientId, user.IsInRole(AppRoles.Operator), user.IsInRole(AppRoles.Auditor));
    }
}

public sealed class DevTokenIssuer
{
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;

    public DevTokenIssuer(SymmetricSecurityKey key, string issuer, string audience)
    {
        _key = key;
        _issuer = issuer;
        _audience = audience;
    }

    public string? Issue(string user)
    {
        var claims = new List<Claim>();
        var normalized = user.Trim().ToUpperInvariant();

        if (normalized is "CUST-01" or "CUST-02" or "CUST-03" or "CUST-04" or "CUST-05" or "CUST-06")
            claims.Add(new Claim("sub", normalized));
        else if (normalized == "OPERATOR")
        {
            claims.Add(new Claim("sub", "OPER-01"));
            claims.Add(new Claim("role", AppRoles.Operator));
        }
        else if (normalized == "AUDITOR")
        {
            claims.Add(new Claim("sub", "AUD-01"));
            claims.Add(new Claim("role", AppRoles.Auditor));
        }
        else
        {
            return null;
        }

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
