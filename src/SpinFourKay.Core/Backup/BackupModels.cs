using System.Text.Json.Serialization;

namespace SpinFourKay.Core.Backup;

public sealed record BackupEntry
{
    public required string OriginalPath { get; init; }

    public required string BackupRelativePath { get; init; }

    public required long Length { get; init; }

    public required string Sha256 { get; init; }

    public required DateTimeOffset LastWriteTimeUtc { get; init; }

    public required FileAttributes Attributes { get; init; }
}

public sealed record BackupManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required string BackupId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required IReadOnlyList<BackupEntry> Entries { get; init; }

    [JsonIgnore]
    public string BackupDirectory { get; init; } = string.Empty;

    [JsonIgnore]
    public string ManifestPath { get; init; } = string.Empty;
}

public sealed record BackupVerificationResult(
    bool IsValid,
    IReadOnlyList<string> Errors);

public interface IBackupService
{
    Task<BackupManifest> CreateAsync(
        IEnumerable<string> sourcePaths,
        string backupRoot,
        CancellationToken cancellationToken = default);

    Task<BackupManifest> LoadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default);

    Task<BackupVerificationResult> VerifyAsync(
        BackupManifest manifest,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(
        BackupManifest manifest,
        CancellationToken cancellationToken = default);
}
