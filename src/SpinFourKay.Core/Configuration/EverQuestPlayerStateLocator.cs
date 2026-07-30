namespace SpinFourKay.Core.Configuration;

/// <summary>
/// Identifies the small, mutable files that contain a Legends player's client
/// settings, character hotbuttons/socials, UI window layout, and userdata.
/// Static game assets, patcher state, logs, and UI skin assets are deliberately
/// outside this set.
/// </summary>
public static class EverQuestPlayerStateLocator
{
    public const int CurrentProtectionVersion = 1;

    private static readonly HashSet<string> ExactRootFileNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "_characters.ini",
            "eqclient.ini",
            "eqlsPlayerData.ini",
            "notes.txt",
        };

    private static readonly HashSet<string> UserDataExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".ini",
            ".txt",
        };

    public static IReadOnlyList<string> FindExistingFiles(string eqDirectory)
    {
        return FindExistingFilesCore(
            eqDirectory,
            requireEqClientIni: true);
    }

    public static IReadOnlyList<string> FindExistingFilesForRecovery(
        string eqDirectory)
    {
        return FindExistingFilesCore(
            eqDirectory,
            requireEqClientIni: false);
    }

    private static string[] FindExistingFilesCore(
        string eqDirectory,
        bool requireEqClientIni)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eqDirectory);
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(eqDirectory));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"The EverQuest Legends directory was not found: '{root}'.");
        }

        string eqClientIni = Path.Combine(root, "eqclient.ini");
        if (requireEqClientIni
            && (!File.Exists(eqClientIni)
                || IsReparsePoint(eqClientIni)))
        {
            throw new FileNotFoundException(
                "The EverQuest client configuration was not found as a regular file.",
                eqClientIni);
        }

        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.TopDirectoryOnly))
        {
            if (!IsReparsePoint(path)
                && IsSupportedProtectedPath(root, path))
            {
                files.Add(Path.GetFullPath(path));
            }
        }

        foreach (string relativeDirectory in new[] { "userdata", "AudioTriggers" })
        {
            string directory = Path.Combine(root, relativeDirectory);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in EnumerateFilesWithoutReparsePoints(
                directory))
            {
                if (IsSupportedProtectedPath(root, path))
                {
                    files.Add(Path.GetFullPath(path));
                }
            }
        }

        if (File.Exists(eqClientIni)
            && !IsReparsePoint(eqClientIni))
        {
            files.Add(Path.GetFullPath(eqClientIni));
        }

        return files
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsSupportedProtectedPath(
        string eqDirectory,
        string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(eqDirectory)
            || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string root;
        string candidate;
        try
        {
            root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(eqDirectory));
            candidate = Path.GetFullPath(candidatePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }

        string prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relative = Path.GetRelativePath(root, candidate);
        if (relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return false;
        }

        string[] segments = relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1)
        {
            string fileName = segments[0];
            if (ExactRootFileNames.Contains(fileName))
            {
                return true;
            }

            if (!string.Equals(
                    Path.GetExtension(fileName),
                    ".ini",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return fileName.StartsWith(
                    "UI_",
                    StringComparison.OrdinalIgnoreCase)
                || fileName.Count(character => character == '_') >= 2;
        }

        if (!string.Equals(
                segments[0],
                "userdata",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                segments[0],
                "AudioTriggers",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return UserDataExtensions.Contains(
            Path.GetExtension(segments[^1]));
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(
        string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(
                directory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                if (!IsReparsePoint(file))
                {
                    yield return file;
                }
            }

            foreach (string child in Directory.EnumerateDirectories(
                directory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                if (!IsReparsePoint(child))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
