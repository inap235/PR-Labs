namespace Labs.Lab2.Server;

/// The provider's connection, if one is connected. A connection becomes the provider
/// by sending HELLO provider while the slot is free; all others are clients.
public sealed class ProviderSlot
{
    private readonly object _lock = new();
    private Connection? _provider;

    /// False if another provider is already connected.
    public bool TryTake(Connection connection)
    {
        lock (_lock)
        {
            if (_provider != null)
                return false;
            _provider = connection;
            // under the lock, so no ALERT can come before OK
            connection.Send("OK provider");
            return true;
        }
    }

    public void Leave(Connection connection)
    {
        lock (_lock)
        {
            if (_provider == connection)
                _provider = null;
        }
    }

    /// Called by the client thread whose order made the stock fall below 5,
    /// right after the order, outside the stock's lock.
    public void Alert(int product, int left)
    {
        Connection? provider;
        lock (_lock)
            provider = _provider;

        string line = $"ALERT {Products.Names[product]} {left}";
        if (provider == null)
        {
            Log.Write("server", $"{line}, no provider connected: it gets the alert when it connects");
            return;
        }
        if (provider.Send(line))
            Log.Write(provider.Endpoint, line);
        else
            Log.Write(provider.Endpoint, $"{line} not sent, the provider is gone: the next one gets it when it connects");
    }
}
