namespace SpinFourKay.Core.Magpie;

/// <summary>
/// Defines the immutable Magpie files that SpinFOURKAYYY's supported quality
/// paths require before the engine can be started.
/// </summary>
public static class MagpieRuntimeAssets
{
    private static readonly string[] RequiredFiles =
    [
        "Magpie.exe",
        "Microsoft.UI.Xaml.dll",
        "resources.pri",
        Path.Combine("effects", "FSR", "FSR_EASU.hlsl"),
        Path.Combine("effects", "FSR", "FSR_RCAS.hlsl"),
        Path.Combine("effects", "Lanczos.hlsl"),
        Path.Combine("effects", "Nearest.hlsl"),
        Path.Combine("effects", "FXAA", "FXAA.hlsli"),
        Path.Combine("effects", "FXAA", "FXAA_High.hlsl"),
        Path.Combine("effects", "SMAA", "SMAA.hlsli"),
        Path.Combine("effects", "SMAA", "SMAA_High.hlsl"),
        Path.Combine("effects", "SMAA", "AreaTex.dds"),
        Path.Combine("effects", "SMAA", "SearchTex.dds"),
    ];

    public static IReadOnlyList<string> RequiredRelativePaths => RequiredFiles;

    public static IReadOnlyList<string> FindMissing(string magpieDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(magpieDirectory);
        string fullDirectory = Path.GetFullPath(magpieDirectory);
        return RequiredFiles
            .Where(relative => !File.Exists(Path.Combine(fullDirectory, relative)))
            .ToArray();
    }

    public static bool IsComplete(string magpieDirectory) =>
        !string.IsNullOrWhiteSpace(magpieDirectory)
        && Directory.Exists(magpieDirectory)
        && FindMissing(magpieDirectory).Count == 0;

    public static void EnsureComplete(string magpieDirectory)
    {
        IReadOnlyList<string> missing = FindMissing(magpieDirectory);
        if (missing.Count == 0)
        {
            return;
        }

        throw new InvalidDataException(
            "The dedicated Magpie runtime is incomplete, so fullscreen scaling "
                + "was not started. Missing required file"
                + (missing.Count == 1 ? ": " : "s: ")
                + string.Join(", ", missing)
                + ". Re-extract the complete SpinFOURKAYYY release and try again.");
    }
}

/// <summary>
/// Copies the bundled engine into an app-version-specific LocalAppData runtime.
/// This keeps a running session independent from a download, build, or extracted
/// release directory that might be replaced while Magpie is compiling shaders.
/// </summary>
public static class MagpieRuntimeProvisioner
{
    private static readonly HashSet<string> MutableRootDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cache",
            "config",
            "logs",
        };

    private static readonly object ProvisionLock = new();

    public static string Prepare(
        string bundledDirectory,
        string runtimeRoot,
        string runtimeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ValidateRuntimeKey(runtimeKey);

        string source = Path.GetFullPath(bundledDirectory);
        string root = Path.GetFullPath(runtimeRoot);
        string destination = Path.Combine(root, runtimeKey);

        lock (ProvisionLock)
        {
            if (MagpieRuntimeAssets.IsComplete(destination))
            {
                return destination;
            }

            MagpieRuntimeAssets.EnsureComplete(source);
            Directory.CreateDirectory(root);

            string nonce = Guid.NewGuid().ToString("N");
            string pending = Path.Combine(root, $".{runtimeKey}.pending-{nonce}");
            string displaced = Path.Combine(root, $".{runtimeKey}.broken-{nonce}");
            bool displacedExisting = false;
            try
            {
                CopyImmutableRuntime(source, pending);
                MagpieRuntimeAssets.EnsureComplete(pending);

                if (Directory.Exists(destination))
                {
                    Directory.Move(destination, displaced);
                    displacedExisting = true;
                }

                try
                {
                    Directory.Move(pending, destination);
                }
                catch
                {
                    if (displacedExisting && !Directory.Exists(destination))
                    {
                        Directory.Move(displaced, destination);
                        displacedExisting = false;
                    }

                    throw;
                }

                MagpieRuntimeAssets.EnsureComplete(destination);
                if (displacedExisting)
                {
                    TryDeleteDirectory(displaced);
                }

                return destination;
            }
            finally
            {
                if (Directory.Exists(pending))
                {
                    TryDeleteDirectory(pending);
                }
            }
        }
    }

    private static void CopyImmutableRuntime(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        CopyDirectoryContents(source, destination, isRoot: true);
    }

    private static void CopyDirectoryContents(
        string source,
        string destination,
        bool isRoot)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(source))
        {
            File.Copy(
                sourceFile,
                Path.Combine(destination, Path.GetFileName(sourceFile)),
                overwrite: false);
        }

        foreach (string sourceDirectory in Directory.EnumerateDirectories(source))
        {
            DirectoryInfo directory = new(sourceDirectory);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The bundled Magpie runtime contains an unsupported link: "
                        + directory.FullName);
            }

            if (isRoot && MutableRootDirectories.Contains(directory.Name))
            {
                continue;
            }

            string childDestination = Path.Combine(destination, directory.Name);
            Directory.CreateDirectory(childDestination);
            CopyDirectoryContents(
                directory.FullName,
                childDestination,
                isRoot: false);
        }
    }

    private static void ValidateRuntimeKey(string runtimeKey)
    {
        if (string.IsNullOrWhiteSpace(runtimeKey)
            || runtimeKey is "." or ".."
            || runtimeKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || runtimeKey.Contains(Path.DirectorySeparatorChar)
            || runtimeKey.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "The Magpie runtime key must be one safe directory name.",
                nameof(runtimeKey));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // The complete destination is already active. A stale quarantine can
            // be retried by normal user cleanup without invalidating this runtime.
        }
        catch (UnauthorizedAccessException)
        {
            // Antivirus scanners may transiently retain copied files. Do not turn
            // successful atomic provisioning into a launch failure for cleanup.
        }
    }
}
