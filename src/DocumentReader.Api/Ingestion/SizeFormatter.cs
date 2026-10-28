using System.Globalization;

namespace DocumentReader.Api.Ingestion;

/// <summary>Formats byte counts for user-facing messages.</summary>
public static class SizeFormatter
{
    public static string Format(long bytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;

        if (bytes >= megabyte)
        {
            return (bytes / megabyte).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        if (bytes >= kilobyte)
        {
            return (bytes / kilobyte).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
    }
}
