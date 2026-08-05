using System.IO;

namespace SpinFourKay.App.Infrastructure;

internal static class PathLocator
{
    public static string? FindLegendsDirectory()
    {
        foreach (string candidate in EnumerateLegendsCandidates())
        {
            if (IsLegendsDirectory(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static bool IsLegendsDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }

        return File.Exists(Path.Combine(fullPath, "eqgame.exe"))
            && File.Exists(Path.Combine(fullPath, "eqclient.ini"));
    }

    public static string FindMagpieDirectory()
    {
        foreach (string candidate in EnumerateMagpieCandidates())
        {
            string executable = Path.Combine(candidate, "Magpie.exe");
            string effects = Path.Combine(candidate, "effects");
            if (File.Exists(executable) && Directory.Exists(effects))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new DirectoryNotFoundException(
            "The bundled Magpie scaling engine is missing. Re-extract the complete "
            + "SpinFOURKAYYY release, including Engine\\Magpie.");
    }

    public static string StateRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpinFOURKAYYY");

    public static string BackupRoot => Path.Combine(StateRoot, "backups");

    public static string LayoutProfileRoot =>
        Path.Combine(StateRoot, "layout-profiles");

    private static IEnumerable<string> EnumerateLegendsCandidates()
    {
        yield return Environment.CurrentDirectory;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < 6; depth++)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }

        yield return @"C:\EQLegends";
    }

    private static IEnumerable<string> EnumerateMagpieCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Engine", "Magpie");

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < 7; depth++)
        {
            yield return Path.Combine(
                directory.FullName,
                "vendor",
                "Magpie-v0.12.1-x64");
            directory = directory.Parent;
        }
    }
}
