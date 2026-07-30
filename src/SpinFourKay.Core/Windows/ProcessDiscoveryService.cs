using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpinFourKay.Core.Windows;

public sealed record ProcessDescriptor(
    int ProcessId,
    string ExecutablePath,
    DateTimeOffset? StartTimeUtc);

public interface IProcessDiscoveryService
{
    IReadOnlyList<ProcessDescriptor> FindByExecutableName(string executableName);

    IReadOnlyList<ProcessDescriptor> FindByExecutablePath(string executablePath);

    Task<ProcessDescriptor> WaitForExecutableAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessDiscoveryService : IProcessDiscoveryService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    public IReadOnlyList<ProcessDescriptor> FindByExecutableName(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        string fileName = Path.GetFileName(executableName.Trim());
        if (!string.Equals(fileName, executableName.Trim(), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fileName)))
        {
            throw new ArgumentException(
                "An executable file name without a directory is required.",
                nameof(executableName));
        }

        string processName = Path.GetFileNameWithoutExtension(fileName);
        List<ProcessDescriptor> matches = [];
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                string? candidatePath = TryGetExecutablePath(process.Id);
                if (candidatePath is null
                    || !string.Equals(
                        Path.GetFileName(candidatePath),
                        fileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches.Add(
                    new ProcessDescriptor(
                        process.Id,
                        candidatePath,
                        TryGetStartTimeUtc(process)));
            }
        }

        return OrderMatches(matches);
    }

    public IReadOnlyList<ProcessDescriptor> FindByExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        return FindByExecutableName(Path.GetFileName(fullPath))
            .Where(
                match => string.Equals(
                    match.ExecutablePath,
                    fullPath,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(match => match.StartTimeUtc)
            .ThenByDescending(match => match.ProcessId)
            .ToArray();
    }

    public async Task<ProcessDescriptor> WaitForExecutableAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ProcessDescriptor> processes = FindByExecutablePath(executablePath);
            if (processes.Count > 0)
            {
                return processes[0];
            }

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < PollInterval ? remaining : PollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out waiting for '{Path.GetFileName(executablePath)}' to start.");
    }

    internal static string? TryGetExecutablePath(int processId)
    {
        using Microsoft.Win32.SafeHandles.SafeProcessHandle process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));
        if (process.IsInvalid)
        {
            return null;
        }

        const int MaximumWindowsPath = 32_768;
        char[] path = GC.AllocateUninitializedArray<char>(MaximumWindowsPath);
        uint size = checked((uint)path.Length);
        if (!NativeMethods.QueryFullProcessImageNameW(process, 0, path, ref size))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 5 or 87)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        return new string(path, 0, checked((int)size));
    }

    private static DateTimeOffset? TryGetStartTimeUtc(Process process)
    {
        try
        {
            return new DateTimeOffset(
                process.StartTime.ToUniversalTime(),
                TimeSpan.Zero);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            // Start time is diagnostic metadata and is not required for matching.
            return null;
        }
    }

    private static ProcessDescriptor[] OrderMatches(
        IEnumerable<ProcessDescriptor> matches) =>
        matches
            .OrderByDescending(match => match.StartTimeUtc)
            .ThenByDescending(match => match.ProcessId)
            .ToArray();
}
