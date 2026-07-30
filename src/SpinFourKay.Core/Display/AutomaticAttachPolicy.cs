using SpinFourKay.Core.Windows;

namespace SpinFourKay.Core.Display;

/// <summary>
/// Deterministic, side-effect-free choices used by the one-run Attach path.
/// </summary>
public static class AutomaticAttachPolicy
{
    public static FineUiScale RecommendScale(PixelSize display)
    {
        double scale = display.Height switch
        {
            >= 1_800 => 1.25,
            >= 1_300 => 1.15,
            _ => 1.10,
        };
        return FineUiScale.FromSlider(scale);
    }

    public static MonitorDescriptor ResolveTargetMonitor(
        PixelRect clientBounds,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (clientBounds.Width <= 0 || clientBounds.Height <= 0)
        {
            throw new InvalidOperationException(
                "The running Legends client has no usable physical-pixel area.");
        }

        (MonitorDescriptor Monitor, long Intersection)[] intersections =
            monitors
                .Select(
                    monitor => (
                        monitor,
                        IntersectionArea(clientBounds, monitor.Bounds)))
                .OrderByDescending(candidate => candidate.Item2)
                .ToArray();
        if (intersections.Length == 0 || intersections[0].Intersection <= 0)
        {
            throw new InvalidOperationException(
                "The running Legends window is not on an available display.");
        }

        long largest = intersections[0].Intersection;
        if (intersections.Length > 1
            && intersections[1].Intersection == largest)
        {
            throw new InvalidOperationException(
                "The running Legends window is split evenly across displays. Move "
                    + "it fully onto the display you want, then try again.");
        }

        long clientArea = (long)clientBounds.Width * clientBounds.Height;
        if (largest * 100 < clientArea * 60)
        {
            throw new InvalidOperationException(
                "Most of the running Legends window is not contained by one display. "
                    + "Move it onto the display you want, then try again.");
        }

        return intersections[0].Monitor;
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
}
