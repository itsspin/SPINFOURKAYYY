using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpinFourKay.Core.Configuration;
using SpinFourKay.Core.IO;

namespace SpinFourKay.Core.Layouts;

public interface IUiLayoutProfileService
{
    Task<UiLayoutPrepareResult> PrepareAsync(
        UiLayoutPrepareRequest request,
        CancellationToken cancellationToken = default);

    Task<UiLayoutCompleteResult> CompleteAsync(
        UiLayoutSessionState state,
        CancellationToken cancellationToken = default);

    Task<UiLayoutSessionState> RollbackAsync(
        UiLayoutSessionState state,
        CancellationToken cancellationToken = default);

    Task<UiLayoutSessionState?> LoadActiveAsync(
        string eqDirectory,
        string stateRoot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Maintains resolution-specific copies of each real EverQuest character layout.
/// Only root UI_*.ini geometry and the minimum video keys needed for a correct
/// initial client size are changed. Character hotbuttons, socials, macros,
/// keybinds, spell sets, userdata, and UI skin assets are outside this service.
/// </summary>
public sealed class UiLayoutProfileService : IUiLayoutProfileService
{
    public const string JournalFileName = "layout-session.json";

    private const long MaximumLayoutBytes = 16 * 1024 * 1024;
    private static readonly Regex LayoutFileNamePattern = new(
        @"^UI_[A-Za-z0-9][A-Za-z0-9'-]*(?:_[A-Za-z0-9][A-Za-z0-9'-]*)+\.ini$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LayoutSlotSegmentPattern = new(
        @"^LO[0-9]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> BackupNameSegments = new(
        StringComparer.OrdinalIgnoreCase)
        {
            "backup",
            "copy",
            "native",
            "old",
            "original",
            "scaled",
        };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<UiLayoutPrepareResult> PrepareAsync(
        UiLayoutPrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateResolutionPair(request.NativeResolution, request.ScaledResolution);

        string eqDirectory = ValidateEqDirectory(request.EqDirectory);
        string stateRoot = Path.GetFullPath(request.StateRoot);
        UiLayoutSessionState? pending = await LoadActiveAsync(
            eqDirectory,
            stateRoot,
            cancellationToken).ConfigureAwait(false);
        if (pending is not null)
        {
            throw new InvalidOperationException(
                "A scale-aware UI layout session is still active. Finish or recover "
                    + "that session before launching another one.");
        }

        string gameRoot = GetGameRoot(stateRoot, eqDirectory);
        Guid sessionId = Guid.NewGuid();
        string sessionDirectory = EnsureChildPath(
            gameRoot,
            Path.Combine(gameRoot, "sessions", sessionId.ToString("N")));
        string nativeDirectory = Path.Combine(sessionDirectory, "native");
        string journalPath = Path.Combine(sessionDirectory, JournalFileName);
        Directory.CreateDirectory(nativeDirectory);

        string eqClientIniPath = Path.Combine(eqDirectory, "eqclient.ini");
        IniDocument eqClient = await IniDocument.LoadAsync(
            eqClientIniPath,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<UiLayoutVideoSetting> videoSettings =
            CaptureVideoSettings(eqClient, request.ScaledResolution);

        List<UiLayoutSessionEntry> entries = [];
        Dictionary<string, byte[]> appliedByPath =
            new(StringComparer.OrdinalIgnoreCase);
        int generatedProfiles = 0;
        int reusedProfiles = 0;
        int scaledWidths = 0;
        int scaledHeights = 0;

        foreach (string layoutPath in EnumerateLiveLayoutFiles(eqDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] native = await ReadStableLayoutAsync(
                layoutPath,
                cancellationToken).ConfigureAwait(false);
            string nativeSha = ComputeSha256(native);
            string fileName = Path.GetFileName(layoutPath);
            string nativeSnapshotPath = EnsureChildPath(
                sessionDirectory,
                Path.Combine(nativeDirectory, fileName));
            await AtomicFile.WriteAllBytesAsync(
                nativeSnapshotPath,
                native,
                cancellationToken).ConfigureAwait(false);

            string profilePath = GetProfilePath(
                gameRoot,
                request.NativeResolution,
                request.ScaledResolution,
                nativeSha,
                fileName);
            byte[]? scaled = await TryReadVerifiedProfileAsync(
                profilePath,
                cancellationToken).ConfigureAwait(false);
            UiLayoutTransformResult transformed;
            if (scaled is null)
            {
                transformed = UiLayoutTransformer.Transform(
                    native,
                    request.NativeResolution,
                    request.ScaledResolution);
                scaled = transformed.Content;
                await WriteVerifiedProfileAsync(
                    profilePath,
                    scaled,
                    cancellationToken).ConfigureAwait(false);
                generatedProfiles++;
            }
            else
            {
                transformed = UiLayoutTransformer.Transform(
                    native,
                    request.NativeResolution,
                    request.ScaledResolution);
                reusedProfiles++;
            }

            scaledWidths += transformed.ScaledWidthCount;
            scaledHeights += transformed.ScaledHeightCount;
            string appliedSha = ComputeSha256(scaled);
            entries.Add(
                new UiLayoutSessionEntry(
                    fileName,
                    Path.GetFullPath(layoutPath),
                    nativeSnapshotPath,
                    nativeSha,
                    profilePath,
                    appliedSha,
                    transformed.ScaledWidthCount,
                    transformed.ScaledHeightCount,
                    ExistedAtPrepare: true));
            appliedByPath.Add(Path.GetFullPath(layoutPath), scaled);
        }

        UiLayoutSessionState state = new()
        {
            SessionId = sessionId,
            Status = UiLayoutSessionStatus.Preparing,
            PreparedAtUtc = DateTimeOffset.UtcNow,
            EqDirectory = eqDirectory,
            StateRoot = stateRoot,
            JournalPath = journalPath,
            NativeResolution = request.NativeResolution,
            ScaledResolution = request.ScaledResolution,
            EqClientIniPath = eqClientIniPath,
            VideoSettings = videoSettings,
            Entries = entries,
        };
        await WriteJournalAsync(state, createNew: true, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            foreach ((string livePath, byte[] scaled) in appliedByPath)
            {
                await AtomicFile.WriteAllBytesAsync(
                    livePath,
                    scaled,
                    cancellationToken).ConfigureAwait(false);
                await VerifyFileHashAsync(
                    livePath,
                    ComputeSha256(scaled),
                    cancellationToken).ConfigureAwait(false);
            }

            ApplyVideoSettings(eqClient, videoSettings);
            await eqClient.SaveAtomicAsync(eqClientIniPath, cancellationToken)
                .ConfigureAwait(false);
            await VerifyVideoSettingsAsync(state, cancellationToken)
                .ConfigureAwait(false);

            state = state with { Status = UiLayoutSessionStatus.Active };
            await WriteJournalAsync(state, createNew: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await RollbackAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Keep the original preparation failure. The Preparing journal and
                // native snapshots remain available for the next recovery attempt.
            }

            throw;
        }

        return new UiLayoutPrepareResult(
            state,
            entries.Count,
            generatedProfiles,
            reusedProfiles,
            scaledWidths,
            scaledHeights);
    }

    public async Task<UiLayoutCompleteResult> CompleteAsync(
        UiLayoutSessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state = await ReloadAndValidateStateAsync(state, cancellationToken)
            .ConfigureAwait(false);
        if (state.Status is UiLayoutSessionStatus.Completed
            or UiLayoutSessionStatus.RolledBack)
        {
            return new UiLayoutCompleteResult(state, 0, 0, 0, 0);
        }

        // Restoring is deliberately journaled before any live file is replaced.
        // If the controller or Windows stopped part-way through that phase, the
        // journal entries already point at the final native snapshots. Replaying
        // those snapshots is safe; re-capturing the partly restored live files as
        // scaled layouts would apply the inverse transform twice.
        if (state.Status == UiLayoutSessionStatus.Restoring)
        {
            state = await FinishNativeRestoreAsync(state, cancellationToken)
                .ConfigureAwait(false);
            return new UiLayoutCompleteResult(state, 0, 0, 0, 0);
        }

        state = state with { Status = UiLayoutSessionStatus.Completing };
        await WriteJournalAsync(state, createNew: false, cancellationToken)
            .ConfigureAwait(false);

        string gameRoot = GetGameRoot(state.StateRoot, state.EqDirectory);
        string sessionDirectory = Path.GetDirectoryName(state.JournalPath)
            ?? throw new InvalidDataException("The layout journal has no parent directory.");
        string restoredNativeDirectory = EnsureChildPath(
            sessionDirectory,
            Path.Combine(sessionDirectory, "restored-native"));
        Directory.CreateDirectory(restoredNativeDirectory);

        Dictionary<string, string> liveLayouts = EnumerateLiveLayoutFiles(
                state.EqDirectory)
            .ToDictionary(
                path => Path.GetFullPath(path),
                path => path,
                StringComparer.OrdinalIgnoreCase);
        List<UiLayoutSessionEntry> completedEntries = [];
        Dictionary<string, byte[]> nativeByLivePath =
            new(StringComparer.OrdinalIgnoreCase);
        int capturedProfiles = 0;
        int restoredExact = 0;
        int convertedBack = 0;
        int newLayouts = 0;

        foreach (UiLayoutSessionEntry entry in state.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] originalNative = await ReadManagedFileAsync(
                entry.NativeSnapshotPath,
                MaximumLayoutBytes,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    ComputeSha256(originalNative),
                    entry.NativeSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The native snapshot for '{entry.FileName}' failed checksum verification.");
            }

            if (!liveLayouts.Remove(entry.LivePath, out string? livePath))
            {
                nativeByLivePath.Add(entry.LivePath, originalNative);
                completedEntries.Add(entry);
                restoredExact++;
                continue;
            }

            byte[] scaledLive = await ReadStableLayoutAsync(
                livePath,
                cancellationToken).ConfigureAwait(false);
            string scaledLiveSha = ComputeSha256(scaledLive);
            byte[] native;
            UiLayoutSessionEntry completedEntry;
            if (string.Equals(
                    scaledLiveSha,
                    entry.AppliedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                native = originalNative;
                completedEntry = entry;
                restoredExact++;
            }
            else
            {
                UiLayoutTransformResult converted = UiLayoutTransformer.Transform(
                    scaledLive,
                    state.ScaledResolution,
                    state.NativeResolution);
                native = converted.Content;
                string nativeSha = ComputeSha256(native);
                string nativeSnapshotPath = EnsureChildPath(
                    sessionDirectory,
                    Path.Combine(restoredNativeDirectory, entry.FileName));
                await AtomicFile.WriteAllBytesAsync(
                    nativeSnapshotPath,
                    native,
                    cancellationToken).ConfigureAwait(false);
                string profilePath = GetProfilePath(
                    gameRoot,
                    state.NativeResolution,
                    state.ScaledResolution,
                    nativeSha,
                    entry.FileName);
                completedEntry = entry with
                {
                    NativeSnapshotPath = nativeSnapshotPath,
                    NativeSha256 = nativeSha,
                    ScaledProfilePath = profilePath,
                    AppliedSha256 = scaledLiveSha,
                    ScaledWidthCount = converted.ScaledWidthCount,
                    ScaledHeightCount = converted.ScaledHeightCount,
                };
                convertedBack++;
            }

            await WriteVerifiedProfileAsync(
                completedEntry.ScaledProfilePath,
                scaledLive,
                cancellationToken).ConfigureAwait(false);
            capturedProfiles++;
            nativeByLivePath.Add(entry.LivePath, native);
            completedEntries.Add(completedEntry);
        }

        foreach ((string fullPath, string livePath) in liveLayouts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(livePath);
            byte[] scaledLive = await ReadStableLayoutAsync(
                livePath,
                cancellationToken).ConfigureAwait(false);
            UiLayoutTransformResult converted = UiLayoutTransformer.Transform(
                scaledLive,
                state.ScaledResolution,
                state.NativeResolution);
            byte[] native = converted.Content;
            string nativeSha = ComputeSha256(native);
            string nativeSnapshotPath = EnsureChildPath(
                sessionDirectory,
                Path.Combine(restoredNativeDirectory, fileName));
            await AtomicFile.WriteAllBytesAsync(
                nativeSnapshotPath,
                native,
                cancellationToken).ConfigureAwait(false);
            string profilePath = GetProfilePath(
                gameRoot,
                state.NativeResolution,
                state.ScaledResolution,
                nativeSha,
                fileName);
            await WriteVerifiedProfileAsync(
                profilePath,
                scaledLive,
                cancellationToken).ConfigureAwait(false);
            completedEntries.Add(
                new UiLayoutSessionEntry(
                    fileName,
                    fullPath,
                    nativeSnapshotPath,
                    nativeSha,
                    profilePath,
                    ComputeSha256(scaledLive),
                    converted.ScaledWidthCount,
                    converted.ScaledHeightCount,
                    ExistedAtPrepare: false));
            nativeByLivePath.Add(fullPath, native);
            capturedProfiles++;
            convertedBack++;
            newLayouts++;
        }

        state = state with
        {
            Status = UiLayoutSessionStatus.Restoring,
            Entries = completedEntries,
        };
        await WriteJournalAsync(state, createNew: false, cancellationToken)
            .ConfigureAwait(false);

        state = await FinishNativeRestoreAsync(
            state,
            nativeByLivePath,
            cancellationToken).ConfigureAwait(false);

        return new UiLayoutCompleteResult(
            state,
            capturedProfiles,
            restoredExact,
            convertedBack,
            newLayouts);
    }

    private static async Task<UiLayoutSessionState> FinishNativeRestoreAsync(
        UiLayoutSessionState state,
        CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> nativeByLivePath =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (UiLayoutSessionEntry entry in state.Entries)
        {
            byte[] native = await ReadManagedFileAsync(
                entry.NativeSnapshotPath,
                MaximumLayoutBytes,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    ComputeSha256(native),
                    entry.NativeSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The native restore snapshot for '{entry.FileName}' failed verification.");
            }

            nativeByLivePath.Add(entry.LivePath, native);
        }

        return await FinishNativeRestoreAsync(
            state,
            nativeByLivePath,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<UiLayoutSessionState> FinishNativeRestoreAsync(
        UiLayoutSessionState state,
        IReadOnlyDictionary<string, byte[]> nativeByLivePath,
        CancellationToken cancellationToken)
    {
        foreach ((string livePath, byte[] native) in nativeByLivePath)
        {
            await AtomicFile.WriteAllBytesAsync(
                livePath,
                native,
                cancellationToken).ConfigureAwait(false);
            await VerifyFileHashAsync(
                livePath,
                ComputeSha256(native),
                cancellationToken).ConfigureAwait(false);
        }

        await RestoreVideoSettingsAsync(state, cancellationToken).ConfigureAwait(false);
        state = state with
        {
            Status = UiLayoutSessionStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        await WriteJournalAsync(state, createNew: false, cancellationToken)
            .ConfigureAwait(false);
        return state;
    }

    public async Task<UiLayoutSessionState> RollbackAsync(
        UiLayoutSessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state = await ReloadAndValidateStateAsync(state, cancellationToken)
            .ConfigureAwait(false);
        if (state.Status is UiLayoutSessionStatus.Completed
            or UiLayoutSessionStatus.RolledBack)
        {
            return state;
        }

        state = state with { Status = UiLayoutSessionStatus.Restoring };
        await WriteJournalAsync(state, createNew: false, cancellationToken)
            .ConfigureAwait(false);
        foreach (UiLayoutSessionEntry entry in state.Entries.Where(
            entry => entry.ExistedAtPrepare))
        {
            byte[] native = await ReadManagedFileAsync(
                entry.NativeSnapshotPath,
                MaximumLayoutBytes,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    ComputeSha256(native),
                    entry.NativeSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The rollback snapshot for '{entry.FileName}' failed verification.");
            }

            await AtomicFile.WriteAllBytesAsync(
                entry.LivePath,
                native,
                cancellationToken).ConfigureAwait(false);
            await VerifyFileHashAsync(
                entry.LivePath,
                entry.NativeSha256,
                cancellationToken).ConfigureAwait(false);
        }

        await RestoreVideoSettingsAsync(state, cancellationToken).ConfigureAwait(false);
        state = state with
        {
            Status = UiLayoutSessionStatus.RolledBack,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        await WriteJournalAsync(state, createNew: false, cancellationToken)
            .ConfigureAwait(false);
        return state;
    }

    public async Task<UiLayoutSessionState?> LoadActiveAsync(
        string eqDirectory,
        string stateRoot,
        CancellationToken cancellationToken = default)
    {
        string validatedEqDirectory = ValidateEqDirectory(eqDirectory);
        string validatedStateRoot = Path.GetFullPath(stateRoot);
        string gameRoot = GetGameRoot(validatedStateRoot, validatedEqDirectory);
        string sessionsRoot = Path.Combine(gameRoot, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return null;
        }

        List<UiLayoutSessionState> active = [];
        foreach (string sessionDirectory in Directory.EnumerateDirectories(sessionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string journalPath = Path.Combine(sessionDirectory, JournalFileName);
            if (!File.Exists(journalPath))
            {
                continue;
            }

            UiLayoutSessionState state = await ReadJournalAsync(
                journalPath,
                cancellationToken).ConfigureAwait(false);
            if (state.Status is not (UiLayoutSessionStatus.Completed
                or UiLayoutSessionStatus.RolledBack))
            {
                ValidateStatePaths(state, validatedEqDirectory, validatedStateRoot);
                active.Add(state);
            }
        }

        if (active.Count > 1)
        {
            throw new InvalidDataException(
                "Multiple active UI layout sessions were found. Recovery must finish "
                    + "before another layout can be prepared.");
        }

        return active.SingleOrDefault();
    }

    private static IReadOnlyList<UiLayoutVideoSetting> CaptureVideoSettings(
        IniDocument document,
        SpinFourKay.Core.Display.PixelSize scaledResolution)
    {
        string width = scaledResolution.Width.ToString(CultureInfo.InvariantCulture);
        string height = scaledResolution.Height.ToString(CultureInfo.InvariantCulture);
        return
        [
            Capture(document, "VideoMode", "Width", width),
            Capture(document, "VideoMode", "Height", height),
            Capture(document, "VideoMode", "WindowedWidth", width),
            Capture(document, "VideoMode", "WindowedHeight", height),
            Capture(document, "VideoMode", "Fullscreen", "0"),
            Capture(document, "Defaults", "AllowResize", "0"),
            Capture(document, "Defaults", "Maximized", "0"),
            Capture(document, "Defaults", "WindowedModeXOffset", "0"),
            Capture(document, "Defaults", "WindowedModeYOffset", "0"),
            Capture(document, "Defaults", "UIScale", "0"),
        ];
    }

    private static UiLayoutVideoSetting Capture(
        IniDocument document,
        string section,
        string key,
        string appliedValue) =>
        new(section, key, document.GetValue(section, key), appliedValue);

    private static void ApplyVideoSettings(
        IniDocument document,
        IEnumerable<UiLayoutVideoSetting> settings)
    {
        foreach (UiLayoutVideoSetting setting in settings)
        {
            document.SetValue(setting.Section, setting.Key, setting.AppliedValue);
        }
    }

    private static async Task RestoreVideoSettingsAsync(
        UiLayoutSessionState state,
        CancellationToken cancellationToken)
    {
        IniDocument document = await IniDocument.LoadAsync(
            state.EqClientIniPath,
            cancellationToken).ConfigureAwait(false);
        foreach (UiLayoutVideoSetting setting in state.VideoSettings)
        {
            if (setting.OriginalValue is null)
            {
                _ = document.RemoveKey(setting.Section, setting.Key);
            }
            else
            {
                document.SetValue(
                    setting.Section,
                    setting.Key,
                    setting.OriginalValue);
            }
        }

        await document.SaveAtomicAsync(state.EqClientIniPath, cancellationToken)
            .ConfigureAwait(false);
        await VerifyOriginalVideoSettingsAsync(state, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task VerifyVideoSettingsAsync(
        UiLayoutSessionState state,
        CancellationToken cancellationToken)
    {
        IniDocument document = await IniDocument.LoadAsync(
            state.EqClientIniPath,
            cancellationToken).ConfigureAwait(false);
        foreach (UiLayoutVideoSetting setting in state.VideoSettings)
        {
            string? actual = document.GetValue(setting.Section, setting.Key);
            if (!string.Equals(actual, setting.AppliedValue, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"eqclient.ini did not retain the prepared {setting.Section}/"
                        + $"{setting.Key} value.");
            }
        }
    }

    private static async Task VerifyOriginalVideoSettingsAsync(
        UiLayoutSessionState state,
        CancellationToken cancellationToken)
    {
        IniDocument document = await IniDocument.LoadAsync(
            state.EqClientIniPath,
            cancellationToken).ConfigureAwait(false);
        foreach (UiLayoutVideoSetting setting in state.VideoSettings)
        {
            string? actual = document.GetValue(setting.Section, setting.Key);
            if (!string.Equals(actual, setting.OriginalValue, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"eqclient.ini did not restore the original {setting.Section}/"
                        + $"{setting.Key} value.");
            }
        }
    }

    private static string[] EnumerateLiveLayoutFiles(string eqDirectory)
    {
        return Directory
            .EnumerateFiles(eqDirectory, "UI_*.ini", SearchOption.TopDirectoryOnly)
            .Where(IsLiveLayoutFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsLiveLayoutFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        string fileName = Path.GetFileName(path);
        if (!LayoutFileNamePattern.IsMatch(fileName))
        {
            return false;
        }

        return !HasBackupSuffix(fileName);
    }

    private static async Task<byte[]> ReadStableLayoutAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        FileInfo before = new(fullPath);
        if (!before.Exists
            || (before.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException(
                "A UI layout is missing or is not a regular file.",
                fullPath);
        }

        if (before.Length > MaximumLayoutBytes)
        {
            throw new IOException(
                $"'{before.Name}' exceeds the safe 16 MiB layout limit.");
        }

        byte[] content = await File.ReadAllBytesAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        FileInfo after = new(fullPath);
        if (!after.Exists
            || after.Length != before.Length
            || after.LastWriteTimeUtc != before.LastWriteTimeUtc
            || content.LongLength != before.Length)
        {
            throw new IOException(
                $"'{before.Name}' changed while its layout was being captured.");
        }

        return content;
    }

    private static async Task<byte[]> ReadManagedFileAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(Path.GetFullPath(path));
        if (!info.Exists || info.Length > maximumBytes)
        {
            throw new FileNotFoundException(
                "A managed UI layout snapshot is missing or invalid.",
                info.FullName);
        }

        return await File.ReadAllBytesAsync(info.FullName, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]?> TryReadVerifiedProfileAsync(
        string profilePath,
        CancellationToken cancellationToken)
    {
        string checksumPath = profilePath + ".sha256";
        if (!File.Exists(profilePath) || !File.Exists(checksumPath))
        {
            return null;
        }

        byte[] profile = await ReadManagedFileAsync(
            profilePath,
            MaximumLayoutBytes,
            cancellationToken).ConfigureAwait(false);
        string expected = (await File.ReadAllTextAsync(
                checksumPath,
                cancellationToken).ConfigureAwait(false))
            .Trim();
        return expected.Length == 64
            && string.Equals(
                expected,
                ComputeSha256(profile),
                StringComparison.OrdinalIgnoreCase)
                ? profile
                : null;
    }

    private static async Task WriteVerifiedProfileAsync(
        string profilePath,
        byte[] content,
        CancellationToken cancellationToken)
    {
        await AtomicFile.WriteAllBytesAsync(
            profilePath,
            content,
            cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteAllTextAsync(
            profilePath + ".sha256",
            ComputeSha256(content) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyFileHashAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        string actual = await FileHash.ComputeSha256Async(path, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"'{Path.GetFileName(path)}' failed checksum verification after writing.");
        }
    }

    private static async Task<UiLayoutSessionState> ReloadAndValidateStateAsync(
        UiLayoutSessionState state,
        CancellationToken cancellationToken)
    {
        UiLayoutSessionState persisted = await ReadJournalAsync(
            state.JournalPath,
            cancellationToken).ConfigureAwait(false);
        ValidateStatePaths(persisted, state.EqDirectory, state.StateRoot);
        if (persisted.SessionId != state.SessionId)
        {
            throw new InvalidDataException("The UI layout session identity changed.");
        }

        return persisted;
    }

    private static async Task<UiLayoutSessionState> ReadJournalAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            Path.GetFullPath(journalPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        UiLayoutSessionState state =
            await JsonSerializer.DeserializeAsync<UiLayoutSessionState>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The UI layout journal is empty.");
        if (state.FormatVersion != UiLayoutSessionState.CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"UI layout journal version {state.FormatVersion} is not supported.");
        }

        return state with { JournalPath = Path.GetFullPath(journalPath) };
    }

    private static async Task WriteJournalAsync(
        UiLayoutSessionState state,
        bool createNew,
        CancellationToken cancellationToken)
    {
        if (createNew && File.Exists(state.JournalPath))
        {
            throw new IOException(
                $"UI layout journal '{state.JournalPath}' already exists.");
        }

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        await AtomicFile.WriteAllBytesAsync(
            state.JournalPath,
            content,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateStatePaths(
        UiLayoutSessionState state,
        string eqDirectory,
        string stateRoot)
    {
        string expectedEq = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(eqDirectory));
        string expectedState = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stateRoot));
        if (!string.Equals(
                expectedEq,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.EqDirectory)),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                expectedState,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.StateRoot)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The UI layout journal is bound to a different game or state directory.");
        }

        string gameRoot = GetGameRoot(expectedState, expectedEq);
        _ = EnsureChildPath(gameRoot, state.JournalPath);
        string eqPrefix = expectedEq + Path.DirectorySeparatorChar;
        foreach (UiLayoutSessionEntry entry in state.Entries)
        {
            string livePath = Path.GetFullPath(entry.LivePath);
            if (!livePath.StartsWith(eqPrefix, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetDirectoryName(livePath),
                    expectedEq,
                    StringComparison.OrdinalIgnoreCase)
                || !IsSafeLayoutFileName(entry.FileName)
                || !string.Equals(
                    Path.GetFileName(livePath),
                    entry.FileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A UI layout journal entry escaped the game directory.");
            }

            _ = EnsureChildPath(gameRoot, entry.NativeSnapshotPath);
            _ = EnsureChildPath(gameRoot, entry.ScaledProfilePath);
        }

        string expectedIni = Path.Combine(expectedEq, "eqclient.ini");
        if (!string.Equals(
                Path.GetFullPath(state.EqClientIniPath),
                expectedIni,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The UI layout journal eqclient.ini path is outside its game directory.");
        }
    }

    private static string ValidateEqDirectory(string eqDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eqDirectory);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(eqDirectory));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"The EverQuest Legends directory was not found: '{root}'.");
        }

        string iniPath = Path.Combine(root, "eqclient.ini");
        if (!File.Exists(iniPath)
            || (File.GetAttributes(iniPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException(
                "eqclient.ini was not found as a regular file.",
                iniPath);
        }

        return root;
    }

    private static void ValidateResolutionPair(
        SpinFourKay.Core.Display.PixelSize nativeResolution,
        SpinFourKay.Core.Display.PixelSize scaledResolution)
    {
        if (nativeResolution.Width <= 0
            || nativeResolution.Height <= 0
            || scaledResolution.Width <= 0
            || scaledResolution.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nativeResolution),
                "UI layout resolutions must contain positive pixels.");
        }

        if (scaledResolution.Width >= nativeResolution.Width
            || scaledResolution.Height >= nativeResolution.Height)
        {
            throw new ArgumentException(
                "A scale-aware layout requires a source smaller than the native "
                    + "display in both dimensions.",
                nameof(scaledResolution));
        }
    }

    private static string GetGameRoot(string stateRoot, string eqDirectory)
    {
        string normalized = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(eqDirectory)).ToUpperInvariant();
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(Path.GetFullPath(stateRoot), "games", hash[..24]);
    }

    private static string GetProfilePath(
        string gameRoot,
        SpinFourKay.Core.Display.PixelSize nativeResolution,
        SpinFourKay.Core.Display.PixelSize scaledResolution,
        string nativeSha,
        string fileName)
    {
        if (!IsSafeLayoutFileName(fileName)
            || nativeSha.Length != 64
            || !nativeSha.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("A scale-profile key is invalid.");
        }

        return EnsureChildPath(
            gameRoot,
            Path.Combine(
                gameRoot,
                "profiles",
                $"{nativeResolution.Width}x{nativeResolution.Height}"
                    + $"_to_{scaledResolution.Width}x{scaledResolution.Height}",
                nativeSha[..32],
                fileName));
    }

    private static bool IsSafeLayoutFileName(string fileName)
    {
        return string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            && LayoutFileNamePattern.IsMatch(fileName)
            && !HasBackupSuffix(fileName);
    }

    private static bool HasBackupSuffix(string fileName)
    {
        string[] segments = Path.GetFileNameWithoutExtension(fileName).Split('_');
        int semanticTail = segments.Length - 1;
        if (semanticTail > 1
            && LayoutSlotSegmentPattern.IsMatch(segments[semanticTail]))
        {
            semanticTail--;
        }

        // Character names such as "Old" or "Native" are valid. Only an
        // obvious backup/copy suffix is excluded from the live layout set.
        return BackupNameSegments.Contains(segments[semanticTail]);
    }

    private static string EnsureChildPath(string root, string candidate)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A scale-aware UI layout path escaped its managed directory.");
        }

        return fullCandidate;
    }

    private static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));
}
