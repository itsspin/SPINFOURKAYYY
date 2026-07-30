namespace SpinFourKay.Core.IO;

/// <summary>
/// Writes a complete replacement beside the destination before swapping it in.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The destination must have a parent directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        FileAttributes? originalAttributes = File.Exists(fullPath)
            ? File.GetAttributes(fullPath)
            : null;

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);

            if (originalAttributes is not null)
            {
                File.SetAttributes(fullPath, originalAttributes.Value);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static Task WriteAllTextAsync(
        string path,
        string content,
        System.Text.Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        byte[] preamble = encoding.GetPreamble();
        byte[] payload = encoding.GetBytes(content);

        if (preamble.Length == 0)
        {
            return WriteAllBytesAsync(path, payload, cancellationToken);
        }

        byte[] combined = GC.AllocateUninitializedArray<byte>(preamble.Length + payload.Length);
        preamble.CopyTo(combined, 0);
        payload.CopyTo(combined, preamble.Length);
        return WriteAllBytesAsync(path, combined, cancellationToken);
    }
}
