using Microsoft.Win32;

namespace SpinFourKay.Core.Windows;

public sealed record DpiCompatibilitySnapshot(
    string ExecutablePath,
    bool ValueExisted,
    string? OriginalValue,
    RegistryValueKind OriginalKind,
    string AppliedValue);

public sealed record DpiRestoreResult(
    bool WasRestored,
    bool RestoredExactly,
    bool CurrentValueChangedAfterApply,
    bool DpiTokenConflict,
    string? ResultValue);

public sealed class DpiCompatibilityValueChangedException : InvalidOperationException
{
    public DpiCompatibilityValueChangedException(string executablePath)
        : base(
            "The DPI compatibility value changed after it was captured. "
            + "Preparation stopped without overwriting the newer value.")
    {
        ExecutablePath = executablePath;
    }

    public string ExecutablePath { get; }
}

public interface IDpiCompatibilityService
{
    DpiCompatibilitySnapshot Capture(string executablePath);

    DpiCompatibilitySnapshot ApplyApplicationAware(string executablePath);

    DpiCompatibilitySnapshot ApplyApplicationAware(
        DpiCompatibilitySnapshot intendedChange);

    DpiRestoreResult Restore(DpiCompatibilitySnapshot snapshot);
}

/// <summary>
/// Manages only the current user's AppCompat entry and preserves unrelated flags.
/// </summary>
public sealed class DpiCompatibilityService : IDpiCompatibilityService
{
    public const string LayersRegistryPath =
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    private static readonly object RegistryLock = new();
    private static readonly HashSet<string> DpiFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "HIGHDPIAWARE",
        "DPIUNAWARE",
        "GDIDPISCALING",
    };

    public DpiCompatibilitySnapshot Capture(string executablePath)
    {
        string fullPath = ValidateExecutablePath(executablePath);
        lock (RegistryLock)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                LayersRegistryPath,
                writable: false);
            DpiCompatibilitySnapshot snapshot = CaptureCore(
                key,
                fullPath,
                appliedValue: string.Empty);

            // Persist the deterministic value we intend to own before the registry
            // is mutated. Recovery can then distinguish our write even if the
            // process stops between SetValue and the next journal update.
            return snapshot with
            {
                AppliedValue = MergeApplicationAware(snapshot.OriginalValue),
            };
        }
    }

    public DpiCompatibilitySnapshot ApplyApplicationAware(string executablePath)
    {
        return ApplyApplicationAware(Capture(executablePath));
    }

    public DpiCompatibilitySnapshot ApplyApplicationAware(
        DpiCompatibilitySnapshot intendedChange)
    {
        ArgumentNullException.ThrowIfNull(intendedChange);
        ValidateSnapshotIntent(intendedChange);
        string fullPath = ValidateExecutablePath(intendedChange.ExecutablePath);
        string intendedPath = Path.GetFullPath(intendedChange.ExecutablePath);
        if (!string.Equals(fullPath, intendedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The DPI compatibility intent does not target the requested executable.");
        }

        string appliedValue = string.IsNullOrEmpty(intendedChange.AppliedValue)
            ? MergeApplicationAware(intendedChange.OriginalValue)
            : intendedChange.AppliedValue;

        lock (RegistryLock)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                LayersRegistryPath,
                writable: true)
                ?? throw new InvalidOperationException(
                    "Windows did not open the per-user compatibility registry key.");

            string? current = ReadStringValue(
                key,
                fullPath,
                out bool currentExists,
                out RegistryValueKind currentKind);
            bool stillOriginal = RegistryValueMatches(
                currentExists,
                current,
                intendedChange.ValueExisted,
                intendedChange.OriginalValue)
                && (!currentExists
                    || currentKind == intendedChange.OriginalKind);
            if (!stillOriginal)
            {
                throw new DpiCompatibilityValueChangedException(fullPath);
            }

            if (!currentExists
                || !string.Equals(current, appliedValue, StringComparison.Ordinal))
            {
                key.SetValue(fullPath, appliedValue, RegistryValueKind.String);
            }

            return intendedChange with
            {
                ExecutablePath = fullPath,
                AppliedValue = appliedValue,
            };
        }
    }

    public DpiRestoreResult Restore(DpiCompatibilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshotIntent(snapshot);
        string fullPath = Path.GetFullPath(snapshot.ExecutablePath);

        lock (RegistryLock)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                LayersRegistryPath,
                writable: true)
                ?? throw new InvalidOperationException(
                    "Windows did not open the per-user compatibility registry key.");

            string? current = ReadStringValue(
                key,
                fullPath,
                out bool currentExists,
                out RegistryValueKind currentKind);
            bool exactOriginal = RegistryValueMatches(
                currentExists,
                current,
                snapshot.ValueExisted,
                snapshot.OriginalValue)
                && (!currentExists || currentKind == snapshot.OriginalKind);
            if (exactOriginal)
            {
                return new DpiRestoreResult(
                    WasRestored: true,
                    RestoredExactly: true,
                    CurrentValueChangedAfterApply: false,
                    DpiTokenConflict: false,
                    ResultValue: snapshot.OriginalValue);
            }

            // Format-version 1 journals created by early builds stored an empty
            // AppliedValue until after SetValue. Infer the exact deterministic
            // intent so those interrupted sessions can still remove our own token.
            string expectedAppliedValue = ResolveExpectedAppliedValue(snapshot);
            bool exactAppliedValue = IsOwnedAppliedValue(
                currentExists,
                current,
                currentKind,
                snapshot);

            if (exactAppliedValue)
            {
                RestoreExactValue(key, snapshot);
                return new DpiRestoreResult(
                    WasRestored: true,
                    RestoredExactly: true,
                    CurrentValueChangedAfterApply: false,
                    DpiTokenConflict: false,
                    ResultValue: snapshot.OriginalValue);
            }

            if (currentExists && currentKind != RegistryValueKind.String)
            {
                return new DpiRestoreResult(
                    WasRestored: false,
                    RestoredExactly: false,
                    CurrentValueChangedAfterApply: true,
                    DpiTokenConflict: true,
                    ResultValue: current);
            }

            LayerTokens currentTokens = LayerTokens.Parse(current);
            LayerTokens appliedTokens = LayerTokens.Parse(expectedAppliedValue);
            HashSet<string> currentDpi = currentTokens.Flags
                .Where(DpiFlags.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> appliedDpi = appliedTokens.Flags
                .Where(DpiFlags.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!currentDpi.SetEquals(appliedDpi))
            {
                // A third party deliberately changed DPI behavior after preparation.
                // Preserve it and require an explicit user choice instead of guessing.
                return new DpiRestoreResult(
                    WasRestored: false,
                    RestoredExactly: false,
                    CurrentValueChangedAfterApply: true,
                    DpiTokenConflict: true,
                    ResultValue: current);
            }

            // Another tool may have added an unrelated compatibility layer after us.
            // Restore the original DPI tokens while retaining those newer unrelated flags.
            string? merged = MergeOriginalDpiFlags(current, snapshot);
            if (string.IsNullOrEmpty(merged))
            {
                key.DeleteValue(fullPath, throwOnMissingValue: false);
            }
            else
            {
                RegistryValueKind kind = snapshot.ValueExisted
                    ? snapshot.OriginalKind
                    : RegistryValueKind.String;
                key.SetValue(fullPath, merged, kind);
            }

            return new DpiRestoreResult(
                WasRestored: true,
                RestoredExactly: false,
                CurrentValueChangedAfterApply: true,
                DpiTokenConflict: false,
                ResultValue: merged);
        }
    }

    private static DpiCompatibilitySnapshot CaptureCore(
        RegistryKey? key,
        string fullPath,
        string appliedValue)
    {
        if (key is null)
        {
            return new DpiCompatibilitySnapshot(
                fullPath,
                ValueExisted: false,
                OriginalValue: null,
                RegistryValueKind.String,
                appliedValue);
        }

        string? value = ReadStringValue(key, fullPath, out bool exists, out RegistryValueKind kind);
        return new DpiCompatibilitySnapshot(
            fullPath,
            exists,
            value,
            kind,
            appliedValue);
    }

    private static string? ReadStringValue(
        RegistryKey key,
        string valueName,
        out bool exists,
        out RegistryValueKind kind)
    {
        exists = key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);
        if (!exists)
        {
            kind = RegistryValueKind.String;
            return null;
        }

        kind = key.GetValueKind(valueName);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            throw new InvalidDataException(
                $"The compatibility entry for '{valueName}' is not a string value.");
        }

        return key.GetValue(
            valueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static bool RegistryValueMatches(
        bool actualExists,
        string? actualValue,
        bool expectedExists,
        string? expectedValue)
    {
        return actualExists == expectedExists
            && (!actualExists
                || string.Equals(actualValue, expectedValue, StringComparison.Ordinal));
    }

    private static string ResolveExpectedAppliedValue(
        DpiCompatibilitySnapshot snapshot)
    {
        return string.IsNullOrEmpty(snapshot.AppliedValue)
            ? MergeApplicationAware(snapshot.OriginalValue)
            : snapshot.AppliedValue;
    }

    private static void ValidateSnapshotIntent(DpiCompatibilitySnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.ExecutablePath);
        if (!snapshot.ValueExisted && snapshot.OriginalValue is not null)
        {
            throw new InvalidDataException(
                "A non-existent DPI compatibility value cannot have original content.");
        }

        if (snapshot.ValueExisted
            && snapshot.OriginalKind is not (
                RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            throw new InvalidDataException(
                "The original DPI compatibility value must be a string.");
        }

        string deterministicIntent = MergeApplicationAware(snapshot.OriginalValue);
        if (!string.IsNullOrEmpty(snapshot.AppliedValue)
            && !string.Equals(
                snapshot.AppliedValue,
                deterministicIntent,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The persisted DPI compatibility intent is not deterministic for "
                + "the captured original value.");
        }
    }

    private static bool IsOwnedAppliedValue(
        bool currentExists,
        string? currentValue,
        RegistryValueKind currentKind,
        DpiCompatibilitySnapshot snapshot)
    {
        return currentExists
            && currentKind == RegistryValueKind.String
            && string.Equals(
                currentValue,
                ResolveExpectedAppliedValue(snapshot),
                StringComparison.Ordinal);
    }

    private static void RestoreExactValue(RegistryKey key, DpiCompatibilitySnapshot snapshot)
    {
        if (!snapshot.ValueExisted)
        {
            key.DeleteValue(snapshot.ExecutablePath, throwOnMissingValue: false);
            return;
        }

        key.SetValue(
            snapshot.ExecutablePath,
            snapshot.OriginalValue ?? string.Empty,
            snapshot.OriginalKind);
    }

    private static string MergeApplicationAware(string? existing)
    {
        LayerTokens parsed = LayerTokens.Parse(existing);
        List<string> flags = parsed.Flags
            .Where(flag => !DpiFlags.Contains(flag))
            .ToList();
        flags.Add("HIGHDPIAWARE");
        return new LayerTokens(parsed.Markers, flags).Format(ensureUserMarker: true);
    }

    private static string? MergeOriginalDpiFlags(
        string? current,
        DpiCompatibilitySnapshot snapshot)
    {
        LayerTokens currentTokens = LayerTokens.Parse(current);
        LayerTokens originalTokens = LayerTokens.Parse(snapshot.OriginalValue);

        List<string> flags = currentTokens.Flags
            .Where(flag => !DpiFlags.Contains(flag))
            .ToList();
        foreach (string originalDpiFlag in originalTokens.Flags.Where(DpiFlags.Contains))
        {
            if (!flags.Contains(originalDpiFlag, StringComparer.OrdinalIgnoreCase))
            {
                flags.Add(originalDpiFlag);
            }
        }

        List<string> markers = currentTokens.Markers.Count > 0
            ? currentTokens.Markers.ToList()
            : originalTokens.Markers.ToList();
        LayerTokens result = new(markers, flags);

        if (result.Flags.Count == 0)
        {
            return null;
        }

        return result.Format(ensureUserMarker: true);
    }

    private static string ValidateExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The executable was not found.", fullPath);
        }

        return fullPath;
    }

    private sealed record LayerTokens(
        IReadOnlyList<string> Markers,
        IReadOnlyList<string> Flags)
    {
        private static readonly HashSet<string> MarkerTokens = new(StringComparer.Ordinal)
        {
            "~",
            "#",
            "!",
            "^",
        };

        public static LayerTokens Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new LayerTokens([], []);
            }

            string[] tokens = value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<string> markers = [];
            List<string> flags = [];

            foreach (string token in tokens)
            {
                if (MarkerTokens.Contains(token))
                {
                    if (!markers.Contains(token, StringComparer.Ordinal))
                    {
                        markers.Add(token);
                    }
                }
                else if (!flags.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    flags.Add(token);
                }
            }

            return new LayerTokens(markers, flags);
        }

        public string Format(bool ensureUserMarker)
        {
            List<string> tokens = Markers.ToList();
            if (ensureUserMarker && !tokens.Contains("~", StringComparer.Ordinal))
            {
                tokens.Insert(0, "~");
            }

            tokens.AddRange(Flags);
            return string.Join(' ', tokens);
        }
    }
}
