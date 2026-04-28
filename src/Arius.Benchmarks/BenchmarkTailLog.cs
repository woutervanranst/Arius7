using BenchmarkDotNet.Reports;

namespace Arius.Benchmarks;

internal static class BenchmarkTailLog
{
    public static void Append(string path, BenchmarkTailLogEntry entry, Summary summary)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(summary);

        var values = ExtractSummaryValues(summary);
        var lineValues = new[]
        {
            entry.ComputerName,
            entry.DateTimeUtc,
            entry.GitHead,
            entry.RepresentativeScaleDivisor.ToString(),
            entry.Iterations.ToString(),
            values.Get("Mean"),
            values.Get("Error"),
            values.Get("StdDev"),
            values.Get("Gen 0", "Gen0"),
            values.Get("Gen 1", "Gen1"),
            values.Get("Gen 2", "Gen2"),
            values.Get("Allocated"),
            values.Get("Completed Work Items"),
            values.Get("Lock Contentions"),
            entry.RawOutputPath,
        };

        using var writer = new StreamWriter(path, append: true);

        writer.WriteLine(ToPipeDelimitedLine(lineValues));
    }

    static BenchmarkSummaryValues ExtractSummaryValues(Summary summary)
    {
        if (summary.Table.FullContent.Length == 0)
            throw new InvalidOperationException("BenchmarkDotNet did not produce a summary row.");

        var headers = summary.Table.FullHeader;
        var values = summary.Table.FullContent[0];

        return new(headers, values);
    }

    static string ToPipeDelimitedLine(IEnumerable<string> values)
        => "| " + string.Join(" | ", values.Select(EscapePipeDelimitedValue)) + " |";

    static string EscapePipeDelimitedValue(string value)
        => value.ReplaceLineEndings(" ").Replace("|", "\\|");
}

internal sealed record BenchmarkTailLogEntry(
    string ComputerName,
    string DateTimeUtc,
    string GitHead,
    int RepresentativeScaleDivisor,
    int Iterations,
    string RawOutputPath);

internal sealed class BenchmarkSummaryValues(IReadOnlyList<string> headers, IReadOnlyList<string> values)
{
    public string Get(params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (string.Equals(Normalize(headers[i]), Normalize(alias), StringComparison.OrdinalIgnoreCase))
                    return values[i];
            }
        }

        return "";
    }

    static string Normalize(string value)
        => value.Replace(" ", "", StringComparison.Ordinal)
            .Replace("/", "", StringComparison.Ordinal);
}
