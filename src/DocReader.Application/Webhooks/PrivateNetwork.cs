using System.Net;
using System.Net.Sockets;

namespace DocReader.Application.Webhooks;

/// <summary>
/// Tells which addresses are not the public internet: loopback, private ranges, link-local (which includes the cloud metadata
/// address), shared address space, multicast and the unspecified address, for IPv4 and IPv6. A webhook is a request the worker
/// makes on behalf of whoever wrote the URL, so by default it may not be made to any of these.
/// </summary>
public static class PrivateNetwork
{
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPrivateIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPrivateIPv6(address),
            _ => true
        };
    }

    /// <summary>Whether a host name, before any DNS lookup, is obviously local. The connection guard checks the real addresses.</summary>
    public static bool IsLocalName(string host)
    {
        var name = host.TrimEnd('.').ToLowerInvariant();

        return name is "localhost" or "ip6-localhost" or "ip6-loopback"
            || name.EndsWith(".localhost", StringComparison.Ordinal)
            || name.EndsWith(".local", StringComparison.Ordinal)
            || name.EndsWith(".internal", StringComparison.Ordinal)
            || !name.Contains('.', StringComparison.Ordinal) && !IPAddress.TryParse(name, out _);
    }

    private static bool IsPrivateIPv4(byte[] bytes) =>
        bytes[0] == 0                                          // 0.0.0.0/8, "this network"
        || bytes[0] == 10                                       // 10.0.0.0/8
        || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)    // 100.64.0.0/10, carrier-grade NAT
        || bytes[0] == 127                                      // loopback
        || (bytes[0] == 169 && bytes[1] == 254)                 // 169.254.0.0/16, link-local and cloud metadata
        || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)     // 172.16.0.0/12, includes the default Docker network
        || (bytes[0] == 192 && bytes[1] == 168)                 // 192.168.0.0/16
        || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)  // 192.0.0.0/24
        || (bytes[0] == 198 && bytes[1] is 18 or 19)            // 198.18.0.0/15, benchmarking
        || bytes[0] >= 224;                                     // multicast, reserved and broadcast

    private static bool IsPrivateIPv6(IPAddress address) =>
        address.Equals(IPAddress.IPv6None)
        || address.Equals(IPAddress.IPv6Any)
        || address.IsIPv6LinkLocal
        || address.IsIPv6SiteLocal
        || address.IsIPv6Multicast
        || address.IsIPv6UniqueLocal;
}
