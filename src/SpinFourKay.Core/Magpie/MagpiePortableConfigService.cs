using System.Text.Json;
using System.Text.Json.Nodes;
using SpinFourKay.Core.Display;
using SpinFourKay.Core.IO;

namespace SpinFourKay.Core.Magpie;

/// <summary>
/// Generates Magpie v0.12 portable configuration without discarding unrelated
/// profiles or scaling modes already present in the dedicated portable folder.
/// </summary>
public sealed class MagpiePortableConfigService : IMagpiePortableConfigService
{
    public const string SmoothModeName = "SpinFOURKAYYY - Smooth FSR";
    public const string NativeClarityModeName =
        "SpinFOURKAYYY - Native Clarity";
    public const string PixelCrispModeName = "SpinFOURKAYYY - Pixel Crisp";
    public const string LanczosModeName = "SpinFOURKAYYY - Lanczos";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<MagpiePortableConfigTransaction> PrepareTransactionAsync(
        MagpieProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        string magpieDirectory = Path.GetFullPath(request.MagpieDirectory);
        string configDirectory = Path.Combine(magpieDirectory, "config");
        string configPath = Path.Combine(configDirectory, "config.json");
        Directory.CreateDirectory(configDirectory);
        bool originalFileExisted = File.Exists(configPath);
        byte[] originalContent = originalFileExisted
            ? await File.ReadAllBytesAsync(configPath, cancellationToken).ConfigureAwait(false)
            : [];
        FileAttributes? originalAttributes = originalFileExisted
            ? File.GetAttributes(configPath)
            : null;
        DateTime? originalLastWriteTimeUtc = originalFileExisted
            ? File.GetLastWriteTimeUtc(configPath)
            : null;

        JsonObject root = await LoadRootAsync(configPath, cancellationToken).ConfigureAwait(false);
        ApplySafeGlobalSettings(root);

        JsonArray scalingModes = GetOrCreateArray(root, "scalingModes");
        int smoothIndex = UpsertNamedObject(
            scalingModes,
            SmoothModeName,
            CreateFsrMode(request.RcasSharpness));
        int nativeClarityIndex = UpsertNamedObject(
            scalingModes,
            NativeClarityModeName,
            CreateNativeClarityMode(request.RcasSharpness));
        int pixelCrispIndex = UpsertNamedObject(
            scalingModes,
            PixelCrispModeName,
            CreateSingleEffectMode(PixelCrispModeName, "Nearest"));
        int lanczosIndex = UpsertNamedObject(
            scalingModes,
            LanczosModeName,
            CreateLanczosMode(request.RcasSharpness));

        int selectedMode = request.NativeClarityOnly
            ? nativeClarityIndex
            : request.Filter switch
            {
                ScalingFilter.Fsr => smoothIndex,
                ScalingFilter.NearestNeighbor => pixelCrispIndex,
                ScalingFilter.Lanczos => lanczosIndex,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.Filter,
                    "The scaling filter is not supported."),
            };

