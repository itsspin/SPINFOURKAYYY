using SpinFourKay.Core.Backup;
using SpinFourKay.Core.Configuration;
using SpinFourKay.Core.IO;
using SpinFourKay.Core.Windows;

namespace SpinFourKay.Core.Orchestration;

public interface IFourKayPreparationService
{
    Task<FourKayPreparedState> PrepareAsync(
        FourKayPreparationRequest request,
        CancellationToken cancellationToken = default);

    Task<FourKayRestoreResult> RestoreAsync(
        FourKayPreparedState state,
        CancellationToken cancellationToken = default);
}

public sealed class FourKayPreparationService : IFourKayPreparationService
{
    private static readonly string[] ClientConfigurationWriterFileNames =
    [
        "eqgame.exe",
        "OptionsEditor.exe",
        "LaunchPad.exe",
    ];

    private readonly IBackupService _backupService;
    private readonly IEqClientConfigurationService _eqClientConfiguration;
    private readonly IDpiCompatibilityService _dpiCompatibility;
    private readonly IProcessDiscoveryService _processDiscovery;
    private readonly IFourKayJournalStore _journalStore;
    private readonly IWindowPlacementService _windowPlacement;

    public FourKayPreparationService(
        IBackupService? backupService = null,
        IEqClientConfigurationService? eqClientConfiguration = null,
        IDpiCompatibilityService? dpiCompatibility = null,
        IProcessDiscoveryService? processDiscovery = null,
        IFourKayJournalStore? journalStore = null,
        IWindowPlacementService? windowPlacement = null)
    {
        _backupService = backupService ?? new BackupService();
        _eqClientConfiguration =
            eqClientConfiguration ?? new EqClientConfigurationService();
        _dpiCompatibility = dpiCompatibility ?? new DpiCompatibilityService();
        _processDiscovery = processDiscovery ?? new ProcessDiscoveryService();
        _journalStore = journalStore ?? new FourKayJournalStore();
        _windowPlacement = windowPlacement ?? new WindowPlacementService();
    }

