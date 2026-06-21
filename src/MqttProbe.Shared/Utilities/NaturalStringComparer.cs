namespace MqttProbe.Utilities;

public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            int result;
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
                result = CompareDigits(x, y, ref ix, ref iy);
            else
                result = CompareLetters(x, y, ref ix, ref iy);

            if (result != 0) return result;
        }
        return (x.Length - ix).CompareTo(y.Length - iy);
    }

    private static int CompareDigits(string x, string y, ref int ix, ref int iy)
    {
        long nx = 0, ny = 0;
        while (ix < x.Length && char.IsDigit(x[ix]))
            nx = nx * 10 + (x[ix++] - '0');
        while (iy < y.Length && char.IsDigit(y[iy]))
            ny = ny * 10 + (y[iy++] - '0');
        return nx.CompareTo(ny);
    }

    private static int CompareLetters(string x, string y, ref int ix, ref int iy)
    {
        var cx = char.ToUpperInvariant(x[ix]);
        var cy = char.ToUpperInvariant(y[iy]);
        if (cx != cy) return cx.CompareTo(cy);
        ix++;
        iy++;
        return 0;
    }
}
