using System.Text.Json.Serialization;

namespace SpinFourKay.Core.Display;

/// <summary>
/// A width and height measured in physical pixels.
/// </summary>
public readonly record struct PixelSize
{
    [JsonConstructor]
    public PixelSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public double AspectRatio => (double)Width / Height;

    public long Area => (long)Width * Height;

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// A rectangle measured in physical pixels.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public PixelSize Size => new(Width, Height);
}

public enum ResolutionPresetKind
{
    Balanced,
    Comfort,
    PixelCrisp,
    Custom,
}

public enum ScalingFilter
{
    Fsr,
    Lanczos,
    NearestNeighbor,
}

public sealed record ResolutionPreset(
    ResolutionPresetKind Kind,
    string Name,
    string Description,
    double DesiredUiScale,
    ScalingFilter Filter);

public sealed record ResolutionPlan(
    PixelSize TargetResolution,
    PixelSize SourceResolution,
    PixelRect DestinationContent,
    double ActualUiScale,
    ScalingFilter Filter,
    ResolutionPresetKind PresetKind)
{
    public bool UsesIntegerScale
    {
        get
        {
            double xScale = (double)DestinationContent.Width / SourceResolution.Width;
            double yScale = (double)DestinationContent.Height / SourceResolution.Height;

            return Math.Abs(xScale - Math.Round(xScale)) < 0.0001
                && Math.Abs(yScale - Math.Round(yScale)) < 0.0001
                && Math.Abs(xScale - yScale) < 0.0001;
        }
    }
}
