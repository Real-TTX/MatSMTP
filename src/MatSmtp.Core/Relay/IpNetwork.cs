using System.Net;
using System.Net.Sockets;

namespace MatSmtp.Core.Relay;

/// <summary>A network in CIDR notation (<c>192.168.0.0/16</c>, <c>fd00::/8</c>) or a single address. An IPv4 address that arrives as IPv6 (<c>::ffff:10.0.0.1</c>) is compared as IPv4.</summary>
public sealed class IpNetwork
{
    private readonly byte[] _prefix;

    private IpNetwork(IPAddress address, int bits)
    {
        Address = address;
        Bits = bits;
        _prefix = address.GetAddressBytes();
    }

    public IPAddress Address { get; }

    public int Bits { get; }

    public static bool TryParse(string? text, out IpNetwork network)
    {
        network = null!;
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var slash = value.IndexOf('/');
        var addressPart = slash < 0 ? value : value[..slash];
        if (!IPAddress.TryParse(addressPart, out var address))
        {
            return false;
        }

        address = Normalize(address);
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var bits = max;
        if (slash >= 0 && (!int.TryParse(value[(slash + 1)..], out bits) || bits < 0 || bits > max))
        {
            return false;
        }

        network = new IpNetwork(address, bits);
        return true;
    }

    public bool Contains(IPAddress? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        var address = Normalize(candidate);
        if (address.AddressFamily != Address.AddressFamily)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        var full = Bits / 8;
        for (var i = 0; i < full; i++)
        {
            if (bytes[i] != _prefix[i])
            {
                return false;
            }
        }

        var rest = Bits % 8;
        if (rest == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));
        return (bytes[full] & mask) == (_prefix[full] & mask);
    }

    public override string ToString() => $"{Address}/{Bits}";

    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>A list of networks (separated by commas, semicolons, spaces or line breaks), as the rules and the settings of the receiver hold them.</summary>
public static class IpNetworks
{
    private static readonly char[] Separators = [',', ';', ' ', '\n', '\r', '\t'];

    public static IReadOnlyList<IpNetwork> Parse(string? list)
    {
        var result = new List<IpNetwork>();
        foreach (var entry in (list ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IpNetwork.TryParse(entry, out var network))
            {
                result.Add(network);
            }
        }

        return result;
    }

    public static bool Contains(string? list, IPAddress? address) => address is not null && Parse(list).Any(n => n.Contains(address));

    /// <summary>The first entry that is not a network, or null.</summary>
    public static string? Problem(string? list)
    {
        foreach (var entry in (list ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IpNetwork.TryParse(entry, out _))
            {
                return $"„{entry}“ ist keine IP-Adresse und kein Netz (Beispiel: 192.168.1.0/24).";
            }
        }

        return null;
    }
}
