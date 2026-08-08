using System.ComponentModel;
using System.Runtime.InteropServices;
using SpinFourKay.Core.Display;

namespace SpinFourKay.Core.Windows;

public sealed record OverlayWindowSnapshot(
    nint Handle,
    int ProcessId,
    string ClassName,
    string Title,
    string? ExecutablePath,
    PixelRect Bounds,
    bool IsVisible,
    bool IsMinimized,
    bool IsRootWindow,
    bool IsTopmost,
    bool IsToolWindow,
    bool IsLayered,
    bool IsInputTransparent);

public sealed record OverlayCompatibilityCaptureRequest
{
    public required PixelRect SourceRegion { get; init; }

    public required PixelRect TargetRegion { get; init; }

    public IReadOnlySet<int> ExcludedProcessIds { get; init; } = new HashSet<int>();
}

public sealed record OverlayCompatibilityUpdate(
    int TrackedWindowCount,
    int MappedWindowCount,
    IReadOnlyList<string> Warnings);

public interface IOverlayWindowApi
{
    IReadOnlyList<OverlayWindowSnapshot> EnumerateTopLevelWindows();

    OverlayWindowSnapshot? Inspect(nint windowHandle);

    bool SetTopmostWithoutActivation(nint windowHandle, bool topmost);

    bool MoveWithoutResizing(nint windowHandle, PixelRect bounds);
}

public interface IOverlayCompatibilityService
{
    OverlayCompatibilitySession Capture(OverlayCompatibilityCaptureRequest request);

    OverlayCompatibilityUpdate Activate(
        OverlayCompatibilitySession session,
        nint scalingWindowHandle,
        int scalingProcessId,
        PixelRect destinationRegion);

    OverlayCompatibilityUpdate Maintain(
        OverlayCompatibilitySession session,
        bool sourceOrScalingOutputIsForeground,
        bool discoverNewWindows = true);

    OverlayCompatibilityUpdate Restore(OverlayCompatibilitySession session);
}

public sealed class OverlayCompatibilitySession
{
    private readonly List<OverlayWindowState> _windows = [];
    private readonly List<string> _warnings = [];
    private readonly HashSet<string> _warningKeys = new(StringComparer.Ordinal);

    internal OverlayCompatibilitySession(OverlayCompatibilityCaptureRequest request)
    {
        Request = request;
        ExcludedProcessIds = request.ExcludedProcessIds.ToHashSet();
    }

    internal OverlayCompatibilityCaptureRequest Request { get; }

    internal HashSet<int> ExcludedProcessIds { get; }

    internal List<OverlayWindowState> Windows => _windows;

    public nint ScalingWindowHandle { get; internal set; }

    internal PixelRect DestinationRegion { get; set; }

    public bool IsActive { get; internal set; }

    public int CapturedWindowCount => _windows.Count;

    public int MappedWindowCount => _windows.Count(window => window.WasMapped);

    public IReadOnlyList<string> Warnings => _warnings.ToArray();

    public bool TracksWindow(nint windowHandle) =>
        windowHandle != nint.Zero
        && _windows.Any(window => window.Identity.Handle == windowHandle);

    internal void AddWarning(string key, string message)
    {
        if (_warningKeys.Add(key))
        {
            _warnings.Add(message);
        }
    }
}

internal sealed class OverlayWindowState
{
    internal OverlayWindowState(
        OverlayWindowSnapshot snapshot,
        bool allowPositionMapping)
    {
        Identity = snapshot;
        OriginalBounds = snapshot.Bounds;
        LastAppliedBounds = snapshot.Bounds;
        AllowPositionMapping = allowPositionMapping;
    }

    internal OverlayWindowSnapshot Identity { get; }

    internal PixelRect OriginalBounds { get; }

    internal PixelRect LastAppliedBounds { get; set; }

    internal bool AllowPositionMapping { get; }

    internal bool WasMapped { get; set; }

    internal bool PreserveExternalPosition { get; set; }

    internal bool IsPromotedByService { get; set; }
}

/// <summary>
/// Keeps genuine always-on-top companion windows above Magpie's focused
/// fullscreen output. Overlay pixels are never captured or resampled: only
/// native window position and z-order are adjusted.
/// </summary>
public sealed class OverlayCompatibilityService : IOverlayCompatibilityService
{
    private readonly IOverlayWindowApi _windowApi;

    public OverlayCompatibilityService(IOverlayWindowApi? windowApi = null)
    {
        _windowApi = windowApi ?? new NativeOverlayWindowApi();
    }

