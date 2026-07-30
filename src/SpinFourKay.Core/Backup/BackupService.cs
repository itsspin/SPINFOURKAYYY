using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using SpinFourKay.Core.IO;

namespace SpinFourKay.Core.Backup;

/// <summary>
/// Creates relocatable, checksummed backups and verifies every payload before restore.
/// </summary>
public sealed class BackupService : IBackupService
{
    private const string ManifestFileName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task<BackupManifest> CreateAsync(
        IEnumerable<string> sourcePaths,
        string backupRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);

        string[] sources = sourcePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sources.Length == 0)
        {
            throw new ArgumentException("At least one source file is required.", nameof(sourcePaths));
        }

        foreach (string source in sources)
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("A file selected for backup was not found.", source);
            }
        }

        string fullBackupRoot = Path.GetFullPath(backupRoot);
        Directory.CreateDirectory(fullBackupRoot);

        string backupId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        string finalDirectory = EnsureChildPath(fullBackupRoot, Path.Combine(fullBackupRoot, backupId));
        string pendingDirectory = EnsureChildPath(fullBackupRoot, finalDirectory + ".pending");
        string filesDirectory = Path.Combine(pendingDirectory, "files");
        Directory.CreateDirectory(filesDirectory);

        try
        {
            List<BackupEntry> entries = new(sources.Length);
            for (int index = 0; index < sources.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string source = sources[index];
                string safeName = MakeSafeFileName(Path.GetFileName(source));
                string relativePath = Path.Combine("files", $"{index:D4}-{safeName}");
                string destination = EnsureChildPath(
                    pendingDirectory,
                    Path.Combine(pendingDirectory, relativePath));

                string sourceHash = await CopyAndHashAsync(source, destination, cancellationToken)
                    .ConfigureAwait(false);
                string destinationHash = await ComputeSha256Async(destination, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"Backup verification failed while copying '{source}'.");
                }

                FileInfo info = new(source);
                entries.Add(new BackupEntry
                {
                    OriginalPath = source,
                    BackupRelativePath = relativePath.Replace('\\', '/'),
                    Length = info.Length,
                    Sha256 = destinationHash,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                    Attributes = info.Attributes,
                });
            }

            BackupManifest manifest = new()
            {
                BackupId = backupId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Entries = entries,
                BackupDirectory = finalDirectory,
                ManifestPath = Path.Combine(finalDirectory, ManifestFileName),
            };

            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await AtomicFile.WriteAllBytesAsync(
                Path.Combine(pendingDirectory, ManifestFileName),
                manifestBytes,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(pendingDirectory, finalDirectory);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(pendingDirectory))
            {
                Directory.Delete(pendingDirectory, recursive: true);
            }

            throw;
        }
    }

    public async Task<BackupManifest> LoadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        string backupDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new ArgumentException("The manifest must have a parent directory.", nameof(manifestPath));

        await using FileStream stream = new(
            fullManifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        BackupManifest manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The backup manifest is empty.");

        if (manifest.FormatVersion != BackupManifest.CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"Backup manifest version {manifest.FormatVersion} is not supported.");
        }

        return manifest with
        {
            BackupDirectory = backupDirectory,
            ManifestPath = fullManifestPath,
        };
    }

    public async Task<BackupVerificationResult> VerifyAsync(
        BackupManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifest(manifest);

        List<string> errors = [];
        foreach (BackupEntry entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string backupPath;
            try
            {
                backupPath = ResolveBackupPath(manifest, entry);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or InvalidDataException
                or NotSupportedException)
            {
                errors.Add($"{entry.OriginalPath}: {exception.Message}");
                continue;
            }

            if (!File.Exists(backupPath))
            {
                errors.Add($"{entry.OriginalPath}: backup payload is missing.");
                continue;
            }

            FileInfo info = new(backupPath);
            if (info.Length != entry.Length)
            {
                errors.Add(
                    $"{entry.OriginalPath}: expected {entry.Length} bytes, found {info.Length}.");
                continue;
            }

            string hash = await ComputeSha256Async(backupPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{entry.OriginalPath}: SHA-256 checksum mismatch.");
            }
        }

        return new BackupVerificationResult(errors.Count == 0, errors);
    }

    public async Task RestoreAsync(
        BackupManifest manifest,
        CancellationToken cancellationToken = default)
    {
        BackupVerificationResult verification = await VerifyAsync(manifest, cancellationToken)
            .ConfigureAwait(false);
        if (!verification.IsValid)
        {
            throw new InvalidDataException(
                "The backup cannot be restored: "
                + string.Join(" ", verification.Errors));
        }

        // All payloads are verified before the first live file is replaced.
        foreach (BackupEntry entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string backupPath = ResolveBackupPath(manifest, entry);
            byte[] content = await File.ReadAllBytesAsync(backupPath, cancellationToken)
                .ConfigureAwait(false);

            await AtomicFile.WriteAllBytesAsync(entry.OriginalPath, content, cancellationToken)
                .ConfigureAwait(false);
            File.SetLastWriteTimeUtc(entry.OriginalPath, entry.LastWriteTimeUtc.UtcDateTime);
            File.SetAttributes(entry.OriginalPath, entry.Attributes);
        }
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.FormatVersion != BackupManifest.CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"Backup manifest version {manifest.FormatVersion} is not supported.");
        }

        if (string.IsNullOrWhiteSpace(manifest.BackupDirectory)
            || manifest.Entries is null
            || manifest.Entries.Count == 0)
        {
            throw new InvalidDataException("The backup manifest is incomplete.");
        }
    }

    private static string ResolveBackupPath(BackupManifest manifest, BackupEntry entry)
    {
        if (Path.IsPathRooted(entry.BackupRelativePath))
        {
            throw new InvalidDataException("A backup payload path must be relative.");
        }

        string relativePath = entry.BackupRelativePath.Replace('/', Path.DirectorySeparatorChar);
        return EnsureChildPath(
            manifest.BackupDirectory,
            Path.Combine(manifest.BackupDirectory, relativePath));
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string EnsureChildPath(string root, string candidate)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullCandidate = Path.GetFullPath(candidate);
        string prefix = fullRoot + Path.DirectorySeparatorChar;

        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A backup path escaped its managed directory.");
        }

        return fullCandidate;
    }

    private static string MakeSafeFileName(string fileName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = fileName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();
        string result = new(chars);
        return string.IsNullOrWhiteSpace(result) ? "file" : result;
    }
}
