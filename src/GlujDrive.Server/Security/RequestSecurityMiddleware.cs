using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace GlujDrive.Server.Security;

public sealed class RequestSecurityMiddleware(RequestDelegate next)
{
    private static readonly string[] PublicApiPaths =
    [
        "/api/auth/status",
        "/api/auth/login",
        "/api/auth/setup",
        "/api/health"
    ];

    public async Task InvokeAsync(HttpContext context, RootAccountStore accounts)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            if (!IsTrustedBrowserOrigin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Cross-origin request rejected.",
                    Detail = "Use the Gluj Drive page served by this server."
                });
                return;
            }

            var isPublic = PublicApiPaths.Any(path =>
                context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!isPublic &&
                !HostConnection.IsLocal(context) &&
                context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = accounts.IsConfigured ? "Sign in to Gluj Drive." : "Owner setup is required.",
                    Detail = accounts.IsConfigured
                        ? "This remote connection requires the owner account."
                        : "Open Gluj Drive on the host PC and create the owner account first."
                });
                return;
            }
        }

        await next(context);
    }

    private static bool IsTrustedBrowserOrigin(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var values) || values.Count != 1)
        {
            // Loopback tools and same-origin GET/navigation requests often omit Origin.
            return HostConnection.IsLocal(context) ||
                   HttpMethods.IsGet(context.Request.Method) ||
                   HttpMethods.IsHead(context.Request.Method);
        }

        if (!Uri.TryCreate(values[0], UriKind.Absolute, out var origin))
        {
            return false;
        }

        if (HostConnection.IsLocal(context) &&
            (origin.IsLoopback ||
             IPAddress.TryParse(origin.Host, out var originAddress) && IPAddress.IsLoopback(originAddress)))
        {
            // The Vite development proxy changes the API Host header while the
            // browser correctly retains its localhost:5173 Origin header.
            return true;
        }

        return string.Equals(origin.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
