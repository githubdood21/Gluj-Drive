using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace GlujDrive.Server.Security;

public sealed class NetworkAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ServerSettingsStore settings,
        ILogger<NetworkAccessMiddleware> logger)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (IsAllowed(remoteAddress, settings.Current))
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "Rejected connection from {RemoteAddress} because of the configured IP access rules.",
            remoteAddress?.ToString() ?? "unknown");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "This device is not allowed to access Gluj Drive.",
            Detail = "The host PC can change the IP allow and deny lists in Server settings."
        });
    }

    public static bool IsAllowed(IPAddress? remoteAddress, ServerSettings settings)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        if (remoteAddress.IsIPv4MappedToIPv6)
        {
            remoteAddress = remoteAddress.MapToIPv4();
        }

        if (IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        if (MatchesAny(remoteAddress, settings.IpDenyList))
        {
            return false;
        }

        return settings.IpAllowList is not { Count: > 0 } ||
               MatchesAny(remoteAddress, settings.IpAllowList);
    }

    private static bool MatchesAny(IPAddress address, IReadOnlyList<string>? rules) =>
        rules?.Any(rule =>
            IpNetworkRule.TryParse(rule, out var network) &&
            network.Contains(address)) == true;
}
