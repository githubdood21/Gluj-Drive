using System.Net;

namespace GlujDrive.Server.Security;

public static class HostConnection
{
    public static bool IsLocal(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        return remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);
    }
}
