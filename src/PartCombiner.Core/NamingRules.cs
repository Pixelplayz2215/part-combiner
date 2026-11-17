using System.Globalization;
using System.Text.RegularExpressions;

namespace PartCombiner.Core;

/// <summary>Recognizes part file names and turns them into (base name, scheme, index).</summary>
internal static partial class NamingRules
{
    internal readonly record struct ParsedName(string Base, NamingScheme Scheme, int Index, int Padding);

    /// <summary>Index given to the trailing .zip of a spanned ZIP set so it sorts last.</summary>
    internal const int ZipLastIndex = int.MaxValue;

    // Part numbers can have up to 9 digits (999,999,999 parts) — effectively no limit.

    [GeneratedRegex(@"^(?<base>.+)\.part(?<n>\d{1,9})\.(?:rar|exe)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RarPartRx();

    [GeneratedRegex(@"^(?<base>.+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RarFirstRx();

    // Old-style RAR: .r00–.r99, then .s00–.s99, .t00 … .y99 (.z is taken by spanned ZIP).
    [GeneratedRegex(@"^(?<base>.+)\.(?<l>[r-y])(?<n>\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RarNextRx();

    [GeneratedRegex(@"^(?<base>.+)\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZipLastRx();

    [GeneratedRegex(@"^(?<base>.+)\.z(?<n>\d{2,9})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZipNextRx();

    [GeneratedRegex(@"^(?<base>.+)\.(?<n>\d{3,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedRx();

    [GeneratedRegex(@"^(?<base>.+)\.part(?<n>\d{1,9})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartSuffixRx();

    // Unix split: .aa, .ab … (or longer suffixes from "split -a 3" → .aaa, .aab …).
    [GeneratedRegex(@"^(?<base>.+)\.(?<s>[a-z]{2,6})$", RegexOptions.CultureInvariant)]
    private static partial Regex AlphabeticRx();

    public static bool TryParse(string fileName, out ParsedName parsed)
    {
        Match m;
        if ((m = RarPartRx().Match(fileName)).Success)
        {
            parsed = new(m.Groups["base"].Value, NamingScheme.RarPart, Int(m, "n"), m.Groups["n"].Length);
            return true;
        }
        if ((m = RarFirstRx().Match(fileName)).Success)
        {
            parsed = new(m.Groups["base"].Value, NamingScheme.RarLegacy, 0, 0);
            return true;
        }
        if ((m = RarNextRx().Match(fileName)).Success)
        {
            int letter = char.ToLowerInvariant(m.Groups["l"].Value[0]) - 'r';
            parsed = new(m.Groups["base"].Value, NamingScheme.RarLegacy, 1 + letter * 100 + Int(m, "n"), 2);
            return true;
        }
        if ((m = ZipLastRx().Match(fileName)).Success)
        {
            parsed = new(m.Groups["base"].Value, NamingScheme.ZipSpan, ZipLastIndex, 0);
            return true;
        }
        if ((m = ZipNextRx().Match(fileName)).Success)
        {
            parsed = new(m.Groups["base"].Value, NamingScheme.ZipSpan, Int(m, "n"), m.Groups["n"].Length);
            return true;
        }
        if ((m = NumberedRx().Match(fileName)).Success)
        {
            parsed = new(m.Groups["base"].Value, NamingScheme.Numbered, Int(m, "n"), m.Groups["n"].Length);
            return true;
        }
        if ((m = PartSuffixRx().Match(fileName)).Success)
        {
            parsed = new(m.Groups["base"].Value, NamingScheme.PartSuffix, Int(m, "n"), m.Groups["n"].Length);
            return true;
        }
        if ((m = AlphabeticRx().Match(fileName)).Success)
        {
            var s = m.Groups["s"].Value;
            int index = 0;
            foreach (var c in s) index = index * 26 + (c - 'a');
            parsed = new(m.Groups["base"].Value, NamingScheme.Alphabetic, index, s.Length);
            return true;
        }

        parsed = default;
        return false;
    }

    public static string Label(NamingScheme scheme, int index, int padding) => scheme switch
    {
        NamingScheme.RarPart => $"part {index}",
        NamingScheme.RarLegacy => index == 0
            ? ".rar"
            : "." + (char)('r' + (index - 1) / 100) + ((index - 1) % 100).ToString("00", CultureInfo.InvariantCulture),
        NamingScheme.ZipSpan => index == ZipLastIndex ? ".zip" : ".z" + index.ToString("00", CultureInfo.InvariantCulture),
        NamingScheme.Numbered => "." + index.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(padding, 3), '0'),
        NamingScheme.PartSuffix => $"part {index}",
        NamingScheme.Alphabetic => "." + Letters(index, Math.Max(padding, 2)),
        _ => index.ToString(CultureInfo.InvariantCulture),
    };

    private static string Letters(int index, int length)
    {
        var chars = new char[length];
        for (int i = length - 1; i >= 0; i--)
        {
            chars[i] = (char)('a' + index % 26);
            index /= 26;
        }
        return new string(chars);
    }

    /// <summary>
    /// True when a part number is written consistently with the rest of its set: either padded to the set's
    /// width (part01 … part12) or naturally longer than it (… .999, .1000). "part1" next to "part02" is not.
    /// </summary>
    public static bool HasConsistentPadding(string digits, int setWidth) =>
        digits.Length == setWidth || (digits.Length > setWidth && digits[0] != '0');

    private static int Int(Match m, string group) =>
        int.Parse(m.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
}
