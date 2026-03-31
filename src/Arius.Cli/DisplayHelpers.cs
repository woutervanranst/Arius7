using Humanizer;
using System.Globalization;

namespace Arius.Cli;

/// <summary>
/// Shared rendering utilities used by both <see cref="Archive.ArchiveVerb"/> and <see cref="Restore.RestoreVerb"/>.
/// </summary>
internal static class DisplayHelpers
{
    /// <summary>
    /// Splits a (current, total) byte pair into aligned string values sharing the unit of <paramref name="total"/>.
    /// Returns (currentValueStr, totalValueStr, unitSymbol) — both values formatted to up to 2 decimal places.
    /// </summary>
    internal static (string current, string total, string unit) SplitSizePair(long current, long total)
    {
        var totalInfo  = total.Bytes();
        var unit       = totalInfo.LargestWholeNumberSymbol;
        var divisor    = total > 0 ? (double)total / totalInfo.LargestWholeNumberValue : 1.0;
        var currentVal = divisor > 0 ? current / divisor : 0.0;
        var totalVal   = totalInfo.LargestWholeNumberValue;
        return (currentVal.ToString("0.00", CultureInfo.InvariantCulture),
                totalVal  .ToString("0.00", CultureInfo.InvariantCulture),
                unit);
    }

    /// <summary>
    /// Renders a progress bar as a Markup string with the given fill ratio and character width.
    /// Filled characters use [green]█[/] and empty characters use [dim]░[/].
    /// </summary>
    /// <param name="fraction">Fill ratio in [0.0, 1.0].</param>
    /// <param name="width">Total bar width in characters.</param>
    internal static string RenderProgressBar(double fraction, int width)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        var filled = (int)Math.Round(fraction * width);
        filled = Math.Clamp(filled, 0, width);
        var empty  = width - filled;
        return $"[green]{new string('█', filled)}[/][dim]{new string('░', empty)}[/]";
    }

    /// <summary>
    /// Truncates <paramref name="input"/> to <paramref name="width"/> characters and left-justifies it.
    /// If the input is longer than <paramref name="width"/>, the result is
    /// <c>"..." + input[last (width-3) chars]</c> — preserving the deepest part of the path.
    /// The result is always exactly <paramref name="width"/> characters wide.
    /// </summary>
    internal static string TruncateAndLeftJustify(string input, int width)
    {
        if (width <= 0)
            return string.Empty;
        if (width <= 3)
            return input.Length <= width ? input.PadRight(width) : new string('.', width);
        if (input.Length <= width)
            return input.PadRight(width);
        return ("..." + input[^(width - 3)..]).PadRight(width);
    }
}
