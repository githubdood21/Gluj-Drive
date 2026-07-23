using System.Globalization;
using System.Net;

namespace GlujDrive.Server.Security;

public readonly record struct IpNetworkRule(byte[] NetworkBytes, int PrefixLength)
{
    public static bool TryParse(string value, out IpNetworkRule rule)
    {
        rule = default;
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 ||
            !IPAddress.TryParse(parts[0], out var address))
        {
            return false;
        }

        address = Normalize(address);
        var bytes = address.GetAddressBytes();
        var maximumPrefixLength = bytes.Length * 8;
        var prefixLength = maximumPrefixLength;

        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength) ||
             prefixLength < 0 ||
             prefixLength > maximumPrefixLength))
        {
            return false;
        }

        ApplyMask(bytes, prefixLength);
        rule = new IpNetworkRule(bytes, prefixLength);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        var candidate = Normalize(address).GetAddressBytes();
        if (candidate.Length != NetworkBytes.Length)
        {
            return false;
        }

        var fullBytes = PrefixLength / 8;
        for (var index = 0; index < fullBytes; index++)
        {
            if (candidate[index] != NetworkBytes[index])
            {
                return false;
            }
        }

        var remainingBits = PrefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (candidate[fullBytes] & mask) == NetworkBytes[fullBytes];
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static void ApplyMask(byte[] bytes, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        if (remainingBits != 0)
        {
            bytes[fullBytes] &= (byte)(0xff << (8 - remainingBits));
            fullBytes++;
        }

        Array.Clear(bytes, fullBytes, bytes.Length - fullBytes);
    }
}
