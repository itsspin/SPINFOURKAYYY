using System.Windows;
using System.Windows.Media;
using SpinFourKay.Core.Display;
using SpinFourKay.Core.Windows;

namespace SpinFourKay.App.Infrastructure;

internal sealed record DisplaySnapshot(
    string DeviceName,
    nint MonitorHandle,
    PixelRect Bounds,
    int DpiPercentage)
{
    public PixelSize Resolution => Bounds.Size;

    public static DisplaySnapshot Capture(
        Window window,
        MonitorDescriptor monitor,
        string deviceName)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);

        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int dpiPercentage = checked((int)Math.Round(dpi.DpiScaleX * 100));

        return new DisplaySnapshot(
            deviceName,
            monitor.Handle,
            monitor.Bounds,
            dpiPercentage);
    }
}