    public async Task<FourKayPreparedState> PrepareAsync(
        FourKayPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        string eqDirectory = Path.GetFullPath(request.EqDirectory);
        string stateDirectory = Path.GetFullPath(request.StateDirectory);
        string eqGamePath = RequireFile(Path.Combine(eqDirectory, "eqgame.exe"));
        string eqClientIniPath = RequireFile(Path.Combine(eqDirectory, "eqclient.ini"));
        await using FileStream operationLock = await FourKayOperationLock.AcquireAsync(
            stateDirectory,
            eqClientIniPath,
            cancellationToken).ConfigureAwait(false);
        MonitorDescriptor targetMonitor = ResolveTargetMonitor(request.TargetMonitor);
        if (targetMonitor.Bounds.Width != request.ResolutionPlan.TargetResolution.Width
            || targetMonitor.Bounds.Height != request.ResolutionPlan.TargetResolution.Height)
        {
            throw new ArgumentException(
                "The resolution plan target does not match the selected monitor's "
                + $"physical {targetMonitor.Bounds.Width}x{targetMonitor.Bounds.Height} bounds.",
                nameof(request));
        }

        ThrowIfClientConfigurationWritersRunning(eqDirectory);
        FourKayPreparedState? pending = await _journalStore.LoadLatestActiveAsync(
            stateDirectory,
            cancellationToken).ConfigureAwait(false);
        if (pending is not null
            && string.Equals(
                pending.EqClientIniPath,
                eqClientIniPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PendingRecoveryException(pending);
        }

        EqClientVideoSettings initialSettings =
            await _eqClientConfiguration.ReadAsync(
                eqClientIniPath,
                cancellationToken).ConfigureAwait(false);
        if (initialSettings.NativeUiScaleStatus == NativeUiScaleStatus.Invalid)
        {
            throw new InvalidDataException(
                "UIScale is present but malformed or outside the supported 0-4 "
                    + "range. Preparation stopped before creating a backup, recovery "
                    + "journal, or per-game DPI change.");
        }

        IReadOnlyList<string> protectedPlayerFiles =
            EverQuestPlayerStateLocator.FindExistingFiles(eqDirectory);
        BackupManifest backup = await _backupService.CreateAsync(
            protectedPlayerFiles,
            Path.Combine(stateDirectory, "backups"),
            cancellationToken).ConfigureAwait(false);
        BackupEntry iniBackup = backup.Entries.Single(
            entry => PathEquals(entry.OriginalPath, eqClientIniPath));
        DpiCompatibilitySnapshot capturedDpi = _dpiCompatibility.Capture(eqGamePath);

        FourKayPreparedState state = new()
        {
            SessionId = Guid.NewGuid(),
            Status = FourKayJournalStatus.Preparing,
            PreparedAtUtc = DateTimeOffset.UtcNow,
            EqDirectory = eqDirectory,
            StateDirectory = stateDirectory,
            EqGamePath = eqGamePath,
            EqClientIniPath = eqClientIniPath,
            BackupManifestPath = backup.ManifestPath,
            DpiCompatibility = capturedDpi,
            ResolutionPlan = request.ResolutionPlan,
            TargetMonitorHandle = targetMonitor.Handle.ToInt64(),
            TargetMonitorBounds = targetMonitor.Bounds,
            OriginalIniSha256 = iniBackup.Sha256,
            AppliedIniSha256 = null,
            PlayerStateProtectionVersion =
                EverQuestPlayerStateLocator.CurrentProtectionVersion,
            ProtectedPlayerFiles = protectedPlayerFiles,
            ChatFontSizeIncrease = request.ChatFontSizeIncrease,
            UiCompatibilityMode = request.UiCompatibilityMode,
        };
        state = await _journalStore.SaveNewAsync(stateDirectory, state, cancellationToken)
            .ConfigureAwait(false);

        bool dpiApplyMayHaveMutatedRegistry = false;
        try
        {
            ThrowIfClientConfigurationWritersRunning(eqDirectory);
            dpiApplyMayHaveMutatedRegistry = true;
            DpiCompatibilitySnapshot appliedDpi;
            try
            {
                appliedDpi =
                    _dpiCompatibility.ApplyApplicationAware(state.DpiCompatibility);
            }
            catch (DpiCompatibilityValueChangedException)
            {
                // This specific precondition failure is guaranteed to occur before
                // SetValue, so the newer third-party value is not ours to restore.
                dpiApplyMayHaveMutatedRegistry = false;
                throw;
            }

            state = state with { DpiCompatibility = appliedDpi };
            state = await _journalStore.UpdateAsync(state, cancellationToken)
                .ConfigureAwait(false);

            ThrowIfClientConfigurationWritersRunning(eqDirectory);
            EqClientConfigurationApplyResult applyResult =
                await _eqClientConfiguration.ApplyWindowedResolutionAsync(
                    eqClientIniPath,
                    request.ResolutionPlan.SourceResolution,
                    request.ChatFontSizeIncrease,
                    cancellationToken).ConfigureAwait(false);
            ThrowIfClientConfigurationWritersRunning(eqDirectory);
            string appliedHash = await FileHash.ComputeSha256Async(
                eqClientIniPath,
                cancellationToken).ConfigureAwait(false);
            ThrowIfClientConfigurationWritersRunning(eqDirectory);

            state = state with
            {
                Status = FourKayJournalStatus.Prepared,
                AppliedIniSha256 = appliedHash,
                AppliedChatFontSize = applyResult.AppliedChatFontSize,
                Warnings = applyResult.Warnings,
            };
            return await _journalStore.UpdateAsync(state, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            List<string> rollbackWarnings = state.Warnings.ToList();
            bool rollbackCompleted = false;
            List<ClientConfigurationWriter> runningWriters =
                FindRunningClientConfigurationWriters(eqDirectory);
            if (runningWriters.Count == 0)
            {
                try
                {
                    await _backupService.RestoreAsync(backup, CancellationToken.None)
                        .ConfigureAwait(false);
                    await VerifyRestoredPlayerStateAsync(
                        backup,
                        CancellationToken.None).ConfigureAwait(false);
                    bool playerStateWasRestored = true;
                    List<ClientConfigurationWriter> writersAfterRollback =
                        FindRunningClientConfigurationWriters(eqDirectory);
                    if (writersAfterRollback.Count > 0)
                    {
                        playerStateWasRestored = false;
                        rollbackWarnings.Add(
                            "A client-side configuration writer started during rollback; "
                            + "recovery remains pending until it exits: "
                            + string.Join(
                                ", ",
                                writersAfterRollback.Select(
                                    writer => Path.GetFileName(
                                        writer.ExecutablePath))));
                    }

                    bool dpiWasRestored = true;
                    if (dpiApplyMayHaveMutatedRegistry)
                    {
                        DpiRestoreResult dpiRollback =
                            _dpiCompatibility.Restore(state.DpiCompatibility);
                        dpiWasRestored = dpiRollback.WasRestored;
                        if (!dpiWasRestored)
                        {
                            rollbackWarnings.Add(
                                "The DPI compatibility value could not be restored automatically; "
                                + "recovery remains pending.");
                        }
                    }

                    if (!playerStateWasRestored)
                    {
                        rollbackWarnings.Add(
                            "Protected Legends player-state did not remain byte-exact "
                                + "after rollback; recovery remains pending.");
                    }

                    if (playerStateWasRestored && dpiWasRestored)
                    {
                        state = state with
                        {
                            Status = FourKayJournalStatus.FailedAndRolledBack,
                            Warnings = rollbackWarnings,
                        };
                        state = await _journalStore.UpdateAsync(
                            state,
                            CancellationToken.None).ConfigureAwait(false);
                        rollbackCompleted = true;
                    }
                    else
                    {
                        state = state with
                        {
                            Status = FourKayJournalStatus.Preparing,
                            Warnings = rollbackWarnings,
                        };
                        state = await _journalStore.UpdateAsync(
                            state,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackWarnings.Add(
                        $"Automatic rollback also failed: {rollbackException.Message}");
                    state = state with
                    {
                        Status = FourKayJournalStatus.Preparing,
                        Warnings = rollbackWarnings,
                    };
                    try
                    {
                        state = await _journalStore.UpdateAsync(state, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve and report the original preparation failure.
                    }
                }
            }
            else
            {
                rollbackWarnings.Add(
                    "A client-side configuration writer started during preparation; "
                    + "recovery remains pending until it exits: "
                    + string.Join(
                        ", ",
                        runningWriters.Select(
                            writer => Path.GetFileName(writer.ExecutablePath))));
                state = state with
                {
                    Status = FourKayJournalStatus.Preparing,
                    Warnings = rollbackWarnings,
                };
                try
                {
                    state = await _journalStore.UpdateAsync(state, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Preserve and report the original preparation failure.
                }
            }

            if (exception is OperationCanceledException && rollbackCompleted)
            {
                throw;
            }

            throw new FourKayPreparationException(
                "SpinFOURKAYYY could not prepare a reversible 4K session.",
                state,
                exception);
        }
    }

    public async Task<FourKayRestoreResult> RestoreAsync(
        FourKayPreparedState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status is FourKayJournalStatus.Restored
            or FourKayJournalStatus.RestoredWithDpiConflict)
        {
            return new FourKayRestoreResult(
                FourKayRestoreDisposition.AlreadyRestored,
                state,
                DpiRestore: null,
                state.RecoveryBackupManifestPath,
                BlockingGameProcessIds: [],
                state.Warnings);
        }

        await using FileStream operationLock = await FourKayOperationLock.AcquireAsync(
            state.StateDirectory,
            state.EqClientIniPath,
            cancellationToken).ConfigureAwait(false);

        List<ClientConfigurationWriter> runningWriters =
            FindRunningClientConfigurationWriters(state.EqDirectory);
        ClientConfigurationWriter? runningGame = runningWriters.FirstOrDefault(
            writer => PathEquals(writer.ExecutablePath, state.EqGamePath));
        if (runningGame is not null)
        {
            return new FourKayRestoreResult(
                FourKayRestoreDisposition.GameStillRunning,
                state,
                DpiRestore: null,
                RecoveryBackupManifestPath: null,
                runningGame.ProcessIds,
                Warnings:
                [
                    "Exit EverQuest Legends before restoring eqclient.ini; "
                    + "the running client could overwrite it on exit.",
                ]);
        }

        if (runningWriters.Count > 0)
        {
            throw new ClientConfigurationWriterRunningException(runningWriters);
        }

        BackupManifest backup = await _backupService.LoadManifestAsync(
            state.BackupManifestPath,
            cancellationToken).ConfigureAwait(false);
        ValidateRestoreScope(state, backup);

        List<string> warnings = state.Warnings.ToList();
        IReadOnlyList<string> recoveryManifestPaths =
            await ProtectLivePlayerStateImmediatelyBeforeRestoreAsync(
                state,
                backup,
                cancellationToken).ConfigureAwait(false);
        foreach (string recoveryManifestPath in recoveryManifestPaths)
        {
            warnings.Add(
                "One or more protected Legends player-state files changed after "
                    + "preparation, so their latest versions were preserved at "
                    + $"'{recoveryManifestPath}' before the original profile was "
                    + "restored.");
        }

        string? latestRecoveryManifestPath = recoveryManifestPaths.Count == 0
            ? null
            : recoveryManifestPaths[^1];
        ThrowIfClientConfigurationWritersRunning(state.EqDirectory);
        await _backupService.RestoreAsync(backup, cancellationToken).ConfigureAwait(false);
        await VerifyRestoredPlayerStateAsync(
            backup,
            cancellationToken).ConfigureAwait(false);
        ThrowIfClientConfigurationWritersRunning(state.EqDirectory);

        DpiRestoreResult dpiRestore = _dpiCompatibility.Restore(state.DpiCompatibility);

        if (dpiRestore.DpiTokenConflict)
        {
            warnings.Add(
                "The DPI compatibility value or registry kind changed after preparation. "
                + "SpinFOURKAYYY preserved the newer value and did not overwrite it.");
        }
        else if (dpiRestore.CurrentValueChangedAfterApply)
        {
            warnings.Add(
                "Another tool changed the compatibility entry; only the original DPI "
                + "tokens were restored and newer unrelated flags were preserved.");
        }

        FourKayPreparedState stateWithWarnings = state with { Warnings = warnings };
        FourKayPreparedState restored;
        if (dpiRestore.DpiTokenConflict)
        {
            restored = stateWithWarnings with
            {
                Status = FourKayJournalStatus.RestoredWithDpiConflict,
                RestoredAtUtc = DateTimeOffset.UtcNow,
                RecoveryBackupManifestPath = latestRecoveryManifestPath,
            };
            restored = await _journalStore.UpdateAsync(restored, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            restored = await _journalStore.MarkRestoredAsync(
                stateWithWarnings,
                latestRecoveryManifestPath,
                cancellationToken).ConfigureAwait(false);
        }

        return new FourKayRestoreResult(
            dpiRestore.DpiTokenConflict
                ? FourKayRestoreDisposition.RestoredWithDpiConflict
                : FourKayRestoreDisposition.Restored,
            restored,
            dpiRestore,
            latestRecoveryManifestPath,
            BlockingGameProcessIds: [],
            warnings);
    }

    private async Task<IReadOnlyList<string>>
        ProtectLivePlayerStateImmediatelyBeforeRestoreAsync(
        FourKayPreparedState state,
        BackupManifest originalBackup,
        CancellationToken cancellationToken)
    {
        const int MaximumStabilityAttempts = 3;
        Dictionary<string, string> originalHashes =
            originalBackup.Entries.ToDictionary(
                entry => Path.GetFullPath(entry.OriginalPath),
                entry => entry.Sha256,
                StringComparer.OrdinalIgnoreCase);

        List<string> recoveryManifestPaths = [];
        for (int attempt = 1; attempt <= MaximumStabilityAttempts; attempt++)
        {
            ThrowIfClientConfigurationWritersRunning(state.EqDirectory);
            PlayerStateSnapshot before =
                await CaptureLivePlayerStateSnapshotAsync(
                    state,
                    cancellationToken).ConfigureAwait(false);
            string[] changedPaths = before.Hashes
                .Where(
                    pair =>
                        !originalHashes.TryGetValue(
                            pair.Key,
                            out string? originalHash)
                        || (!HashEquals(pair.Value, originalHash)
                            && !(PathEquals(
                                    pair.Key,
                                    state.EqClientIniPath)
                                && state.AppliedIniSha256 is not null
                                && HashEquals(
                                    pair.Value,
                                    state.AppliedIniSha256))))
                .Select(pair => pair.Key)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (changedPaths.Length > 0)
            {
                BackupManifest recovery = await _backupService.CreateAsync(
                    changedPaths,
                    Path.Combine(
                        originalBackup.BackupDirectory,
                        "recovery-before-restore"),
                    cancellationToken).ConfigureAwait(false);
                if (recovery.Entries.Count != changedPaths.Length
                    || recovery.Entries.Any(
                        entry =>
                            !before.Hashes.TryGetValue(
                                Path.GetFullPath(entry.OriginalPath),
                                out string? beforeHash)
                            || !HashEquals(entry.Sha256, beforeHash)))
                {
                    throw new IOException(
                        "Protected Legends player-state changed while its recovery "
                            + "backup was being captured. Restore was aborted and all "
                            + "live files were left intact.");
                }

                recoveryManifestPaths.Add(recovery.ManifestPath);
            }

            ThrowIfClientConfigurationWritersRunning(state.EqDirectory);
            PlayerStateSnapshot after =
                await CaptureLivePlayerStateSnapshotAsync(
                    state,
                    cancellationToken).ConfigureAwait(false);
            ThrowIfClientConfigurationWritersRunning(state.EqDirectory);
            if (string.Equals(
                before.Fingerprint,
                after.Fingerprint,
                StringComparison.Ordinal))
            {
                return recoveryManifestPaths;
            }
        }

        throw new IOException(
            "Protected Legends player-state continued changing immediately before "
                + "restore. Restore was aborted and the live files were left intact.");
    }

    private static async Task<PlayerStateSnapshot>
        CaptureLivePlayerStateSnapshotAsync(
            FourKayPreparedState state,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<string> paths = state.PlayerStateProtectionVersion == 0
            ? File.Exists(state.EqClientIniPath)
                ? [state.EqClientIniPath]
                : []
            : EverQuestPlayerStateLocator.FindExistingFilesForRecovery(
                state.EqDirectory);
        Dictionary<string, string> hashes =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes[Path.GetFullPath(path)] =
                await FileHash.ComputeSha256Async(
                    path,
                    cancellationToken).ConfigureAwait(false);
        }

        string fingerprint = string.Join(
            '\n',
            hashes
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(
                    pair =>
                        pair.Key.ToUpperInvariant()
                        + "\0"
                        + pair.Value.ToUpperInvariant()));
        return new PlayerStateSnapshot(hashes, fingerprint);
    }

    private static async Task VerifyRestoredPlayerStateAsync(
        BackupManifest backup,
        CancellationToken cancellationToken)
    {
        List<string> issues = [];
        foreach (BackupEntry entry in backup.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(entry.OriginalPath);
            if (!File.Exists(path))
            {
                issues.Add($"'{path}' is missing");
                continue;
            }

            string hash = await FileHash.ComputeSha256Async(
                path,
                cancellationToken).ConfigureAwait(false);
            if (!HashEquals(hash, entry.Sha256))
            {
                issues.Add($"'{path}' failed its original SHA-256 check");
            }
        }

        if (issues.Count > 0)
        {
            throw new IOException(
                "Protected Legends player-state changed during the final restore. "
                    + "Recovery remains pending and can be retried after every "
                    + "client-side writer exits: "
                    + string.Join("; ", issues));
        }
    }

    private void ThrowIfClientConfigurationWritersRunning(string eqDirectory)
    {
        List<ClientConfigurationWriter> runningWriters =
            FindRunningClientConfigurationWriters(eqDirectory);
        if (runningWriters.Count > 0)
        {
            ClientConfigurationWriter? runningGame = runningWriters.FirstOrDefault(
                writer => string.Equals(
                    Path.GetFileName(writer.ExecutablePath),
                    "eqgame.exe",
                    StringComparison.OrdinalIgnoreCase));
            if (runningGame is not null)
            {
                throw new GameAlreadyRunningException(
                    runningGame.ExecutablePath,
                    runningGame.ProcessIds);
            }

            throw new ClientConfigurationWriterRunningException(runningWriters);
        }
    }

    private List<ClientConfigurationWriter>
        FindRunningClientConfigurationWriters(string eqDirectory)
    {
        List<ClientConfigurationWriter> runningWriters = [];
        foreach (string fileName in ClientConfigurationWriterFileNames)
        {
            string executablePath = Path.GetFullPath(
                Path.Combine(eqDirectory, fileName));
            if (!File.Exists(executablePath))
            {
                continue;
            }

            int[] processIds = _processDiscovery.FindByExecutablePath(executablePath)
                .Select(process => process.ProcessId)
                .ToArray();
            if (processIds.Length > 0)
            {
                runningWriters.Add(new ClientConfigurationWriter(
                    executablePath,
                    processIds));
            }
        }

        return runningWriters;
    }

    private static bool HashEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void ValidateRequest(FourKayPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EqDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StateDirectory);
        ArgumentNullException.ThrowIfNull(request.ResolutionPlan);
        if (request.ChatFontSizeIncrease is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Chat font size increase must be 0, 1, or 2.");
        }

        if (!Enum.IsDefined(request.UiCompatibilityMode)
            || request.UiCompatibilityMode
                == FourKayUiCompatibilityMode.UnspecifiedLegacy)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A new preparation requires an explicit generic/custom or strict "
                    + "SpinUI compatibility mode.");
        }
    }

    private static string RequireFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("A required EverQuest Legends file is missing.", fullPath);
        }

        return fullPath;
    }

