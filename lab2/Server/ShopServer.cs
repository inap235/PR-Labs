using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Labs.Lab2.Server;

/// The listening socket, and the data all threads share, each with its protection:
///   stock                         - lock in Stock
///   waiting orders, order numbers - lock in Kitchen
///   the provider                  - lock in ProviderSlot
///   sending on a socket           - lock in Connection
///   the log                       - Console.WriteLine is thread safe
public sealed class ShopServer(IPAddress bind, int port, Stock stock, int workers)
{
    public Stock Stock { get; } = stock;
    public Kitchen Kitchen { get; } = new(workers, stock);
    public ProviderSlot Provider { get; } = new();

    public void Run()
    {
        // The listening socket only accepts connections. Every Accept returns a new socket for
        // one client; the messages of that client go through that new socket.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(bind, port));
        listener.Listen(backlog: 100);

        Log.Write("server", $"listening on {listener.LocalEndPoint}");
        if (bind.Equals(IPAddress.Any))
            LogNetworkAddresses();
        Log.Write("server", $"{Kitchen.Workers} workers, stock: {Protocol.Items(Stock.Snapshot())}");

        while (true)
        {
            Socket socket;
            try
            {
                socket = listener.Accept();
            }
            catch (SocketException e)
            {
                // e.g. a client that reset its connection before it was accepted: only that one is lost
                Log.Write("server", $"error: accept failed ({e.SocketErrorCode})");
                continue;
            }

            try
            {
                // Every client gets its own thread, and this one goes straight back to Accept.
                var session = new Session(this, new Connection(socket));
                new Thread(session.Run)
                {
                    Name = $"client {socket.RemoteEndPoint}",
                    IsBackground = true
                }.Start();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                Log.Write("server", $"error: connection lost before it was served ({e.Message})");
                socket.Close();
            }
        }
    }

    /// Addresses other computers can use: adapters that are up and have a gateway
    /// (this skips loopback and virtual ones like VirtualBox).
    private void LogNetworkAddresses()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
                continue;
            var properties = adapter.GetIPProperties();
            bool hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            if (!hasGateway)
                continue;

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    Log.Write("server", $"other computers connect to {unicast.Address}:{port} ({adapter.Name})");
            }
        }
    }
}
