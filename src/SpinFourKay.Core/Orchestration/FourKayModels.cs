using System.Text.Json.Serialization;
using SpinFourKay.Core.Display;
using SpinFourKay.Core.Magpie;
using SpinFourKay.Core.Windows;

namespace SpinFourKay.Core.Orchestration;

public enum FourKayJournalStatus
{
    Preparing,
    Prepared,
    Restored,
    RestoredWithDpiConflict,
    FailedAndRolledBack,
}

public enum FourKayUiCompatibilityMode
{
    UnspecifiedLegacy,
    GenericOrCustom,
    SpinUiStrict,
}

public sealed record FourKayPreparationRequest
{
    public required string EqDirectory { get; init; }

    public required string StateDirectory { get; init; }

    public required ResolutionPlan ResolutionPlan { get; init; }

    public nint TargetMonitor { get; init; }

    public int ChatFontSizeIncrease { get; init; }

    public FourKayUiCompatibilityMode UiCompatibilityMode { get; init; }
}

public sealed record FourKayPreparedState
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required Guid SessionId { get; init; }

    public required FourKayJournalStatus Status { get; init; }

    public required DateTimeOffset PreparedAtUtc { get; init; }

    public DateTimeOffset? RestoredAtUtc { get; init; }

    public required string EqDirectory { get; init; }

    public required string StateDirectory { get; init; }

    public required string EqGamePath { get; init; }

    public required string EqClientIniPath { get; init; }

    public required string BackupManifestPath { get; init; }

    public required DpiCompatibilitySnapshot DpiCompatibility { get; init; }

    public required ResolutionPlan ResolutionPlan { get; init; }

    public required long TargetMonitorHandle { get; init; }

    public required PixelRect TargetMonitorBounds { get; init; }

    public required string OriginalIniSha256 { get; init; }

    public string? AppliedIniSha256 { get; init; }

    /// <summary>
    /// Zero identifies a legacy journal that protects eqclient.ini only. New
    /// sessions record every mutable player-state file captured in the same
    /// verified backup manifest.
    /// </summary>
    public int PlayerStateProtectionVersion { get; init; }

    public IReadOnlyList<string> ProtectedPlayerFiles { get; init; } = [];

    public required int ChatFontSizeIncrease { get; init; }

    public int? AppliedChatFontSize { get; init; }

    public FourKayUiCompatibilityMode UiCompatibilityMode { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? RecoveryBackupManifestPath { get; init; }

    [JsonIgnore]
    public string JournalPath { get; init; } = string.Empty;
}

public enum FourKayRestoreDisposition
{
    Restored,
    RestoredWithDpiConflict,
    AlreadyRestored,
    GameStillRunning,
}

public sealed record FourKayRestoreResult(
    FourKayRestoreDisposition Disposition,
    FourKayPreparedState State,
    DpiRestoreResult? DpiRestore,
    string? RecoveryBackupManifestPath,
    IReadOnlyList<int> BlockingGameProcessIds,
    IReadOnlyList<string> Warnings);

public sealed class GameAlreadyRunningException : InvalidOperationException
{
    public GameAlreadyRunningException(string executablePath, IReadOnlyList<int> processIds)
        : base(
            $"'{Path.GetFileName(executablePath)}' is already running. "
            + "Exit the game before changing its saved resolution.")
    {
        ExecutablePath = executablePath;
        ProcessIds = processIds;
    }

    public string ExecutablePath { get; }

    public IReadOnlyList<int> ProcessIds { get; }
}

public sealed record ClientConfigurationWriter(
    string ExecutablePath,
    IReadOnlyList<int> ProcessIds);

public sealed class ClientConfigurationWriterRunningException : InvalidOperationException
{
    public ClientConfigurationWriterRunningException(
        IReadOnlyList<ClientConfigurationWriter> runningWriters)
        : base(
            "Close the EverQuest client-side tools that can write eqclient.ini before "
            + "continuing: "
            + string.Join(
                ", ",
                runningWriters.Select(writer => Path.GetFileName(writer.ExecutablePath))))
    {
        RunningWriters = runningWriters;
    }

    public IReadOnlyList<ClientConfigurationWriter> RunningWriters { get; }
}

public sealed class PendingRecoveryException : InvalidOperationException
{
    public PendingRecoveryException(FourKayPreparedState pendingState)
        : base(
            "A previous SpinFOURKAYYY session still has reversible changes active. "
            + "Restore that session before preparing another one.")
    {
        PendingState = pendingState;
    }

    public FourKayPreparedState PendingState { get; }
}

public sealed class FourKayPreparationException : InvalidOperationException
{
    public FourKayPreparationException(
        string message,
        FourKayPreparedState? state,
        Exception innerException)
        : base(message, innerException)
    {
        State = state;
    }

    public FourKayPreparedState? State { get; }
}

public sealed class UnsafeScalingAttachmentException : InvalidOperationException
{
    public UnsafeScalingAttachmentException(MagpieScalingWindowInspection inspection)
        : base(
            "Magpie started, but its fullscreen/input mapping did not pass validation: "
            + string.Join(" ", inspection.Issues))
    {
        Inspection = inspection;
    }

    public MagpieScalingWindowInspection Inspection { get; }
}

public sealed class DedicatedMagpieConfigReloadRequiredException : InvalidOperationException
{
    public DedicatedMagpieConfigReloadRequiredException()
        : base(
            "The bundled Magpie instance is already running and its portable profile changed. "
            + "Close Magpie, then try again so the new profile can load.")
    {
    }
}
