using System.Globalization;

namespace PartCombiner.Core;

public static class Bytes
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.CurrentCulture) + " B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }
}
