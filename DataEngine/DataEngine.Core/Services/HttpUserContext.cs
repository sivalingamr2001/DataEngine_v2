using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using DataEngine.Core.Interfaces;

namespace DataEngine.Core.Services;

/// <summary>
/// Reads user identity from the current HTTP context claims.
/// </summary>
public sealed class HttpUserContext(IHttpContextAccessor httpContextAccessor) : IUserContext
{
    public string? UserId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
                return null;

            return user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue(ClaimTypes.Name)
                ?? user.FindFirstValue("sub")
                ?? user.Identity?.Name;
        }
    }

    public bool IsAuthenticated =>
        httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true;
}
