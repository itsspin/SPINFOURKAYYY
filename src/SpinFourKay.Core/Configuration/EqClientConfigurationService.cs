using System.Globalization;
using SpinFourKay.Core.Display;

namespace SpinFourKay.Core.Configuration;

public enum NativeUiScaleStatus
{
    Missing,
    Valid,
    Invalid,
}

public sealed record EqClientVideoSettings(
    PixelSize? FullscreenResolution,
    PixelSize? WindowedResolution,
    bool? Fullscreen,
    bool? AllowResize,
    bool? Maximized,
    int? ChatFontSize)
{
    public int? NativeUiScaleIndex { get; init; }

    public NativeUiScaleStatus NativeUiScaleStatus { get; init; }
}

public sealed record EqClientConfigurationApplyResult(
    int? AppliedChatFontSize,
    IReadOnlyList<string> Warnings);

public interface IEqClientConfigurationService
{
    Task<EqClientVideoSettings> ReadAsync(
        string eqClientIniPath,
        CancellationToken cancellationToken = default);

    Task ApplyWindowedResolutionAsync(
        string eqClientIniPath,
        PixelSize sourceResolution,
        CancellationToken cancellationToken = default);

    Task<EqClientConfigurationApplyResult> ApplyWindowedResolutionAsync(
        string eqClientIniPath,
        PixelSize sourceResolution,
        int chatFontSizeIncrease,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies only the small set of EverQuest video keys needed by the scaler.
/// All other INI content is retained by <see cref="IniDocument"/>.
/// </summary>
public sealed class EqClientConfigurationService : IEqClientConfigurationService
{
    public async Task<EqClientVideoSettings> ReadAsync(
        string eqClientIniPath,
        CancellationToken cancellationToken = default)
    {
        IniDocument document = await IniDocument.LoadAsync(
            ValidatePath(eqClientIniPath),
            cancellationToken).ConfigureAwait(false);

        string? nativeUiScaleText = document.GetValue("Defaults", "UIScale");
        bool nativeUiScaleIsValid = int.TryParse(
            nativeUiScaleText,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int nativeUiScaleIndex)
            && nativeUiScaleIndex is >= 0 and <= 4;

        return new EqClientVideoSettings(
            ReadSize(document, "Width", "Height"),
            ReadSize(document, "WindowedWidth", "WindowedHeight"),
            ReadBoolean(document.GetValue("VideoMode", "Fullscreen")),
            ReadBoolean(document.GetValue("Defaults", "AllowResize")),
            ReadBoolean(document.GetValue("Defaults", "Maximized")),
            ReadInteger(document.GetValue("Defaults", "ChatFontSize")))
        {
            NativeUiScaleIndex =
                nativeUiScaleIsValid ? nativeUiScaleIndex : null,
            NativeUiScaleStatus = nativeUiScaleText is null
                ? NativeUiScaleStatus.Missing
                : nativeUiScaleIsValid
                    ? NativeUiScaleStatus.Valid
                    : NativeUiScaleStatus.Invalid,
        };
    }

    public async Task ApplyWindowedResolutionAsync(
        string eqClientIniPath,
        PixelSize sourceResolution,
        CancellationToken cancellationToken = default)
    {
        _ = await ApplyWindowedResolutionAsync(
            eqClientIniPath,
            sourceResolution,
            chatFontSizeIncrease: 0,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EqClientConfigurationApplyResult> ApplyWindowedResolutionAsync(
        string eqClientIniPath,
        PixelSize sourceResolution,
        int chatFontSizeIncrease,
        CancellationToken cancellationToken = default)
    {
        if (chatFontSizeIncrease is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chatFontSizeIncrease),
                "Chat font size increase must be 0, 1, or 2.");
        }

        string path = ValidatePath(eqClientIniPath);
        IniDocument document = await IniDocument.LoadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        List<string> warnings = [];

        string width = sourceResolution.Width.ToString(CultureInfo.InvariantCulture);
        string height = sourceResolution.Height.ToString(CultureInfo.InvariantCulture);

        document.SetValue("VideoMode", "Width", width);
        document.SetValue("VideoMode", "Height", height);
        document.SetValue("VideoMode", "WindowedWidth", width);
        document.SetValue("VideoMode", "WindowedHeight", height);
        document.SetValue("VideoMode", "Fullscreen", "0");
        document.SetValue("Defaults", "AllowResize", "0");
        document.SetValue("Defaults", "Maximized", "0");
        document.SetValue("Defaults", "WindowedModeXOffset", "0");
        document.SetValue("Defaults", "WindowedModeYOffset", "0");

        string? nativeUiScaleText = document.GetValue("Defaults", "UIScale");
        if (nativeUiScaleText is null)
        {
            document.SetValue("Defaults", "UIScale", "0");
        }
        else
        {
            if (int.TryParse(
                nativeUiScaleText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int nativeUiScaleIndex)
                && nativeUiScaleIndex is >= 0 and <= 4)
            {
                document.SetValue("Defaults", "UIScale", "0");
            }
            else
            {
                throw new InvalidDataException(
                    "UIScale is present but malformed or outside the supported 0-4 "
                        + "range. No client settings were written. Correct the value "
                        + "in Legends first, or restore a known-good eqclient.ini.");
            }
        }

        int? appliedChatFontSize = null;
        if (chatFontSizeIncrease > 0)
        {
            string? currentText = document.GetValue("Defaults", "ChatFontSize");
            if (int.TryParse(
                currentText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int current)
                && current is >= 0 and <= 10)
            {
                appliedChatFontSize = Math.Clamp(current + chatFontSizeIncrease, 0, 10);
                document.SetValue(
                    "Defaults",
                    "ChatFontSize",
                    appliedChatFontSize.Value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                warnings.Add(
                    "ChatFontSize was missing or outside the safe 0-10 range; "
                    + "the existing value was left unchanged.");
            }
        }

        await document.SaveAtomicAsync(path, cancellationToken).ConfigureAwait(false);
        return new EqClientConfigurationApplyResult(appliedChatFontSize, warnings);
    }

    private static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The EverQuest client configuration was not found.", fullPath);
        }

        return fullPath;
    }

    private static PixelSize? ReadSize(IniDocument document, string widthKey, string heightKey)
    {
        string? widthText = document.GetValue("VideoMode", widthKey);
        string? heightText = document.GetValue("VideoMode", heightKey);

        return int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
            && int.TryParse(heightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)
            && width > 0
            && height > 0
                ? new PixelSize(width, height)
                : null;
    }

    private static bool? ReadBoolean(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ when bool.TryParse(value, out bool result) => result,
            _ => null,
        };
    }

    private static int? ReadInteger(string? value)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int result)
                ? result
                : null;
    }
}
