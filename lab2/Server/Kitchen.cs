namespace Labs.Lab2.Server;

/// Prepares the orders with a fixed number of workers. A producer-consumer queue:
///   producers - the client threads (Add)
///   consumers - the workers (Work), each takes the oldest order
///   queue     - _waiting, guarded by _lock
/// Cancel removes the orders of a client that is gone and puts their products back.
public sealed class Kitchen
{
    private readonly object _lock = new();
    private readonly LinkedList<Order> _waiting = new(); // oldest first
    private readonly List<Order> _preparing = [];
    private readonly Stock _stock;
    private int _lastOrderId;

    public int Workers { get; }

    public Kitchen(int workers, Stock stock)
    {
        Workers = workers;
        _stock = stock;
        for (int i = 1; i <= workers; i++)
        {
            int number = i;
            new Thread(() => Work(number))
            {
                Name = $"worker {number}",
                IsBackground = true
            }.Start();
        }
    }

    /// Numbers the order and queues it in one step, so the numbers follow the queue.
    public Order Add(int[] quantities, Connection client)
    {
        lock (_lock)
        {
            var order = new Order(++_lastOrderId, quantities, Products.PreparationSeconds(quantities), client);
            _waiting.AddLast(order);
            Monitor.Pulse(_lock); // wakes one waiting worker, if any
            return order;
        }
    }

    /// Cancels all orders of a client that is gone. Under the lock an order is either
    /// in the queue (removed here, products back here) or with a worker (marked here,
    /// the worker stops and puts the products back), never both.
    public void Cancel(Connection client)
    {
        var removed = new List<Order>();
        lock (_lock)
        {
            for (var node = _waiting.First; node != null;)
            {
                var next = node.Next;
                if (node.Value.Client == client)
                {
                    removed.Add(node.Value);
                    _waiting.Remove(node);
                }
                node = next;
            }
            foreach (var order in _preparing)
            {
                if (order.Client == client)
                    order.Cancelled.Set(); // wakes the worker that waits on it
            }
        }

        foreach (var order in removed)
        {
            int[] stock = _stock.Return(order.Quantities);
            Log.Write(client.Endpoint, $"order {order.Id} cancelled while waiting, back to stock: {Protocol.Items(order.Quantities, skipZero: true)}, stock {Protocol.Items(stock)}");
        }
    }

    private void Work(int number)
    {
        while (true)
        {
            Order order;
            int stillWaiting;
            lock (_lock)
            {
                // while, not if: another worker may take the order first
                while (_waiting.Count == 0)
                    Monitor.Wait(_lock); // releases the lock while waiting
                order = _waiting.First!.Value;
                _waiting.RemoveFirst();
                _preparing.Add(order);
                stillWaiting = _waiting.Count;
            }

            try
            {
                Prepare(order, number, stillWaiting);
            }
            catch (Exception e)
            {
                // an exception that ends a thread ends the whole server
                Log.Write(order.Client.Endpoint, $"error: worker {number} failed on order {order.Id}: {e.Message}");
            }
        }
    }

    private void Prepare(Order order, int worker, int stillWaiting)
    {
        order.Announced.Wait(); // READY must not come before ACCEPTED
        Log.Write(order.Client.Endpoint, $"order {order.Id} started by worker {worker}, {stillWaiting} orders waiting");

        // Preparing = waiting. Waiting on Cancelled instead of Thread.Sleep wakes up early if the client leaves.
        order.Cancelled.Wait(TimeSpan.FromSeconds(order.Seconds));

        bool cancelled;
        lock (_lock)
        {
            // decided under the lock: after this, Cancel no longer sees the order
            _preparing.Remove(order);
            cancelled = order.Cancelled.IsSet;
        }

        if (cancelled)
        {
            // what was prepared is lost, the products go back
            int[] stock = _stock.Return(order.Quantities);
            Log.Write(order.Client.Endpoint, $"order {order.Id} stopped by worker {worker}, client gone, back to stock: {Protocol.Items(order.Quantities, skipZero: true)}, stock {Protocol.Items(stock)}");
            return;
        }

        Log.Write(order.Client.Endpoint, $"order {order.Id} ready");
        order.Client.Send($"READY {order.Id}");
    }
}
