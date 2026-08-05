using System.Diagnostics;
using SpinFourKay.Core.Windows;

namespace SpinFourKay.Core.Magpie;

public sealed class MagpieProcessService : IMagpieProcessService
{
    private const string MagpieQuitMessageName = "WM_MAGPIE_QUIT";

    public IReadOnlyList<MagpieRunningInstance> InspectRunningInstances(string magpieDirectory)
    {
        string executablePath = ValidateMagpieDirectory(magpieDirectory);
        List<MagpieRunningInstance> instances = [];

        foreach (Process process in Process.GetProcessesByName("Magpie"))
        {
            using (process)
            {
                string? runningPath = ProcessDiscoveryService.TryGetExecutablePath(process.Id);
                instances.Add(new MagpieRunningInstance(
                    process.Id,
                    runningPath,
                    string.Equals(
                        runningPath,
                        executablePath,
                        StringComparison.OrdinalIgnoreCase)));
            }
        }

        return instances;
    }

    public Process? TryFindRunning(string magpieDirectory)
    {
        MagpieRunningInstance? bundled = InspectRunningInstances(magpieDirectory)
            .FirstOrDefault(instance => instance.IsBundledInstance);
        if (bundled is null)
        {
            return null;
        }

        try
        {
            return Process.GetProcessById(bundled.ProcessId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public MagpieProcessStartResult StartPortable(
        string magpieDirectory,
        bool startInTray = true)
    {
        string executablePath = ValidateMagpieDirectory(magpieDirectory);
        string fullDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("Magpie.exe has no parent directory.");
        MagpieRuntimeAssets.EnsureComplete(fullDirectory);
        string configPath = Path.Combine(fullDirectory, "config", "config.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "Portable Magpie config was not generated before launch.",
                configPath);
        }

        IReadOnlyList<MagpieRunningInstance> instances = InspectRunningInstances(fullDirectory);
        MagpieRunningInstance[] external = instances
            .Where(instance => !instance.IsBundledInstance)
            .ToArray();
        if (external.Length > 0)
        {
            throw new ExternalMagpieInstanceConflictException(external);
        }

        MagpieRunningInstance? bundled = instances.FirstOrDefault(
            instance => instance.IsBundledInstance);
        if (bundled is not null)
        {
            try
            {
                return new MagpieProcessStartResult(
                    Process.GetProcessById(bundled.ProcessId),
                    AlreadyRunning: true);
            }
            catch (ArgumentException)
            {
                // It exited between inspection and acquisition; start our copy below.
            }
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            WorkingDirectory = fullDirectory,
            UseShellExecute = false,
        };
        if (startInTray)
        {
            startInfo.ArgumentList.Add("-t");
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start Magpie.");

        if (process.WaitForExit(milliseconds: 500))
        {
            process.Dispose();
            MagpieRunningInstance[] conflicts = InspectRunningInstances(fullDirectory)
                .Where(instance => !instance.IsBundledInstance)
                .ToArray();
            if (conflicts.Length > 0)
            {
                throw new ExternalMagpieInstanceConflictException(conflicts);
            }

            throw new InvalidOperationException(
                "Magpie exited before the portable profile became active.");
        }

        return new MagpieProcessStartResult(process, AlreadyRunning: false);
    }

    public async Task<bool> ShutdownBundledAsync(
        string magpieDirectory,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        IReadOnlyList<MagpieRunningInstance> instances =
            InspectRunningInstances(magpieDirectory);
        MagpieRunningInstance[] external = instances
            .Where(instance => !instance.IsBundledInstance)
            .ToArray();
        if (external.Length > 0)
        {
            throw new ExternalMagpieInstanceConflictException(external);
        }

        MagpieRunningInstance? bundled = instances.FirstOrDefault(
            instance => instance.IsBundledInstance);
        if (bundled is null)
        {
            return true;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(bundled.ProcessId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            uint quitMessage = NativeMethods.RegisterWindowMessageW(MagpieQuitMessageName);
            if (quitMessage == 0
                || !NativeMethods.PostMessageW(
                    NativeMethods.HwndBroadcast,
                    quitMessage,
                    nint.Zero,
                    nint.Zero))
            {
                return false;
            }

            using CancellationTokenSource timeoutCancellation =
                new(effectiveTimeout);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCancellation.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested
                && timeoutCancellation.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    public async Task<bool> ShutdownExactAsync(
        string magpieDirectory,
        Process process,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        string expectedExecutablePath = ValidateMagpieDirectory(magpieDirectory);
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        if (process.HasExited)
        {
            return true;
        }

        string? actualExecutablePath =
            ProcessDiscoveryService.TryGetExecutablePath(process.Id);
        if (!string.Equals(
            actualExecutablePath,
            expectedExecutablePath,
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IReadOnlyList<MagpieRunningInstance> instances =
            InspectRunningInstances(magpieDirectory);
        if (instances.Count != 1
            || !instances[0].IsBundledInstance
            || instances[0].ProcessId != process.Id)
        {
            return false;
        }

        uint quitMessage = NativeMethods.RegisterWindowMessageW(MagpieQuitMessageName);
        if (quitMessage == 0
            || !NativeMethods.PostMessageW(
                NativeMethods.HwndBroadcast,
                quitMessage,
                nint.Zero,
                nint.Zero))
        {
            return false;
        }

        using CancellationTokenSource timeoutCancellation =
            new(effectiveTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return process.HasExited
                && InspectRunningInstances(magpieDirectory).Count == 0;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && timeoutCancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string ValidateMagpieDirectory(string magpieDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(magpieDirectory);
        string fullDirectory = Path.GetFullPath(magpieDirectory);
        string executablePath = Path.Combine(fullDirectory, "Magpie.exe");
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Magpie.exe was not found.", executablePath);
        }

        return executablePath;
    }
}
