using System.ComponentModel;
using System.Runtime.InteropServices;
using SpinFourKay.Core.Display;
using SpinFourKay.Core.Windows;

namespace SpinFourKay.Core.Magpie;

public sealed record MagpieScalingWindowInspection(
    bool IsActive,
    bool IsVisible,
    bool IsFullscreen,
    bool IsWindowedScaling,
    bool IsSafeForInput,
    nint ScalingWindowHandle,
    int? ScalingProcessId,
    nint SourceWindowHandle,
    PixelRect? ScalingWindowBounds,
    PixelRect? MonitorBounds,
    PixelRect? SourceRegion,
    PixelRect? DestinationRegion,
    CoordinateMappingValidationResult? CoordinateValidation,
    IReadOnlyList<string> Issues);

public interface IMagpieScalingWindowInspector
{
    MagpieScalingWindowInspection Inspect(nint expectedSourceWindow = default);

    Task<MagpieScalingWindowInspection> WaitForSafeAttachmentAsync(
        nint expectedSourceWindow,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<bool> StopScalingAsync(
        nint expectedSourceWindow,
        int expectedScalingProcessId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads Magpie's documented scaling-window properties in physical pixels.
/// The calling executable must declare PerMonitorV2 DPI awareness.
/// </summary>
public sealed class MagpieScalingWindowInspector : IMagpieScalingWindowInspector
{
    public const string ScalingWindowClassName =
        "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly CoordinateMappingValidator _mappingValidator;

    public MagpieScalingWindowInspector(CoordinateMappingValidator? mappingValidator = null)
    {
        _mappingValidator = mappingValidator ?? new CoordinateMappingValidator();
    }

    public MagpieScalingWindowInspection Inspect(nint expectedSourceWindow = default)
    {
        nint scalingWindow = NativeMethods.FindWindowW(ScalingWindowClassName, windowName: null);
        if (scalingWindow == nint.Zero || !NativeMethods.IsWindow(scalingWindow))
        {
            return Inactive("Magpie's scaling window is not active.");
        }

        bool isVisible = NativeMethods.IsWindowVisible(scalingWindow);
        List<string> issues = [];
        uint scalingThreadId = NativeMethods.GetWindowThreadProcessId(
            scalingWindow,
            out uint scalingProcessIdValue);
        int? scalingProcessId =
            scalingThreadId > 0
                && scalingProcessIdValue > 0
                && scalingProcessIdValue <= int.MaxValue
                ? (int)scalingProcessIdValue
                : null;
        if (scalingProcessId is null)
        {
            issues.Add("The scaling window owner process could not be identified.");
        }

        if (!isVisible)
        {
            issues.Add("Magpie's scaling window exists but is not visible or fully initialized.");
        }

        nint sourceWindow = NativeMethods.GetPropW(scalingWindow, "Magpie.SrcHWND");
        bool isWindowed = NativeMethods.GetPropW(scalingWindow, "Magpie.Windowed") != nint.Zero;
        PixelRect? sourceRegion = ReadPropertyRect(scalingWindow, "Magpie.Src");
        PixelRect? destinationRegion = ReadPropertyRect(scalingWindow, "Magpie.Dest");

        if (sourceWindow == nint.Zero || !NativeMethods.IsWindow(sourceWindow))
        {
            issues.Add("Magpie did not expose a valid source window.");
        }
        else if (expectedSourceWindow != nint.Zero && sourceWindow != expectedSourceWindow)
        {
            issues.Add("Magpie is attached to a different source window.");
        }

        if (isWindowed)
        {
            issues.Add("Magpie is using windowed scaling instead of monitor-filling fullscreen scaling.");
        }

        PixelRect? scalingBounds = TryGetWindowBounds(scalingWindow);
        PixelRect? monitorBounds = TryGetMonitorBounds(scalingWindow);
        bool fullscreen = scalingBounds is not null
            && monitorBounds is not null
            && RectsMatch(scalingBounds.Value, monitorBounds.Value, tolerance: 1);
        if (!fullscreen)
        {
            issues.Add("The scaling window does not fill its monitor.");
        }

        CoordinateMappingValidationResult? coordinateValidation = null;
        if (sourceRegion is null || destinationRegion is null)
        {
            issues.Add("Magpie's source or destination mapping rectangle is unavailable.");
        }
        else
        {
            coordinateValidation = _mappingValidator.Validate(
                sourceRegion.Value,
                destinationRegion.Value);
            issues.AddRange(coordinateValidation.Errors);

            PixelRect? sourceClientBounds = sourceWindow == nint.Zero
                ? null
                : TryGetClientBounds(sourceWindow);
            if (sourceWindow != nint.Zero && sourceClientBounds is null)
            {
                issues.Add(
                    "The game's physical client bounds are unavailable, so mouse "
                    + "coordinates cannot be validated safely.");
            }
            else if (sourceClientBounds is { } clientBounds
                && !RectsMatch(sourceRegion.Value, clientBounds, tolerance: 2))
            {
                issues.Add(
                    "Magpie's uncropped source region does not match the game's physical client area.");
            }

            if (monitorBounds is { } monitor
                && !Contains(monitor, destinationRegion.Value))
            {
                issues.Add("Magpie's destination mapping extends beyond the active monitor.");
            }
        }

        return new MagpieScalingWindowInspection(
            IsActive: true,
            IsVisible: isVisible,
            IsFullscreen: fullscreen,
            IsWindowedScaling: isWindowed,
            IsSafeForInput: issues.Count == 0,
            ScalingWindowHandle: scalingWindow,
            ScalingProcessId: scalingProcessId,
            SourceWindowHandle: sourceWindow,
            ScalingWindowBounds: scalingBounds,
            MonitorBounds: monitorBounds,
            SourceRegion: sourceRegion,
            DestinationRegion: destinationRegion,
            CoordinateValidation: coordinateValidation,
            Issues: issues);
    }

    public async Task<MagpieScalingWindowInspection> WaitForSafeAttachmentAsync(
        nint expectedSourceWindow,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (expectedSourceWindow == nint.Zero)
        {
            throw new ArgumentException(
                "A source window handle is required.",
                nameof(expectedSourceWindow));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        MagpieScalingWindowInspection latest = Inactive(
            "Magpie's scaling window has not initialized.");

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = Inspect(expectedSourceWindow);
            if (latest.IsSafeForInput)
            {
                return latest;
            }

            if (!await DelayUntilNextPollAsync(deadline, cancellationToken)
                .ConfigureAwait(false))
            {
                break;
            }
        }

        return latest;
    }

    public async Task<bool> StopScalingAsync(
        nint expectedSourceWindow,
        int expectedScalingProcessId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (expectedSourceWindow == nint.Zero)
        {
            throw new ArgumentException(
                "An exact source window is required before closing scaling.",
                nameof(expectedSourceWindow));
        }

        if (expectedScalingProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedScalingProcessId),
                "An exact scaling-window owner process ID is required.");
        }

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        nint scalingWindow = NativeMethods.FindWindowW(ScalingWindowClassName, windowName: null);
        if (scalingWindow == nint.Zero || !NativeMethods.IsWindow(scalingWindow))
        {
            return true;
        }

        nint actualSource = NativeMethods.GetPropW(scalingWindow, "Magpie.SrcHWND");
        if (expectedSourceWindow != nint.Zero && actualSource != expectedSourceWindow)
        {
            return false;
        }

        uint actualScalingThreadId = NativeMethods.GetWindowThreadProcessId(
            scalingWindow,
            out uint actualScalingProcessId);
        if (actualScalingThreadId == 0
            || actualScalingProcessId != (uint)expectedScalingProcessId)
        {
            return false;
        }

        if (!NativeMethods.PostMessageW(
            scalingWindow,
            NativeMethods.WmClose,
            nint.Zero,
            nint.Zero))
        {
            return false;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + effectiveTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint current = NativeMethods.FindWindowW(ScalingWindowClassName, windowName: null);
            if (current == nint.Zero || !NativeMethods.IsWindow(current))
            {
                return true;
            }

            if (expectedSourceWindow != nint.Zero
                && NativeMethods.GetPropW(current, "Magpie.SrcHWND") != expectedSourceWindow)
            {
                return true;
            }

            uint currentThreadId = NativeMethods.GetWindowThreadProcessId(
                current,
                out uint currentProcessId);
            if (currentThreadId == 0
                || currentProcessId != (uint)expectedScalingProcessId)
            {
                return true;
            }

            if (!await DelayUntilNextPollAsync(deadline, cancellationToken)
                .ConfigureAwait(false))
            {
                break;
            }
        }

        return false;
    }

    private static async Task<bool> DelayUntilNextPollAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        await Task.Delay(
            remaining < PollInterval ? remaining : PollInterval,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static MagpieScalingWindowInspection Inactive(string issue)
    {
        return new MagpieScalingWindowInspection(
            IsActive: false,
            IsVisible: false,
            IsFullscreen: false,
            IsWindowedScaling: false,
            IsSafeForInput: false,
            ScalingWindowHandle: nint.Zero,
            ScalingProcessId: null,
            SourceWindowHandle: nint.Zero,
            ScalingWindowBounds: null,
            MonitorBounds: null,
            SourceRegion: null,
            DestinationRegion: null,
            CoordinateValidation: null,
            Issues: [issue]);
    }

    private static PixelRect? ReadPropertyRect(nint window, string prefix)
    {
        int left = ReadSignedProperty(window, $"{prefix}Left");
        int top = ReadSignedProperty(window, $"{prefix}Top");
        int right = ReadSignedProperty(window, $"{prefix}Right");
        int bottom = ReadSignedProperty(window, $"{prefix}Bottom");

        if (right <= left || bottom <= top)
        {
            return null;
        }

        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static int ReadSignedProperty(nint window, string name)
    {
        long value = NativeMethods.GetPropW(window, name).ToInt64();
        return unchecked((int)value);
    }

    private static PixelRect? TryGetWindowBounds(nint window)
    {
        return NativeMethods.GetWindowRect(window, out NativeMethods.NativeRect rectangle)
            ? ToPixelRect(rectangle)
            : null;
    }

    private static PixelRect? TryGetClientBounds(nint window)
    {
        if (!NativeMethods.GetClientRect(window, out NativeMethods.NativeRect rectangle))
        {
            return null;
        }

        NativeMethods.NativePoint origin = new();
        if (!NativeMethods.ClientToScreen(window, ref origin))
        {
            return null;
        }

        return new PixelRect(
            origin.X,
            origin.Y,
            Math.Max(0, rectangle.Right - rectangle.Left),
            Math.Max(0, rectangle.Bottom - rectangle.Top));
    }

    private static PixelRect? TryGetMonitorBounds(nint window)
    {
        nint monitor = NativeMethods.MonitorFromWindow(
            window,
            NativeMethods.MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return null;
        }

        NativeMethods.MonitorInfo info = new()
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error);
        }

        return ToPixelRect(info.Monitor);
    }

    private static PixelRect ToPixelRect(NativeMethods.NativeRect rectangle)
    {
        return new PixelRect(
            rectangle.Left,
            rectangle.Top,
            Math.Max(0, rectangle.Right - rectangle.Left),
            Math.Max(0, rectangle.Bottom - rectangle.Top));
    }

    private static bool RectsMatch(PixelRect left, PixelRect right, int tolerance)
    {
        return Math.Abs(left.X - right.X) <= tolerance
            && Math.Abs(left.Y - right.Y) <= tolerance
            && Math.Abs(left.Width - right.Width) <= tolerance
            && Math.Abs(left.Height - right.Height) <= tolerance;
    }

    private static bool Contains(PixelRect outer, PixelRect inner)
    {
        return inner.X >= outer.X
            && inner.Y >= outer.Y
            && inner.X + inner.Width <= outer.X + outer.Width
            && inner.Y + inner.Height <= outer.Y + outer.Height;
    }
}