    private static void ValidateRestoreScope(
        FourKayPreparedState state,
        BackupManifest backup)
    {
        string eqDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(state.EqDirectory));
        string expectedIniPath = Path.GetFullPath(
            Path.Combine(eqDirectory, "eqclient.ini"));
        string expectedGamePath = Path.GetFullPath(
            Path.Combine(eqDirectory, "eqgame.exe"));
        if (!PathEquals(state.EqClientIniPath, expectedIniPath)
            || !PathEquals(state.EqGamePath, expectedGamePath)
            || state.DpiCompatibility is null
            || string.IsNullOrWhiteSpace(
                state.DpiCompatibility.ExecutablePath)
            || !PathEquals(
                state.DpiCompatibility.ExecutablePath,
                expectedGamePath))
        {
            throw new InvalidDataException(
                "The recovery journal does not point to the selected client's exact files.");
        }

        string managedBackupsRoot = Path.Combine(
            Path.GetFullPath(state.StateDirectory),
            "backups");
        if (!IsChildPath(managedBackupsRoot, state.BackupManifestPath)
            || !IsChildPath(managedBackupsRoot, backup.BackupDirectory))
        {
            throw new InvalidDataException(
                "The recovery manifest is outside SpinFOURKAYYY's managed backup directory.");
        }

