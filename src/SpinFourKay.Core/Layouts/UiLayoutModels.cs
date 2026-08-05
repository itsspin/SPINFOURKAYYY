using SpinFourKay.Core.Display;

namespace SpinFourKay.Core.Layouts;

public enum UiLayoutSessionStatus
{
    Preparing,
    Active,
    Completing,
    Restoring,
    Completed,
    RolledBack,
}

public sealed record UiLayoutVideoSetting(
    string Section,
    string Key,
    string? OriginalValue,
    string AppliedValue);

public sealed record UiLayoutSessionEntry(
    string FileName,
    string LivePath,
    string NativeSnapshotPath,
    string NativeSha256,
    string ScaledProfilePath,
    string AppliedSha256,
    int ScaledWidthCount,
    int ScaledHeightCount,
    bool ExistedAtPrepare);

public sealed record UiLayoutSessionState
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required Guid SessionId { get; init; }

    public required UiLayoutSessionStatus Status { get; init; }

    public required DateTimeOffset PreparedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public required string EqDirectory { get; init; }

    public required string StateRoot { get; init; }

    public required string JournalPath { get; init; }

    public required PixelSize NativeResolution { get; init; }

    public required PixelSize ScaledResolution { get; init; }

    public required string EqClientIniPath { get; init; }

    public required IReadOnlyList<UiLayoutVideoSetting> VideoSettings { get; init; }

    public required IReadOnlyList<UiLayoutSessionEntry> Entries { get; init; }
}

public sealed record UiLayoutPrepareRequest
{
    public required string EqDirectory { get; init; }

    public required string StateRoot { get; init; }

    public required PixelSize NativeResolution { get; init; }

    public required PixelSize ScaledResolution { get; init; }
}

public sealed record UiLayoutPrepareResult(
    UiLayoutSessionState State,
    int LayoutCount,
    int GeneratedProfileCount,
    int ReusedProfileCount,
    int ScaledWidthCount,
    int ScaledHeightCount);

public sealed record UiLayoutCompleteResult(
    UiLayoutSessionState State,
    int CapturedProfileCount,
    int RestoredExactCount,
    int ConvertedBackCount,
    int NewLayoutCount);

public sealed record UiLayoutTransformResult(
    byte[] Content,
    int ScaledWidthCount,
    int ScaledHeightCount);
