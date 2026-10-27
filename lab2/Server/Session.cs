using System.Net.Sockets;

namespace Labs.Lab2.Server;

/// One connected client: receives its messages, answers them, logs what happens.
public sealed class Session(ShopServer shop, Connection connection)
{
    private bool _firstMessage = true;
    private bool _isProvider;

    /// Runs until the client leaves. How a gone client is noticed:
    ///   Receive returns 0  - it closed the connection (at once)
    ///   ConnectionReset    - its process was killed (at once)
    ///   TimedOut           - its network is gone (keepalive, after about 16 s)
    public void Run()
    {
        Log.Write(connection.Endpoint, "connected");
        var buffer = new byte[4096];
        var framer = new LineFramer();
        string reason = "closed the connection";
        try
        {
            while (true)
            {
                int count = connection.Receive(buffer);
                if (count == 0)
                    return; // the client closed its side of the connection

                foreach (var message in framer.Push(buffer, count))
                {
                    if (message.Error != null)
                    {
                        Error(message.Error);
                    }
                    else if (!Handle(message.Line!))
                    {
                        reason = "quit";
                        return;
                    }
                }
            }
        }
        catch (SocketException e)
        {
            reason = $"connection lost ({e.SocketErrorCode})";
        }
        catch (ObjectDisposedException)
        {
            reason = "closed by the server after a failed send";
        }
        catch (Exception e)
        {
            // an exception that ends a thread ends the whole server
            reason = $"server error: {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            if (_isProvider)
            {
                shop.Provider.Leave(connection); // first, so no more alerts go to a closing socket
                reason += "; the provider left, the shop goes on with the stock it has";
            }
            connection.Close();
            Log.Write(connection.Endpoint, $"disconnected: {reason}");
            shop.Kitchen.Cancel(connection);
        }
    }

    /// Handles one line. False means the client asked to quit.
    private bool Handle(string line)
    {
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return true;

        bool first = _firstMessage;
        _firstMessage = false;

        switch (words[0].ToUpperInvariant())
        {
            case "HELLO": Hello(words, first); break;
            case "MENU": Menu(words); break;
            case "ORDER": Order(words); break;
            case "DELIVER": Deliver(words); break;
            case "QUIT":
                if (words.Length > 1)
                {
                    Error("QUIT takes no parameters");
                    break;
                }
                Reply("BYE");
                return false;
            default: Error($"unknown command '{words[0]}'"); break;
        }
        return true;
    }

    private void Hello(string[] words, bool first)
    {
        if (!first)
        {
            Error("HELLO only as the first message");
            return;
        }
        if (words.Length != 2)
        {
            Error("HELLO needs a role: client or provider");
            return;
        }
        switch (words[1].ToLowerInvariant())
        {
            case "client": Reply("OK client"); break;
            case "provider": BecomeProvider(); break;
            default: Error($"unknown role '{words[1]}'"); break;
        }
    }

    private void BecomeProvider()
    {
        if (!shop.Provider.TryTake(connection))
        {
            Error("provider already connected");
            return;
        }
        _isProvider = true;
        Log.Write(connection.Endpoint, "is the provider");

        // Alerts missed while no provider was connected: every product low right now.
        // Slot first, stock after: a fall in between is never missed (at worst sent twice).
        int[] stock = shop.Stock.Snapshot();
        for (int p = 0; p < Products.Count; p++)
        {
            if (stock[p] < Stock.Low)
                shop.Provider.Alert(p, stock[p]);
        }
    }

    private void Deliver(string[] words)
    {
        if (!_isProvider)
        {
            Error("only the provider can deliver");
            return;
        }
        if (!Protocol.TryParseItems(words, "delivery", out int[] delivered, out string error))
        {
            Error(error);
            return;
        }

        if (!shop.Stock.TryDeliver(delivered, out int[] after))
        {
            Error($"the stock can hold at most {Stock.Max} of each product");
            return;
        }
        Reply($"DELIVERED {Protocol.Items(after)}");
        Log.Write(connection.Endpoint, $"delivery: {Protocol.Items(delivered, skipZero: true)}, stock {Protocol.Items(after)}");
    }

    private void Menu(string[] words)
    {
        if (words.Length > 1)
            Error("MENU takes no parameters");
        else
            Reply(Protocol.Menu(shop.Stock.Snapshot()));
    }

    private void Order(string[] words)
    {
        if (_isProvider)
        {
            Error("the provider cannot order");
            return;
        }
        if (!Protocol.TryParseItems(words, "order", out int[] wanted, out string error))
        {
            Error(error);
            return;
        }

        if (!shop.Stock.TryTake(wanted, out int[] before))
        {
            Reply($"REFUSED {Protocol.Short(wanted, before)}");
            Log.Write(connection.Endpoint, $"order refused: {Protocol.Items(wanted, skipZero: true)}, stock {Protocol.Items(before)}");
            return;
        }

        var order = shop.Kitchen.Add(wanted, connection);
        Reply($"ACCEPTED {order.Id} {order.Seconds}");
        Log.Write(connection.Endpoint, $"order {order.Id} accepted: {Protocol.Items(wanted, skipZero: true)}, {order.Seconds} s");
        order.Announced.Set(); // only now may a worker start it and later send READY

        // this thread made the stock fall, so it tells the provider
        for (int p = 0; p < Products.Count; p++)
        {
            if (Stock.FellLow(before, wanted, p))
                shop.Provider.Alert(p, before[p] - wanted[p]);
        }
    }

    private void Reply(string line) => connection.Send(line);

    private void Error(string reason)
    {
        connection.Send($"ERROR {reason}");
        Log.Write(connection.Endpoint, $"error: {reason}");
    }
}
