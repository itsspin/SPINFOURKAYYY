using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SpinFourKay.Core.Display;

namespace SpinFourKay.Core.Layouts;

/// <summary>
/// Converts the generic outer window geometry used by EverQuest UI layout INIs.
/// Percent-based anchors already map to the same physical display position after
/// whole-frame scaling, so only fixed pixel widths and heights are transformed.
/// Every unknown line and every non-ASCII byte is retained byte-for-byte.
/// </summary>
public static partial class UiLayoutTransformer
{
    [GeneratedRegex(
        "^(?<prefix>[ \\t]*(?<key>Width|Height)[ \\t]*=[ \\t]*)(?<value>[0-9]+)(?<suffix>[ \\t]*\\r?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex GeometryLinePattern();

    public static UiLayoutTransformResult Transform(
        ReadOnlyMemory<byte> content,
        PixelSize fromResolution,
        PixelSize toResolution)
    {
        ValidateResolution(fromResolution, nameof(fromResolution));
        ValidateResolution(toResolution, nameof(toResolution));

        string source = Encoding.Latin1.GetString(content.Span);
        int widthCount = 0;
        int heightCount = 0;
        string transformed = GeometryLinePattern().Replace(
            source,
            match =>
            {
                bool isWidth = string.Equals(
                    match.Groups["key"].Value,
                    "Width",
                    StringComparison.OrdinalIgnoreCase);
                if (!long.TryParse(
                        match.Groups["value"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out long value))
                {
                    return match.Value;
                }

                double ratio = isWidth
                    ? (double)toResolution.Width / fromResolution.Width
                    : (double)toResolution.Height / fromResolution.Height;
                long scaled = value == 0
                    ? 0
                    : Math.Max(
                        1,
                        checked((long)Math.Round(
                            value * ratio,
                            MidpointRounding.AwayFromZero)));
                if (scaled > int.MaxValue)
                {
                    throw new InvalidDataException(
                        $"A UI layout {match.Groups["key"].Value} value became too large.");
                }

                if (isWidth)
                {
                    widthCount++;
                }
                else
                {
                    heightCount++;
                }

                return match.Groups["prefix"].Value
                    + scaled.ToString(CultureInfo.InvariantCulture)
                    + match.Groups["suffix"].Value;
            });

        return new UiLayoutTransformResult(
            Encoding.Latin1.GetBytes(transformed),
            widthCount,
            heightCount);
    }

    private static void ValidateResolution(PixelSize resolution, string parameterName)
    {
        if (resolution.Width <= 0 || resolution.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A UI layout resolution must contain positive pixels.");
        }
    }
}
