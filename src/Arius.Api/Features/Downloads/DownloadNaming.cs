using System.Globalization;

namespace Arius.Api.Downloads;

/// <summary>Names the ZIP a browser download is served as.</summary>
internal static class DownloadNaming
{
    /// <summary>
    /// <c>{alias}-{yyyy-MM-dd}.zip</c> for everything, <c>… (k of n).zip</c> for a partial set,
    /// <c>… (remaining).zip</c> for the files left after a first download. The date is the job's start date.
    /// </summary>
    public static string ArchiveName(string alias, DateTimeOffset startedAt, int selected, int total, bool remaining)
    {
        var date = startedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (remaining)
            return $"{alias}-{date} (remaining).zip";

        return selected < total
            ? $"{alias}-{date} ({selected} of {total}).zip"
            : $"{alias}-{date}.zip";
    }
}
