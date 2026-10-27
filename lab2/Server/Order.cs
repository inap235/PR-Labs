namespace Labs.Lab2.Server;

/// An accepted order; its products are already taken from the stock.
public sealed class Order(int id, int[] quantities, int seconds, Connection client)
{
    public int Id { get; } = id;
    public int[] Quantities { get; } = quantities;
    public int Seconds { get; } = seconds;
    public Connection Client { get; } = client;

    /// Set after ACCEPTED is sent; the worker waits for it before starting.
    public ManualResetEventSlim Announced { get; } = new();

    /// Set by Kitchen.Cancel when the client leaves during preparation.
    public ManualResetEventSlim Cancelled { get; } = new();
}
