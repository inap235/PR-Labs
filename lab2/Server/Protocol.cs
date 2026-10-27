using System.Globalization;
using System.Text;

namespace Labs.Lab2.Server;

/// Reads and writes the product lists of the messages, see lab2/PROTOCOL.md.
public static class Protocol
{
    public const int MaxQuantity = 9999;

    /// "ORDER coffee 2 tea 1" -> one quantity per product. A product written twice is summed.
    public static bool TryParseItems(string[] words, string what, out int[] quantities, out string error)
    {
        quantities = new int[Products.Count];
        error = "";
        if (words.Length == 1)
        {
            error = $"empty {what}";
            return false;
        }

        for (int i = 1; i < words.Length; i += 2)
        {
            int product = Products.Find(words[i]);
            if (product < 0)
            {
                error = $"unknown product '{words[i]}'";
                return false;
            }
            if (i + 1 == words.Length)
            {
                error = $"missing quantity for {Products.Names[product]}";
                return false;
            }
            // NumberStyles.None: digits only, no sign, no spaces, no "1e3"
            if (!int.TryParse(words[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int quantity)
                || quantity < 1 || quantity > MaxQuantity)
            {
                error = $"bad quantity '{words[i + 1]}' for {Products.Names[product]}";
                return false;
            }
            quantities[product] += quantity;
        }
        return true;
    }

    /// "coffee 43 tea 12 croissant 50"
    public static string Items(int[] counts, bool skipZero = false)
    {
        var text = new StringBuilder();
        for (int p = 0; p < Products.Count; p++)
        {
            if (skipZero && counts[p] == 0)
                continue;
            if (text.Length > 0)
                text.Append(' ');
            text.Append(Products.Names[p]).Append(' ').Append(counts[p]);
        }
        return text.ToString();
    }

    /// The short products with how many are left: "croissant 1 tea 0"
    public static string Short(int[] wanted, int[] left)
    {
        var text = new StringBuilder();
        for (int p = 0; p < Products.Count; p++)
        {
            if (wanted[p] <= left[p])
                continue;
            if (text.Length > 0)
                text.Append(' ');
            text.Append(Products.Names[p]).Append(' ').Append(left[p]);
        }
        return text.ToString();
    }

    /// "MENU coffee 4 23 tea 3 12 croissant 2 40": product, seconds, stock
    public static string Menu(int[] stock)
    {
        var text = new StringBuilder("MENU");
        for (int p = 0; p < Products.Count; p++)
            text.Append(' ').Append(Products.Names[p]).Append(' ').Append(Products.Seconds[p]).Append(' ').Append(stock[p]);
        return text.ToString();
    }

    /// {"coffee": 43, "tea": 12, "croissant": 50}, for stock.json
    public static Dictionary<string, int> Object(int[] counts)
    {
        var json = new Dictionary<string, int>();
        for (int p = 0; p < Products.Count; p++)
            json[Products.Names[p]] = counts[p];
        return json;
    }
}
