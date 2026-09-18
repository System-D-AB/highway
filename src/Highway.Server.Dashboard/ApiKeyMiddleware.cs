using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Highway.Server.Dashboard;

/// <summary>
/// Middleware that enforces API-key authentication on every request.
/// Accepts the key from: X-Highway-Key header, ?key= query parameter, or
/// a session cookie (set on first successful presentation).
/// </summary>
internal sealed class ApiKeyMiddleware
{
    private const string HeaderName = "X-Highway-Key";
    private const string QueryParam = "key";
    private const string CookieName = ".highway-session";

    private readonly RequestDelegate _next;
    private readonly byte[] _expectedKeyBytes;

    public ApiKeyMiddleware(RequestDelegate next, DashboardOptions options)
    {
        _next = next;
        _expectedKeyBytes = Encoding.UTF8.GetBytes(options.ApiKey ?? "");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Liveness and readiness probes are keyless (052): an orchestrator must reach them without a
        // secret, and they leak nothing beyond up/down and a role word. The detailed /replication
        // route, which exposes topology, is NOT exempt and stays behind the key.
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health") || path.StartsWithSegments("/ready"))
        {
            await _next(context);
            return;
        }

        // Check cookie first (set on previous successful auth)
        if (context.Request.Cookies.TryGetValue(CookieName, out var cookieValue)
            && IsValid(cookieValue))
        {
            await _next(context);
            return;
        }

        // Check header
        string? presented = context.Request.Headers[HeaderName].FirstOrDefault();

        // Check query parameter (for EventSource which can't set headers)
        if (presented is null)
            presented = context.Request.Query[QueryParam].FirstOrDefault();

        if (presented is null || !IsValid(presented))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized: API key required.");
            return;
        }

        // Set session cookie so subsequent requests don't need the key
        context.Response.Cookies.Append(CookieName, presented, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
            IsEssential = true,
        });

        await _next(context);
    }

    private bool IsValid(string presented)
    {
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(presentedBytes, _expectedKeyBytes);
    }
}
