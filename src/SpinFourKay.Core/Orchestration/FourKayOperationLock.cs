using System.Security.Cryptography;
using System.Text;

namespace SpinFourKay.Core.Orchestration;

internal static class FourKayOperationLock
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    internal static async Task<FileStream> AcquireAsync(
        string stateDirectory,
        string eqClientIniPath,
        CancellationToken cancellationToken)
    {
        string locksDirectory = Path.Combine(Path.GetFullPath(stateDirectory), "locks");
        Directory.CreateDirectory(locksDirectory);
        byte[] identity = SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(eqClientIniPath).ToUpperInvariant()));
        string lockPath = Path.Combine(
            locksDirectory,
            $"{Convert.ToHexString(identity)}.lock");
        DateTimeOffset deadline = DateTimeOffset.UtcNow + LockTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
                await Task.Delay(
                    remaining < RetryDelay ? remaining : RetryDelay,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            "Another SpinFOURKAYYY process is preparing or restoring this client.");
    }
}