    public OverlayCompatibilitySession Capture(
        OverlayCompatibilityCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OverlayPlacementPlanner.ValidateRegion(request.SourceRegion, nameof(request));
        OverlayPlacementPlanner.ValidateRegion(request.TargetRegion, nameof(request));

        OverlayCompatibilitySession session = new(request);
        try
        {
            foreach (OverlayWindowSnapshot snapshot in
                _windowApi.EnumerateTopLevelWindows())
            {
                TryCaptureWindow(session, snapshot, allowPositionMapping: true);
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or NotSupportedException)
        {
            session.AddWarning(
                "capture",
                "Always-on-top companion windows could not be inventoried safely; "
                    + "their own placement was left unchanged. "
                    + exception.Message);
        }

        return session;
    }

    public OverlayCompatibilityUpdate Activate(
        OverlayCompatibilitySession session,
        nint scalingWindowHandle,
        int scalingProcessId,
        PixelRect destinationRegion)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (scalingWindowHandle == nint.Zero)
        {
            throw new ArgumentException(
                "The scaling window handle is invalid.",
                nameof(scalingWindowHandle));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scalingProcessId);
        OverlayPlacementPlanner.ValidateRegion(
            destinationRegion,
            nameof(destinationRegion));

        session.ScalingWindowHandle = scalingWindowHandle;
        session.DestinationRegion = destinationRegion;
        session.ExcludedProcessIds.Add(scalingProcessId);
        session.IsActive = true;

        foreach (OverlayWindowState window in session.Windows)
        {
            OverlayWindowSnapshot? current = _windowApi.Inspect(window.Identity.Handle);
            if (!SameWindow(window.Identity, current))
            {
                continue;
            }

            if (window.AllowPositionMapping
                && OverlayPlacementPlanner.ShouldMapPosition(
                    session.Request.SourceRegion,
                    current!.Bounds))
            {
                PixelRect mapped = OverlayPlacementPlanner.MapNativeSize(
                    session.Request.SourceRegion,
                    destinationRegion,
                    current.Bounds);
                if (mapped != current.Bounds)
                {
                    if (_windowApi.MoveWithoutResizing(current.Handle, mapped))
                    {
                        window.LastAppliedBounds = mapped;
                        window.WasMapped = true;
                    }
                    else
                    {
                        session.AddWarning(
                            $"move:{current.Handle}",
                            "A companion overlay kept its original position because "
                                + "Windows rejected a no-resize placement request.");
                    }
                }
            }

            Promote(session, window);
        }

        return CreateUpdate(session);
    }

    public OverlayCompatibilityUpdate Maintain(
        OverlayCompatibilitySession session,
        bool sourceOrScalingOutputIsForeground,
        bool discoverNewWindows = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsActive || !sourceOrScalingOutputIsForeground)
        {
            if (session.IsActive)
            {
                foreach (OverlayWindowState window in session.Windows)
                {
                    Demote(session, window);
                }
            }

            return CreateUpdate(session);
        }

        try
        {
            if (discoverNewWindows)
            {
                foreach (OverlayWindowSnapshot snapshot in
                    _windowApi.EnumerateTopLevelWindows())
                {
                    TryCaptureWindow(
                        session,
                        snapshot,
                        allowPositionMapping: false);
                }
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or NotSupportedException)
        {
            session.AddWarning(
                "runtime-capture",
                "New companion overlays could not be inventoried during this "
                    + "session. Existing protected overlays remain active. "
                    + exception.Message);
        }

        foreach (OverlayWindowState window in session.Windows)
        {
            OverlayWindowSnapshot? current = _windowApi.Inspect(window.Identity.Handle);
            if (!SameWindow(window.Identity, current))
            {
                continue;
            }

            if (window.WasMapped
                && !window.PreserveExternalPosition
                && current!.Bounds != window.LastAppliedBounds)
            {
                // The companion application or the player moved/resized the window
                // after our one-time mapping. Adopt that newer placement and never
                // overwrite it during cleanup.
                window.LastAppliedBounds = current.Bounds;
                window.PreserveExternalPosition = true;
            }

            Promote(session, window);
        }

        return CreateUpdate(session);
    }

    public OverlayCompatibilityUpdate Restore(OverlayCompatibilitySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsActive)
        {
            return CreateUpdate(session);
        }

        foreach (OverlayWindowState window in session.Windows)
        {
            if (!window.WasMapped || window.PreserveExternalPosition)
            {
                continue;
            }

            OverlayWindowSnapshot? current = _windowApi.Inspect(window.Identity.Handle);
            if (!SameWindow(window.Identity, current)
                || current!.Bounds != window.LastAppliedBounds)
            {
                continue;
            }

            if (!_windowApi.MoveWithoutResizing(
                window.Identity.Handle,
                window.OriginalBounds))
            {
                session.AddWarning(
                    $"restore:{window.Identity.Handle}",
                    "A companion overlay could not be returned to its exact "
                        + "pre-scaling position. Its current placement was preserved.");
            }
        }