        BackupEntry[] iniEntries = backup.Entries
            .Where(entry => PathEquals(entry.OriginalPath, expectedIniPath))
            .ToArray();
        if (iniEntries.Length != 1
            || !string.Equals(
                iniEntries[0].Sha256,
                state.OriginalIniSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The recovery payload does not match the journal's original eqclient.ini.");
        }

        if (state.PlayerStateProtectionVersion == 0)
        {
            if (backup.Entries.Count != 1
                || state.ProtectedPlayerFiles.Count != 0)
            {
                throw new InvalidDataException(
                    "A legacy recovery journal may contain only its original "
                        + "eqclient.ini payload.");
            }

            return;
        }

        if (state.PlayerStateProtectionVersion
                != EverQuestPlayerStateLocator.CurrentProtectionVersion
            || state.ProtectedPlayerFiles.Count == 0)
        {
            throw new InvalidDataException(
                "The recovery journal contains an unsupported player-state "
                    + "protection version.");
        }

        string[] protectedPaths = state.ProtectedPlayerFiles
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (protectedPaths.Length != state.ProtectedPlayerFiles.Count
            || !protectedPaths.Any(path => PathEquals(path, expectedIniPath))
            || protectedPaths.Any(
                path =>
                    !EverQuestPlayerStateLocator.IsSupportedProtectedPath(
                        eqDirectory,
                        path)))
        {
            throw new InvalidDataException(
                "The recovery journal's protected player-state file list is "
                    + "duplicated, incomplete, or outside the supported Legends "
                    + "profile scope.");
        }

        string[] entryPaths = backup.Entries
            .Select(entry => Path.GetFullPath(entry.OriginalPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (entryPaths.Length != backup.Entries.Count
            || entryPaths.Length != protectedPaths.Length
            || entryPaths.Any(
                path =>
                    !EverQuestPlayerStateLocator.IsSupportedProtectedPath(
                        eqDirectory,
                        path))
            || protectedPaths.Except(
                    entryPaths,
                    StringComparer.OrdinalIgnoreCase)
                .Any())
        {
            throw new InvalidDataException(
                "The verified backup manifest does not exactly match the journal's "
                    + "protected Legends player-state files.");
        }
    }

    private static bool IsChildPath(string root, string candidate)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(
            fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private MonitorDescriptor ResolveTargetMonitor(nint targetMonitor)
    {
        IReadOnlyList<MonitorDescriptor> monitors = _windowPlacement.GetMonitors();
        MonitorDescriptor? selected = targetMonitor == nint.Zero
            ? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            : monitors.FirstOrDefault(monitor => monitor.Handle == targetMonitor);
        return selected
            ?? throw new ArgumentException(
                targetMonitor == nint.Zero
                    ? "Windows did not report a primary monitor."
                    : "The selected monitor is no longer connected.",
                nameof(targetMonitor));
    }

    private sealed record PlayerStateSnapshot(
        IReadOnlyDictionary<string, string> Hashes,
        string Fingerprint);
}
