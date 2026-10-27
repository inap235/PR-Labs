namespace Labs.Lab2.Server;

/// The products of the shop. A product is its index in these arrays,
/// and a list of products is an int[] with one quantity per product.
public static class Products
{
    public static readonly string[] Names = ["coffee", "tea", "croissant"];
    public static readonly int[] Seconds = [4, 3, 2];

    public static int Count => Names.Length;

    /// Index of the product, or -1. Not case sensitive.
    public static int Find(string name) =>
        Array.FindIndex(Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// 2 croissants, 1 tea and 1 coffee: 2*2 + 3 + 4 = 11 seconds.
    public static int PreparationSeconds(int[] quantities)
    {
        int seconds = 0;
        for (int p = 0; p < Count; p++)
            seconds += quantities[p] * Seconds[p];
        return seconds;
    }
}