        foreach (OverlayWindowState window in session.Windows)
        {
            OverlayWindowSnapshot? current = _windowApi.Inspect(window.Identity.Handle);
            if (SameWindow(window.Identity, current)
                && !_windowApi.SetTopmostWithoutActivation(
                    window.Identity.Handle,
                    window.Identity.IsTopmost))
            {
                session.AddWarning(
                    $"restore-z:{window.Identity.Handle}",
                    "A companion overlay's original topmost state could not be "
                        + "restored. Its own window policy remains in control.");
            }

            window.IsPromotedByService = false;
        }

        session.IsActive = false;
        return CreateUpdate(session);
    }

    private static void TryCaptureWindow(
        OverlayCompatibilitySession session,
        OverlayWindowSnapshot snapshot,
        bool allowPositionMapping)
    {
        if (session.Windows.Any(window => window.Identity.Handle == snapshot.Handle)
            || !OverlayPlacementPlanner.IsEligible(
                snapshot,
                session.Request.TargetRegion,
                session.ExcludedProcessIds,
                session.ScalingWindowHandle))
        {
            return;
        }

        session.Windows.Add(new OverlayWindowState(snapshot, allowPositionMapping));
    }

    private void Promote(
        OverlayCompatibilitySession session,
        OverlayWindowState window)
    {
        if (!_windowApi.SetTopmostWithoutActivation(
            window.Identity.Handle,
            topmost: true))
        {
            session.AddWarning(
                $"topmost:{window.Identity.Handle}",
                "Windows did not allow one companion overlay to stay above the "
                    + "scaled game. That overlay retained its own normal behavior.");
            return;
        }

        window.IsPromotedByService = true;
    }

    private void Demote(
        OverlayCompatibilitySession session,
        OverlayWindowState window)
    {
        if (!window.IsPromotedByService)
        {
            return;
        }

        OverlayWindowSnapshot? current = _windowApi.Inspect(window.Identity.Handle);
        if (!SameWindow(window.Identity, current))
        {
            window.IsPromotedByService = false;
            return;
        }

        if (!_windowApi.SetTopmostWithoutActivation(
            window.Identity.Handle,
            topmost: false))
        {
            session.AddWarning(
                $"background-z:{window.Identity.Handle}",
                "A companion overlay could not be lowered while another app was "
                    + "foreground. Its own window policy remains in control.");
            return;
        }

        window.IsPromotedByService = false;
    }

    private static bool SameWindow(
        OverlayWindowSnapshot expected,
        OverlayWindowSnapshot? current)
    {
        if (current is null
            || current.Handle != expected.Handle
            || current.ProcessId != expected.ProcessId
            || !string.Equals(
                current.ClassName,
                expected.ClassName,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (expected.ExecutablePath is null || current.ExecutablePath is null)
        {
            return true;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(current.ExecutablePath),
                Path.GetFullPath(expected.ExecutablePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static OverlayCompatibilityUpdate CreateUpdate(
        OverlayCompatibilitySession session) =>
        new(
            session.CapturedWindowCount,
            session.MappedWindowCount,
            session.Warnings);
}

public static class OverlayPlacementPlanner
{
    private const double MaximumOverlayAreaFraction = 0.45;

    private static readonly HashSet<string> ExcludedClasses = new(
        [
            "#32768",
            "IME",
            "MSCTFIME UI",
            "NotifyIconOverflowWindow",
            "Progman",
            "Shell_SecondaryTrayWnd",
            "Shell_TrayWnd",
            "tooltips_class32",
            "WorkerW",
            "Windows.UI.Core.CoreWindow",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedProcesses = new(
        [
            "ApplicationFrameHost.exe",
            "dwm.exe",
            "explorer.exe",
            "LockApp.exe",
            "SearchHost.exe",
            "SecurityHealthSystray.exe",
            "ShellExperienceHost.exe",
            "StartMenuExperienceHost.exe",
            "SystemSettings.exe",
            "Taskmgr.exe",
            "TextInputHost.exe",
            "Widgets.exe",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsEligible(
        OverlayWindowSnapshot snapshot,
        PixelRect targetRegion,
        IReadOnlySet<int> excludedProcessIds,
        nint scalingWindowHandle = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(excludedProcessIds);
        ValidateRegion(targetRegion, nameof(targetRegion));

        if (snapshot.Handle == nint.Zero
            || snapshot.Handle == scalingWindowHandle
            || snapshot.ProcessId <= 0
            || excludedProcessIds.Contains(snapshot.ProcessId)
            || !snapshot.IsVisible
            || snapshot.IsMinimized
            || !snapshot.IsRootWindow
            || !snapshot.IsTopmost
            || snapshot.Bounds.Width <= 0
            || snapshot.Bounds.Height <= 0
            || ExcludedClasses.Contains(snapshot.ClassName)
            || !Intersects(snapshot.Bounds, targetRegion))
        {
            return false;
        }

        long overlayArea = (long)snapshot.Bounds.Width * snapshot.Bounds.Height;
        long targetArea = (long)targetRegion.Width * targetRegion.Height;
        if (overlayArea > targetArea * MaximumOverlayAreaFraction)
        {
            return false;
        }

        string? processName = snapshot.ExecutablePath is null
            ? null
            : Path.GetFileName(snapshot.ExecutablePath);
        if (processName is not null && ExcludedProcesses.Contains(processName))
        {
            return false;
        }

        string windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        return snapshot.ExecutablePath is null
            || windowsDirectory.Length == 0
            || !IsUnderDirectory(snapshot.ExecutablePath, windowsDirectory);
    }

    public static bool ShouldMapPosition(PixelRect sourceRegion, PixelRect overlayBounds)
    {
        ValidateRegion(sourceRegion, nameof(sourceRegion));
        ValidateRegion(overlayBounds, nameof(overlayBounds));

        long intersectionArea = IntersectionArea(sourceRegion, overlayBounds);
        long overlayArea = (long)overlayBounds.Width * overlayBounds.Height;
        long centerXTwice = (long)overlayBounds.X * 2 + overlayBounds.Width;
        long centerYTwice = (long)overlayBounds.Y * 2 + overlayBounds.Height;
        bool centerInside =
            centerXTwice >= (long)sourceRegion.X * 2
            && centerXTwice <= ((long)sourceRegion.X + sourceRegion.Width) * 2
            && centerYTwice >= (long)sourceRegion.Y * 2
            && centerYTwice <= ((long)sourceRegion.Y + sourceRegion.Height) * 2;

        return centerInside || intersectionArea * 4 >= overlayArea;
    }

    public static PixelRect MapNativeSize(
        PixelRect sourceRegion,
        PixelRect destinationRegion,
        PixelRect overlayBounds)
    {
        ValidateRegion(sourceRegion, nameof(sourceRegion));
        ValidateRegion(destinationRegion, nameof(destinationRegion));
        ValidateRegion(overlayBounds, nameof(overlayBounds));

        int x = MapAxis(
            sourceRegion.X,
            sourceRegion.Width,
            destinationRegion.X,
            destinationRegion.Width,
            overlayBounds.X,
            overlayBounds.Width);
        int y = MapAxis(
            sourceRegion.Y,
            sourceRegion.Height,
            destinationRegion.Y,
            destinationRegion.Height,
            overlayBounds.Y,
            overlayBounds.Height);
        return new PixelRect(x, y, overlayBounds.Width, overlayBounds.Height);
    }

    public static void ValidateRegion(PixelRect region, string parameterName)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The physical pixel region must have a positive size.");
        }
    }

    private static int MapAxis(
        int sourceOrigin,
        int sourceLength,
        int destinationOrigin,
        int destinationLength,
        int overlayOrigin,
        int overlayLength)
    {
        double scale = (double)destinationLength / sourceLength;
        double overlayCenter = overlayOrigin + overlayLength / 2.0;
        double relativeCenter = (overlayCenter - sourceOrigin) / sourceLength;
        double mapped;
        if (relativeCenter <= 1.0 / 3.0)
        {
            mapped = destinationOrigin
                + (overlayOrigin - sourceOrigin) * scale;
        }
        else if (relativeCenter >= 2.0 / 3.0)
        {
            double sourceFarEdge = (double)sourceOrigin + sourceLength;
            double destinationFarEdge =
                (double)destinationOrigin + destinationLength;
            double farGap = sourceFarEdge - (overlayOrigin + overlayLength);
            mapped = destinationFarEdge - farGap * scale - overlayLength;
        }
        else
        {
            double sourceCenter = sourceOrigin + sourceLength / 2.0;
            double destinationCenter =
                destinationOrigin + destinationLength / 2.0;
            mapped = destinationCenter
                + (overlayCenter - sourceCenter) * scale
                - overlayLength / 2.0;
        }

        int rounded = checked((int)Math.Round(
            mapped,
            MidpointRounding.AwayFromZero));
        int maximum = checked(
            destinationOrigin + Math.Max(0, destinationLength - overlayLength));
        return Math.Clamp(rounded, destinationOrigin, maximum);
    }

    private static long IntersectionArea(PixelRect left, PixelRect right)
    {
        long intersectionWidth = Math.Max(
            0L,
            Math.Min(
                (long)left.X + left.Width,
                (long)right.X + right.Width)
                - Math.Max(left.X, right.X));
        long intersectionHeight = Math.Max(
            0L,
            Math.Min(
                (long)left.Y + left.Height,
                (long)right.Y + right.Height)
                - Math.Max(left.Y, right.Y));
        return intersectionWidth * intersectionHeight;
    }

    private static bool Intersects(PixelRect left, PixelRect right) =>
        IntersectionArea(left, right) > 0;

    private static bool IsUnderDirectory(string filePath, string directory)
    {
        try
        {
            string fullPath = Path.GetFullPath(filePath);
            string fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(
                fullDirectory,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return true;
        }
    }
}

internal sealed class NativeOverlayWindowApi : IOverlayWindowApi
{
    public IReadOnlyList<OverlayWindowSnapshot> EnumerateTopLevelWindows()
    {
        List<OverlayWindowSnapshot> windows = [];
        NativeMethods.EnumWindowsCallback callback = (windowHandle, parameter) =>
        {
            _ = parameter;
            OverlayWindowSnapshot? snapshot = Inspect(windowHandle);
            if (snapshot is not null)
            {
                windows.Add(snapshot);
            }

            return true;
        };

        if (!NativeMethods.EnumWindows(callback, nint.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error);
            }
        }

        return windows;
    }

    public OverlayWindowSnapshot? Inspect(nint windowHandle)
    {
        if (windowHandle == nint.Zero
            || !NativeMethods.IsWindow(windowHandle)
            || !NativeMethods.GetWindowRect(
                windowHandle,
                out NativeMethods.NativeRect nativeBounds))
        {
            return null;
        }

        uint threadId = NativeMethods.GetWindowThreadProcessId(
            windowHandle,
            out uint processIdValue);
        if (threadId == 0
            || processIdValue == 0
            || processIdValue > int.MaxValue)
        {
            return null;
        }

        int processId = checked((int)processIdValue);
        uint extendedStyle = unchecked(
            (uint)NativeMethods.GetWindowLongPtr(
                windowHandle,
                NativeMethods.GwlExStyle).ToInt64());
        return new OverlayWindowSnapshot(
            windowHandle,
            processId,
            ReadClassName(windowHandle),
            ReadWindowTitle(windowHandle),
            ProcessDiscoveryService.TryGetExecutablePath(processId),
            new PixelRect(
                nativeBounds.Left,
                nativeBounds.Top,
                Math.Max(0, nativeBounds.Right - nativeBounds.Left),
                Math.Max(0, nativeBounds.Bottom - nativeBounds.Top)),
            NativeMethods.IsWindowVisible(windowHandle),
            NativeMethods.IsIconic(windowHandle),
            NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot)
                == windowHandle,
            (extendedStyle & NativeMethods.WsExTopmost) != 0,
            (extendedStyle & NativeMethods.WsExToolWindow) != 0,
            (extendedStyle & NativeMethods.WsExLayered) != 0,
            (extendedStyle & NativeMethods.WsExTransparent) != 0);
    }

    public bool SetTopmostWithoutActivation(nint windowHandle, bool topmost) =>
        NativeMethods.SetWindowPos(
            windowHandle,
            topmost
                ? NativeMethods.HwndTopmost
                : NativeMethods.HwndNotTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove
                | NativeMethods.SwpNoSize
                | NativeMethods.SwpNoActivate
                | NativeMethods.SwpNoOwnerZOrder);

    public bool MoveWithoutResizing(nint windowHandle, PixelRect bounds) =>
        NativeMethods.SetWindowPos(
            windowHandle,
            nint.Zero,
            bounds.X,
            bounds.Y,
            0,
            0,
            NativeMethods.SwpNoSize
                | NativeMethods.SwpNoZOrder
                | NativeMethods.SwpNoActivate
                | NativeMethods.SwpNoOwnerZOrder);

    private static string ReadClassName(nint windowHandle)
    {
        char[] buffer = new char[256];
        int length = NativeMethods.GetClassNameW(
            windowHandle,
            buffer,
            buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static string ReadWindowTitle(nint windowHandle)
    {
        int length = NativeMethods.GetWindowTextLengthW(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length + 1];
        int copied = NativeMethods.GetWindowTextW(
            windowHandle,
            buffer,
            buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
    }
}
