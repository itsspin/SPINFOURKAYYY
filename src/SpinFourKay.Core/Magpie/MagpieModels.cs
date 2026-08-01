using SpinFourKay.Core.Display;

namespace SpinFourKay.Core.Magpie;

public sealed record MagpieGraphicsAdapter(
    int Index = -1,
    uint VendorId = 0,
    uint DeviceId = 0);

public sealed record MagpieProfileRequest
{
    public required string MagpieDirectory { get; init; }

    public required string SourceExecutablePath { get; init; }

    public required string SourceWindowClass { get; init; }

    public string ProfileName { get; init; } = "SpinFOURKAYYY - EverQuest Legends";

    public string? LauncherPath { get; init; }

    public ScalingFilter Filter { get; init; } = ScalingFilter.Fsr;

    /// <summary>
    /// Clarity strength from 0 to 2. Values up to 1 map to one RCAS pass;
    /// values above 1 layer a bundled AdaptiveSharpen pass with the remainder.
    /// </summary>
    public double RcasSharpness { get; init; } = 1.0;

    /// <summary>
    /// Uses the RCAS clarity treatment without resizing the captured frame.
    /// This is reserved for a source whose client area exactly matches the
    /// physical target display.
    /// </summary>
    public bool NativeClarityOnly { get; init; }

    public bool AutoScaleFullscreen { get; init; }

    /// <summary>
    /// Magpie's 3D-game mode tears its fullscreen surface down whenever an
    /// overlapping application receives focus. SpinFOURKAYYY keeps this off so
    /// Alt+Tab only changes Z-order and the verified input map remains alive.
    /// </summary>
    public bool ThreeDGameMode { get; init; }

    public bool DisableDirectFlip { get; init; }

    public bool CaptureTitleBar { get; init; }

    public bool AdjustCursorSpeed { get; init; } = true;

    public MagpieGraphicsAdapter GraphicsAdapter { get; init; } = new();

    public double? MaximumFrameRate { get; init; }
}

public sealed record MagpiePortableConfigResult(
    string ConfigPath,
    int ScalingModeIndex,
    int ProfileIndex,
    bool ContentChanged);

public sealed record MagpiePortableConfigTransaction(
    string ConfigPath,
    bool OriginalFileExisted,
    byte[] OriginalContent,
    FileAttributes? OriginalAttributes,
    DateTime? OriginalLastWriteTimeUtc,
    byte[] AppliedContent,
    int ScalingModeIndex,
    int ProfileIndex);

public enum MagpiePortableConfigRollbackDisposition
{
    Restored,
    NewerContentPreserved,
    RetryRequired,
}

public sealed record MagpiePortableConfigRollbackResult(
    MagpiePortableConfigRollbackDisposition Disposition,
    string? Issue)
{
    public bool Restored =>
        Disposition == MagpiePortableConfigRollbackDisposition.Restored;

    public bool Resolved =>
        Disposition != MagpiePortableConfigRollbackDisposition.RetryRequired;
}

public sealed record MagpieProcessStartResult(
    System.Diagnostics.Process Process,
    bool AlreadyRunning);

public sealed record MagpieRunningInstance(
    int ProcessId,
    string? ExecutablePath,
    bool IsBundledInstance);

public sealed class ExternalMagpieInstanceConflictException : InvalidOperationException
{
    public ExternalMagpieInstanceConflictException(
        IReadOnlyList<MagpieRunningInstance> conflictingInstances)
        : base(
            "Another Magpie instance is already running. Close it before starting "
            + "SpinFOURKAYYY so the dedicated portable profile can load.")
    {
        ConflictingInstances = conflictingInstances;
    }

    public IReadOnlyList<MagpieRunningInstance> ConflictingInstances { get; }
}

public interface IMagpiePortableConfigService
{
    Task<MagpiePortableConfigTransaction> PrepareTransactionAsync(
        MagpieProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<MagpiePortableConfigResult> ApplyTransactionAsync(
        MagpiePortableConfigTransaction transaction,
        CancellationToken cancellationToken = default);

    Task<MagpiePortableConfigRollbackResult> RollbackTransactionAsync(
        MagpiePortableConfigTransaction transaction,
        bool allowChangedContentAfterEngineExit = false,
        CancellationToken cancellationToken = default);

    Task<MagpiePortableConfigResult> WriteAsync(
        MagpieProfileRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMagpieProcessService
{
    IReadOnlyList<MagpieRunningInstance> InspectRunningInstances(string magpieDirectory);

    System.Diagnostics.Process? TryFindRunning(string magpieDirectory);

    MagpieProcessStartResult StartPortable(
        string magpieDirectory,
        bool startInTray = true);

    Task<bool> ShutdownBundledAsync(
        string magpieDirectory,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<bool> ShutdownExactAsync(
        string magpieDirectory,
        System.Diagnostics.Process process,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
