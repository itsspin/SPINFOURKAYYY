using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SpinFourKay.App.Infrastructure;

internal enum SpinUiDetectionStatus
{
    NotDetected,
    Detected,
    Uncertain,
}

internal sealed record SpinUiLayoutFingerprint(
    string Path,
    long Length,
    string Sha256);

internal sealed record SpinUiDetectionResult(
    SpinUiDetectionStatus Status,
    bool ThemeDirectoryPresent,
    IReadOnlyList<SpinUiLayoutFingerprint> MatchingLayouts,
    string? Issue)
{
    public bool IsDetected => Status == SpinUiDetectionStatus.Detected;

    public bool IsReliable => Status != SpinUiDetectionStatus.Uncertain;

    public IReadOnlyList<string> MatchingLayoutFiles =>
        MatchingLayouts.Select(layout => layout.Path).ToArray();
}

/// <summary>
/// Performs read-only SpinUI detection. A positive result requires both the
/// spinui_reloaded theme folder and a root UI_*_LO*.ini whose [Main] section
/// selects that skin.
/// </summary>
internal static class SpinUiDetector
{
    private const string SpinUiSkinName = "spinui_reloaded";
    private const long MaximumLayoutBytes = 16 * 1024 * 1024;
    private static readonly Regex LayoutFileNamePattern = new(
        @"^UI_.+_LO\d+\.ini$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static SpinUiDetectionResult Detect(string eqDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eqDirectory);
        string root = Path.GetFullPath(eqDirectory);
        string themeDirectory = Path.Combine(root, "uifiles", SpinUiSkinName);
        if (!Directory.Exists(themeDirectory))
        {
            return new SpinUiDetectionResult(
                SpinUiDetectionStatus.NotDetected,
                ThemeDirectoryPresent: false,
                MatchingLayouts: [],
                Issue: null);
        }

        List<SpinUiLayoutFingerprint> matches = [];
        List<string> readFailures = [];
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory
                .EnumerateFiles(root, "UI_*.ini", SearchOption.TopDirectoryOnly)
                .Where(IsLayoutProfileName)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return new SpinUiDetectionResult(
                SpinUiDetectionStatus.Uncertain,
                ThemeDirectoryPresent: true,
                MatchingLayouts: [],
                Issue: "SpinUI assets are present, but root UI layout profiles could "
                    + "not be enumerated: "
                    + exception.Message);
        }

        foreach (string candidate in candidates)
        {
            try
            {
                SpinUiLayoutFingerprint? match = InspectLayout(candidate);
                if (match is not null)
                {
                    matches.Add(match);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                readFailures.Add($"{Path.GetFileName(candidate)}: {exception.Message}");
            }
        }

        if (matches.Count > 0)
        {
            return new SpinUiDetectionResult(
                SpinUiDetectionStatus.Detected,
                ThemeDirectoryPresent: true,
                MatchingLayouts: matches,
                Issue: readFailures.Count == 0
                    ? null
                    : "Some additional layout profiles could not be inspected.");
        }

        if (readFailures.Count > 0)
        {
            return new SpinUiDetectionResult(
                SpinUiDetectionStatus.Uncertain,
                ThemeDirectoryPresent: true,
                MatchingLayouts: [],
                Issue: "SpinUI assets are present, but layout-profile detection could "
                    + "not finish reliably: "
                    + string.Join(" ", readFailures));
        }

        return new SpinUiDetectionResult(
            SpinUiDetectionStatus.NotDetected,
            ThemeDirectoryPresent: true,
            MatchingLayouts: [],
            Issue: null);
    }

    private static bool IsLayoutProfileName(string path)
    {
        return LayoutFileNamePattern.IsMatch(Path.GetFileName(path));
    }

    private static SpinUiLayoutFingerprint? InspectLayout(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long initialLength = stream.Length;
        DateTime initialWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        if (initialLength > MaximumLayoutBytes)
        {
            throw new IOException(
                $"{Path.GetFileName(path)} exceeds the safe layout-inspection limit.");
        }

        using MemoryStream snapshot = new(
            capacity: checked((int)Math.Max(0, initialLength)));
        stream.CopyTo(snapshot);
        byte[] data = snapshot.ToArray();
        if (data.LongLength != initialLength
            || stream.Length != initialLength
            || File.GetLastWriteTimeUtc(path) != initialWriteTimeUtc)
        {
            throw new IOException(
                $"{Path.GetFileName(path)} changed while it was being inspected.");
        }

        if (!UsesSpinUiInMainSection(data))
        {
            return null;
        }

        return new SpinUiLayoutFingerprint(
            Path.GetFullPath(path),
            data.LongLength,
            Convert.ToHexString(SHA256.HashData(data)));
    }

    private static bool UsesSpinUiInMainSection(byte[] data)
    {
        using MemoryStream stream = new(data, writable: false);
        using StreamReader reader = new(
            stream,
            detectEncodingFromByteOrderMarks: true);

        bool inMainSection = false;
        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0
                || trimmed.StartsWith(';')
                || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                string section = trimmed[1..^1].Trim();
                inMainSection = string.Equals(
                    section,
                    "Main",
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inMainSection)
            {
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = trimmed[..separator].Trim();
            string value = trimmed[(separator + 1)..].Trim();
            if (string.Equals(key, "UISkin", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(
                    value,
                    SpinUiSkinName,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}
