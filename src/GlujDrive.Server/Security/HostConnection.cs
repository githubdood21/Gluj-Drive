using System.Net;

namespace GlujDrive.Server.Security;

public static class HostConnection
{
    public static bool IsLocal(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            return false;
        }

        var host = context.Request.Host.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               IPAddress.TryParse(host, out var hostAddress) && IPAddress.IsLoopback(hostAddress);
    }
}
