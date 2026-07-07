using DataEngine.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace DataEngine.API.Security;

public static class AuthenticationExtensions
{
    public const string SmartAuthScheme = "SmartAuth";

    public static IServiceCollection AddDataEngineAuthentication(
        this IServiceCollection services,
        SecurityOptions security)
    {
        var hasApiKey = !string.IsNullOrWhiteSpace(security.ApiKey);
        var hasJwt = security.Jwt.Enabled;

        if (!security.RequireAuthentication && !hasApiKey && !hasJwt)
            return services;

        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SmartAuthScheme;
            options.DefaultChallengeScheme = SmartAuthScheme;
        });

        authBuilder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName, _ => { });

        if (hasJwt)
        {
            authBuilder.AddJwtBearer(options =>
            {
                options.Authority = string.IsNullOrWhiteSpace(security.Jwt.Authority)
                    ? null : security.Jwt.Authority;
                options.Audience = string.IsNullOrWhiteSpace(security.Jwt.Audience)
                    ? null : security.Jwt.Audience;

                if (!string.IsNullOrWhiteSpace(security.Jwt.SigningKey))
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = !string.IsNullOrWhiteSpace(security.Jwt.Audience),
                        ValidAudience = security.Jwt.Audience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(security.Jwt.SigningKey))
                    };
                }
            });
        }

        authBuilder.AddPolicyScheme(SmartAuthScheme, SmartAuthScheme, options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var authHeader = context.Request.Headers.Authorization.ToString();
                if (hasJwt && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    return JwtBearerDefaults.AuthenticationScheme;

                if (hasApiKey)
                    return ApiKeyAuthenticationHandler.SchemeName;

                return hasJwt ? JwtBearerDefaults.AuthenticationScheme : ApiKeyAuthenticationHandler.SchemeName;
            };
        });

        services.AddAuthorization(options =>
        {
            if (security.RequireAuthentication)
            {
                options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
            }
        });

        return services;
    }
}