        JsonArray profiles = GetOrCreateArray(root, "profiles");
        EnsureDefaultProfile(profiles, selectedMode);
        HardenProfilesForManagedSession(profiles);
        JsonObject profile = CreateGameProfile(request, selectedMode);
        int profileIndex = UpsertGameProfile(profiles, profile, request);

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions);
        return new MagpiePortableConfigTransaction(
            configPath,
            originalFileExisted,
            originalContent.ToArray(),
            originalAttributes,
            originalLastWriteTimeUtc,
            content,
            selectedMode,
            profileIndex);
    }

    public async Task<MagpiePortableConfigResult> ApplyTransactionAsync(
        MagpiePortableConfigTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(transaction.ConfigPath);
        cancellationToken.ThrowIfCancellationRequested();

        bool currentExists = File.Exists(transaction.ConfigPath);
        byte[] currentContent = currentExists
            ? await File.ReadAllBytesAsync(transaction.ConfigPath, cancellationToken)
                .ConfigureAwait(false)
            : [];
        if (currentExists != transaction.OriginalFileExisted
            || !currentContent.AsSpan().SequenceEqual(transaction.OriginalContent))
        {
            throw new IOException(
                "Magpie's portable config changed after it was snapshotted. "
                + "No profile was written.");
        }

        bool contentChanged = !currentExists
            || !transaction.AppliedContent.AsSpan().SequenceEqual(currentContent);
        await AtomicFile.WriteAllBytesAsync(
            transaction.ConfigPath,
            transaction.AppliedContent,
            cancellationToken).ConfigureAwait(false);

        return new MagpiePortableConfigResult(
            transaction.ConfigPath,
            transaction.ScalingModeIndex,
            transaction.ProfileIndex,
            contentChanged);
    }

    public async Task<MagpiePortableConfigRollbackResult> RollbackTransactionAsync(
        MagpiePortableConfigTransaction transaction,
        bool allowChangedContentAfterEngineExit = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(transaction.ConfigPath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool currentExists = File.Exists(transaction.ConfigPath);
            byte[] currentContent = currentExists
                ? await File.ReadAllBytesAsync(transaction.ConfigPath, cancellationToken)
                    .ConfigureAwait(false)
                : [];
            bool alreadyOriginal =
                currentExists == transaction.OriginalFileExisted
                && currentContent.AsSpan().SequenceEqual(transaction.OriginalContent);
            if (alreadyOriginal)
            {
                return new MagpiePortableConfigRollbackResult(
                    MagpiePortableConfigRollbackDisposition.Restored,
                    null);
            }

            bool stillApplied =
                currentExists
                && currentContent.AsSpan().SequenceEqual(transaction.AppliedContent);
            if (!stillApplied && !allowChangedContentAfterEngineExit)
            {
                return new MagpiePortableConfigRollbackResult(
                    MagpiePortableConfigRollbackDisposition.NewerContentPreserved,
                    "Magpie's portable config changed after the transaction was applied; "
                        + "the newer file was preserved.");
            }

            string? recoveryIssue = null;
            if (!stillApplied && currentExists)
            {
                string directory = Path.GetDirectoryName(transaction.ConfigPath)
                    ?? throw new InvalidDataException(
                        "Magpie's config path has no parent directory.");
                string recoveryPath = Path.Combine(
                    directory,
                    $"{Path.GetFileName(transaction.ConfigPath)}."
                        + $"spinfourkayyy-recovery-"
                        + $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-"
                        + $"{Guid.NewGuid():N}");
                await AtomicFile.WriteAllBytesAsync(
                    recoveryPath,
                    currentContent,
                    cancellationToken).ConfigureAwait(false);
                recoveryIssue =
                    "Magpie changed its config before exiting; that post-start file "
                    + $"was preserved at '{recoveryPath}' before rollback.";
            }

            if (transaction.OriginalFileExisted)
            {
                await AtomicFile.WriteAllBytesAsync(
                    transaction.ConfigPath,
                    transaction.OriginalContent,
                    cancellationToken).ConfigureAwait(false);
                if (transaction.OriginalAttributes is { } attributes)
                {
                    File.SetAttributes(transaction.ConfigPath, attributes);
                }

                if (transaction.OriginalLastWriteTimeUtc is { } lastWriteTimeUtc)
                {
                    File.SetLastWriteTimeUtc(transaction.ConfigPath, lastWriteTimeUtc);
                }
            }
            else if (File.Exists(transaction.ConfigPath))
            {
                File.Delete(transaction.ConfigPath);
            }

            bool restoredExists = File.Exists(transaction.ConfigPath);
            byte[] restoredContent = restoredExists
                ? await File.ReadAllBytesAsync(transaction.ConfigPath, cancellationToken)
                    .ConfigureAwait(false)
                : [];
            bool restored =
                restoredExists == transaction.OriginalFileExisted
                && restoredContent.AsSpan().SequenceEqual(transaction.OriginalContent);
            return new MagpiePortableConfigRollbackResult(
                restored
                    ? MagpiePortableConfigRollbackDisposition.Restored
                    : MagpiePortableConfigRollbackDisposition.RetryRequired,
                restored
                    ? recoveryIssue
                    : "The original portable config could not be verified.");
        }
        catch (Exception exception)
        {
            return new MagpiePortableConfigRollbackResult(
                MagpiePortableConfigRollbackDisposition.RetryRequired,
                "Restoring Magpie's portable config raised an error: " + exception.Message);
        }
    }

    public async Task<MagpiePortableConfigResult> WriteAsync(
        MagpieProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        MagpiePortableConfigTransaction transaction = await PrepareTransactionAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyTransactionAsync(transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception writeFailure)
        {
            MagpiePortableConfigRollbackResult rollback =
                await RollbackTransactionAsync(
                    transaction,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (!rollback.Resolved)
            {
                throw new IOException(
                    "Writing Magpie's portable config failed, and restoring its "
                        + "pre-write snapshot could not be confirmed. "
                        + rollback.Issue,
                    writeFailure);
            }

            throw;
        }
    }

    private static async Task<JsonObject> LoadRootAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return [];
        }

        await using FileStream stream = new(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        JsonNode? node = await JsonNode.ParseAsync(
            stream,
            nodeOptions: null,
            documentOptions: default,
            cancellationToken).ConfigureAwait(false);
        return node as JsonObject
            ?? throw new InvalidDataException("Magpie's portable config root must be a JSON object.");
    }

    private static void ApplySafeGlobalSettings(JsonObject root)
    {
        root["language"] ??= string.Empty;
        root["alwaysRunAsAdmin"] = false;
        root["autoCheckForUpdates"] = false;
        root["checkForPreviewUpdates"] = false;
        root["simulateExclusiveFullscreen"] = false;
        root["allowScalingMaximized"] = false;
        root["duplicateFrameDetectionMode"] ??= 1;
    }

    private static JsonObject CreateFsrMode(double rcasSharpness)
    {
        JsonArray effects =
        [
            new JsonObject
            {
                ["name"] = @"FSR\FSR_EASU",
                ["scalingType"] = 1,
                ["scale"] = CreateScale(),
            },
        ];
        AppendClarityPasses(effects, rcasSharpness);
        return new JsonObject
        {
            ["name"] = SmoothModeName,
            ["effects"] = effects,
        };
    }

    private static JsonObject CreateNativeClarityMode(double rcasSharpness)
    {
        JsonArray effects = [];
        AppendClarityPasses(effects, rcasSharpness);
        return new JsonObject
        {
            ["name"] = NativeClarityModeName,
            ["effects"] = effects,
        };
    }

    private static JsonObject CreateLanczosMode(double rcasSharpness)
    {
        JsonArray effects =
        [
            new JsonObject
            {
                ["name"] = "Lanczos",
                ["scalingType"] = 1,
                ["scale"] = CreateScale(),
            },
        ];
        AppendClarityPasses(effects, rcasSharpness);
        return new JsonObject
        {
            ["name"] = LanczosModeName,
            ["effects"] = effects,
        };
    }

    /// <summary>
    /// Clarity up to 1.0 is one RCAS pass, whose ringing limiter keeps text
    /// edges clean. Above 1.0 the remainder drives a bundled AdaptiveSharpen
    /// pass (its own overshoot compression avoids halos), which is visibly
    /// stronger than stacking a second RCAS. Both effects and their parameter
    /// names/ranges are pinned by the audited Magpie v0.12.1 effect sources.
    /// </summary>
    private static void AppendClarityPasses(
        JsonArray effects,
        double rcasSharpness)
    {
        double rcasPass = Math.Min(rcasSharpness, 1.0);
        effects.Add(
            new JsonObject
            {
                ["name"] = @"FSR\FSR_RCAS",
                ["parameters"] = new JsonObject
                {
                    ["sharpness"] = rcasPass,
                },
            });
        double adaptiveBoost = rcasSharpness - rcasPass;
        if (adaptiveBoost > 0)
        {
            // AdaptiveSharpen's curveHeight range is 0-2 with 0.3-2.0 the
            // documented reasonable band; the 0-1 boost maps to 0-1.2.
            effects.Add(
                new JsonObject
                {
                    ["name"] = @"Sharpen\AdaptiveSharpen",
                    ["parameters"] = new JsonObject
                    {
                        ["curveHeight"] = Math.Round(
                            Math.Min(adaptiveBoost * 1.2, 2.0),
                            4),
                    },
                });
        }
    }

    private static JsonObject CreateSingleEffectMode(string modeName, string effectName)
    {
        return new JsonObject
        {
            ["name"] = modeName,
            ["effects"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = effectName,
                    ["scalingType"] = 1,
                    ["scale"] = CreateScale(),
                },
            },
        };
    }

    private static JsonObject CreateScale() =>
        new()
        {
            ["x"] = 1.0,
            ["y"] = 1.0,
        };

    private static JsonObject CreateDefaultProfile(int scalingMode)
    {
        return new JsonObject
        {
            ["scalingMode"] = scalingMode,
            ["autoScale"] = 0,
            ["captureMethod"] = 0,
            ["multiMonitorUsage"] = 0,
            ["3DGameMode"] = false,
            ["captureTitleBar"] = false,
            ["adjustCursorSpeed"] = true,
            ["disableDirectFlip"] = false,
            ["cursorScaling"] = 6,
            ["customCursorScaling"] = 1.0,
            ["cursorInterpolationMode"] = 0,
            ["croppingEnabled"] = false,
        };
    }

    private static JsonObject CreateGameProfile(
        MagpieProfileRequest request,
        int scalingMode)
    {
        bool frameLimiterEnabled = request.MaximumFrameRate is not null;
        double maximumFrameRate = request.MaximumFrameRate ?? 60.0;

        return new JsonObject
        {
            ["name"] = request.ProfileName,
            ["packaged"] = false,
            ["pathRule"] = Path.GetFullPath(request.SourceExecutablePath),
            ["classNameRule"] = request.SourceWindowClass,
            ["launcherPath"] = request.LauncherPath is null
                ? string.Empty
                : Path.GetFullPath(request.LauncherPath),
            ["autoScale"] = request.AutoScaleFullscreen ? 1 : 0,
            ["launchParameters"] = string.Empty,
            ["scalingMode"] = scalingMode,
            ["captureMethod"] = 0,
            ["multiMonitorUsage"] = 0,
            ["initialWindowedScaleFactor"] = 0,
            ["customInitialWindowedScaleFactor"] = 1.25,
            ["graphicsCardId"] = new JsonObject
            {
                ["idx"] = request.GraphicsAdapter.Index,
                ["vendorId"] = request.GraphicsAdapter.VendorId,
                ["deviceId"] = request.GraphicsAdapter.DeviceId,
            },
            ["frameRateLimiterEnabled"] = frameLimiterEnabled,
            ["maxFrameRate"] = maximumFrameRate,
            ["3DGameMode"] = request.ThreeDGameMode,
            ["captureTitleBar"] = request.CaptureTitleBar,
            ["adjustCursorSpeed"] = request.AdjustCursorSpeed,
            ["disableDirectFlip"] = request.DisableDirectFlip,
            ["cursorScaling"] = 6,
            ["customCursorScaling"] = 1.0,
            ["cursorInterpolationMode"] = 0,
            ["autoHideCursorEnabled"] = false,
            ["autoHideCursorDelay"] = 3.0,
            ["croppingEnabled"] = false,
            ["cropping"] = new JsonObject
            {
                ["left"] = 0.0,
                ["top"] = 0.0,
                ["right"] = 0.0,
                ["bottom"] = 0.0,
            },
        };
    }

    private static JsonArray GetOrCreateArray(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonArray array)
        {
            return array;
        }

        if (root[propertyName] is not null)
        {
            throw new InvalidDataException(
                $"Magpie's '{propertyName}' setting must be a JSON array.");
        }

        JsonArray created = [];
        root[propertyName] = created;
        return created;
    }

    private static int UpsertNamedObject(
        JsonArray array,
        string name,
        JsonObject replacement)
    {
        for (int index = 0; index < array.Count; index++)
        {
            if (array[index] is JsonObject item
                && string.Equals(
                    item["name"]?.GetValue<string>(),
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                array[index] = replacement;
                return index;
            }
        }

        array.Add(replacement);
        return array.Count - 1;
    }

    private static void EnsureDefaultProfile(JsonArray profiles, int scalingMode)
    {
        if (profiles.Count == 0
            || profiles[0] is not JsonObject first
            || first["name"] is not null)
        {
            profiles.Insert(0, CreateDefaultProfile(scalingMode));
            return;
        }

        first["scalingMode"] = scalingMode;
        first["autoScale"] = 0;
        first["captureMethod"] = 0;
        first["3DGameMode"] = false;
    }

    private static void HardenProfilesForManagedSession(JsonArray profiles)
    {
        foreach (JsonNode? node in profiles)
        {
            if (node is JsonObject profile)
            {
                profile["autoScale"] = 0;
                profile["3DGameMode"] = false;
            }
        }
    }

    private static int UpsertGameProfile(
        JsonArray profiles,
        JsonObject replacement,
        MagpieProfileRequest request)
    {
        string sourcePath = Path.GetFullPath(request.SourceExecutablePath);
        for (int index = 1; index < profiles.Count; index++)
        {
            if (profiles[index] is not JsonObject item)
            {
                continue;
            }

            bool sameName = string.Equals(
                item["name"]?.GetValue<string>(),
                request.ProfileName,
                StringComparison.OrdinalIgnoreCase);
            bool sameRule = string.Equals(
                    item["pathRule"]?.GetValue<string>(),
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    item["classNameRule"]?.GetValue<string>(),
                    request.SourceWindowClass,
                    StringComparison.Ordinal);

            if (sameName || sameRule)
            {
                profiles[index] = replacement;
                return index;
            }
        }

        profiles.Add(replacement);
        return profiles.Count - 1;
    }

    private static void ValidateRequest(MagpieProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MagpieDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceWindowClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileName);

        string magpieDirectory = Path.GetFullPath(request.MagpieDirectory);
        string magpieExecutable = Path.Combine(magpieDirectory, "Magpie.exe");
        if (!File.Exists(magpieExecutable))
        {
            throw new FileNotFoundException("Magpie.exe was not found.", magpieExecutable);
        }

        string sourceExecutable = Path.GetFullPath(request.SourceExecutablePath);
        if (!File.Exists(sourceExecutable))
        {
            throw new FileNotFoundException(
                "The source game executable was not found.",
                sourceExecutable);
        }

        if (request.LauncherPath is not null && !File.Exists(Path.GetFullPath(request.LauncherPath)))
        {
            throw new FileNotFoundException(
                "The configured launcher executable was not found.",
                request.LauncherPath);
        }

        if (!double.IsFinite(request.RcasSharpness)
            || request.RcasSharpness < 0
            || request.RcasSharpness > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.RcasSharpness,
                "RCAS sharpness must be between 0 and 2; values above 1 add a "
                    + "second sharpening pass.");
        }

        if (request.NativeClarityOnly
            && request.Filter != ScalingFilter.Fsr)
        {
            throw new ArgumentException(
                "Native clarity requires the FSR/RCAS quality path.",
                nameof(request));
        }

        if (request.MaximumFrameRate is { } maximumFrameRate
            && (!double.IsFinite(maximumFrameRate)
                || maximumFrameRate < 10
                || maximumFrameRate > 1000))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                maximumFrameRate,
                "Maximum frame rate must be between 10 and 1000.");
        }
    }
}
