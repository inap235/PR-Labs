using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Labs.Lab3.Chat;

/// The network the chat uses: an IPv4 address of this computer, its mask and its adapter.
/// The general channel leaves this computer through this one adapter.
public sealed record LocalNetwork(IPAddress Address, IPAddress Mask, string Adapter, bool HasGateway)
{
    /// The broadcast address of the network: the address with all host bits set,
    /// for example 192.168.100.255 for 192.168.100.15 with the mask 255.255.255.0.
    public IPAddress Broadcast
    {
        get
        {
            byte[] address = Address.GetAddressBytes();
            byte[] mask = Mask.GetAddressBytes();
            for (int i = 0; i < address.Length; i++)
                address[i] |= (byte)~mask[i];
            return new IPAddress(address);
        }
    }

    public override string ToString() => $"{Address} ({Adapter})";

    /// The network with the given address, or by default the first adapter with a gateway: the
    /// real local network, not a virtual adapter (VirtualBox, WSL, Docker). Without one, only the
    /// peers of this computer can be reached, through the loopback adapter.
    public static LocalNetwork Choose(IPAddress? wanted)
    {
        var all = All();
        if (wanted != null)
        {
            return all.FirstOrDefault(n => n.Address.Equals(wanted))
                   ?? throw new ArgumentException($"This computer has no address {wanted}. Its IPv4 addresses: " +
                                                  string.Join(", ", all.Select(n => n.ToString())));
        }
        return all.FirstOrDefault(n => n.HasGateway)
               ?? all.FirstOrDefault(n => IPAddress.IsLoopback(n.Address))
               ?? new LocalNetwork(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), "loopback", false);
    }

    /// The IPv4 addresses of the adapters that are up.
    public static List<LocalNetwork> All()
    {
        var list = new List<LocalNetwork>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
                continue;
            var properties = adapter.GetIPProperties();
            bool hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(new LocalNetwork(unicast.Address, unicast.IPv4Mask, adapter.Name, hasGateway));
            }
        }
        return list;
    }
}
