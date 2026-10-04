namespace Labs.Lab1.Common;

/// <summary>How many negative, zero and positive numbers were seen.</summary>
public struct Counts(long negative, long zero, long positive)
{
    public long Negative = negative;
    public long Zero = zero;
    public long Positive = positive;

    public readonly long Total => Negative + Zero + Positive;

    public void Add(Counts other)
    {
        Negative += other.Negative;
        Zero += other.Zero;
        Positive += other.Positive;
    }

    public readonly bool SameAs(Counts other) =>
        Negative == other.Negative && Zero == other.Zero && Positive == other.Positive;

    public static Counts Sum(IEnumerable<Counts> parts)
    {
        Counts total = default;
        foreach (var part in parts)
            total.Add(part);
        return total;
    }

    public override readonly string ToString() =>
        $"negative={Negative:N0} zero={Zero:N0} positive={Positive:N0} total={Total:N0}";
}
