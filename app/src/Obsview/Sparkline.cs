using System.Globalization;
using System.Text;

namespace Obsview;

/// <summary>
/// Inline SVG sparklines for the stat tiles, drawn server-side. A tile is too
/// small for a chart library to earn its keep, and a path string in the HTML
/// paints with the first byte rather than after a script runs.
/// </summary>
public static class Sparkline
{
    public const int Width = 120;
    public const int Height = 32;
    private const int Pad = 2;

    /// <summary>
    /// An SVG path <c>d</c> through <paramref name="values"/>, left to right,
    /// with gaps where a value is null. Empty when there are fewer than two
    /// points to draw a line between. A flat series sits on the middle line
    /// rather than the floor, so "always 3" does not read as "always 0".
    /// </summary>
    public static string Path(IReadOnlyList<double?> values, int width = Width, int height = Height)
    {
        ArgumentNullException.ThrowIfNull(values);
        var points = values
            .Select((v, i) => (i, v))
            .Where(p => p.v is not null)
            .Select(p => (p.i, v: p.v!.Value))
            .ToList();
        if (points.Count < 2 || values.Count < 2)
        {
            return "";
        }

        var lo = points.Min(p => p.v);
        var hi = points.Max(p => p.v);
        var span = hi - lo;
        var step = (width - (2.0 * Pad)) / (values.Count - 1);

        double Y(double v) => span == 0 ? height / 2.0 : Pad + ((height - (2.0 * Pad)) * (1 - ((v - lo) / span)));

        var path = new StringBuilder();
        int? previous = null;
        foreach (var (i, v) in points)
        {
            var command = previous is not null && i == previous + 1 ? 'L' : 'M';
            if (path.Length > 0)
            {
                path.Append(' ');
            }

            path.Append(command)
                .Append(Fmt.Fixed(Pad + (i * step), 1))
                .Append(',')
                .Append(Fmt.Fixed(Y(v), 1));
            previous = i;
        }

        return path.ToString();
    }

    public static string Path(IEnumerable<double> values) => Path(values.Select(v => (double?)v).ToList());
}

/// <summary>
/// Number formats as the pages write them, culture-invariant, matching what
/// the Python app printed (so links, figures and tests read the same).
/// </summary>
public static class Fmt
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A fixed number of decimals, half to even like Python's format.</summary>
    public static string Fixed(double value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.ToEven).ToString("F" + decimals.ToString(Invariant), Invariant);

    /// <summary>Thousands separators and a fixed number of decimals: <c>1,234.50</c>.</summary>
    public static string Grouped(double value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.ToEven).ToString("N" + decimals.ToString(Invariant), Invariant);

    /// <summary>An integer with thousands separators.</summary>
    public static string Count(long value) => value.ToString("N0", Invariant);

    /// <summary>An integer with its sign: <c>+3</c>, <c>0</c> as <c>+0</c>, <c>-2</c>.</summary>
    public static string WithSign(long value) => (value >= 0 ? "+" : "") + value.ToString(Invariant);

    /// <summary>A signed fixed-point number: <c>+1.5</c>, <c>-0.3</c>.</summary>
    public static string WithSign(double value, int decimals)
    {
        var text = Fixed(value, decimals);
        return text.StartsWith('-') ? text : "+" + text;
    }

    /// <summary><c>s</c> when a count isn't one: "3 deploys", "1 deploy".</summary>
    public static string S(long count) => count == 1 ? "" : "s";
}
