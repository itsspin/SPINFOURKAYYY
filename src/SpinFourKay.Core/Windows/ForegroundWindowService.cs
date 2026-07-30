using SpinFourKay.Core.Display;

namespace SpinFourKay.Core.Windows;

public sealed record WindowRuntimeSnapshot(
    nint Handle,
    bool IsValid,
    int? ProcessId,
    string? ClassName,
    PixelRect? ClientBounds,
    nint MonitorHandle);

public interface IForegroundWindowService
{
    nint GetForegroundWindowHandle();

    WindowRuntimeSnapshot InspectWindow(nint windowHandle);
}

public sealed class ForegroundWindowService : IForegroundWindowService
{
    public nint GetForegroundWindowHandle()
    {
        return NativeMethods.GetForegroundWindow();
    }

    public WindowRuntimeSnapshot InspectWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero || !NativeMethods.IsWindow(windowHandle))
        {
            return new WindowRuntimeSnapshot(
                windowHandle,
                IsValid: false,
                ProcessId: null,
                ClassName: null,
                ClientBounds: null,
                MonitorHandle: nint.Zero);
        }

        uint threadId = NativeMethods.GetWindowThreadProcessId(
            windowHandle,
            out uint processIdValue);
        int? processId =
            threadId > 0
                && processIdValue > 0
                && processIdValue <= int.MaxValue
                ? (int)processIdValue
                : null;

        PixelRect? clientBounds = null;
        if (NativeMethods.GetClientRect(
                windowHandle,
                out NativeMethods.NativeRect clientRectangle))
        {
            NativeMethods.NativePoint clientOrigin = new();
            if (NativeMethods.ClientToScreen(windowHandle, ref clientOrigin))
            {
                clientBounds = new PixelRect(
                    clientOrigin.X,
                    clientOrigin.Y,
                    Math.Max(0, clientRectangle.Right - clientRectangle.Left),
                    Math.Max(0, clientRectangle.Bottom - clientRectangle.Top));
            }
        }

        char[] classNameBuffer = GC.AllocateUninitializedArray<char>(256);
        int classNameLength = NativeMethods.GetClassNameW(
            windowHandle,
            classNameBuffer,
            classNameBuffer.Length);
        string? className = classNameLength > 0
            ? new string(classNameBuffer, 0, classNameLength)
            : null;
        nint monitorHandle = NativeMethods.MonitorFromWindow(
            windowHandle,
            NativeMethods.MonitorDefaultToNearest);

        return new WindowRuntimeSnapshot(
            windowHandle,
            IsValid: true,
            processId,
            className,
            clientBounds,
            monitorHandle);
    }
}
