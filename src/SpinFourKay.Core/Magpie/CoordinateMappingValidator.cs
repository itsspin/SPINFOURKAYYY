using SpinFourKay.Core.Display;

namespace SpinFourKay.Core.Magpie;

public readonly record struct PixelPoint(double X, double Y);

public sealed record CoordinateMappingSample(
    string Name,
    PixelPoint Source,
    PixelPoint Destination,
    PixelPoint RoundTripSource,
    double ErrorInSourcePixels);

public sealed record CoordinateMappingValidationResult(
    bool IsSafe,
    double MaximumRoundTripError,
    double RelativeAspectRatioError,
    IReadOnlyList<CoordinateMappingSample> Samples,
    IReadOnlyList<string> Errors);

/// <summary>
/// Validates the affine mapping Magpie exposes for mouse coordinates.
/// Calculations use physical pixels and intentionally include integer cursor rounding.
/// </summary>
public sealed class CoordinateMappingValidator
{
    private readonly double _defaultMaximumSourcePixelError;
    private readonly double _defaultMaximumRelativeAspectError;

    public CoordinateMappingValidator(
        double defaultMaximumSourcePixelError = 1.0,
        double defaultMaximumRelativeAspectError = 0.005)
    {
        if (!double.IsFinite(defaultMaximumSourcePixelError)
            || defaultMaximumSourcePixelError < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultMaximumSourcePixelError));
        }

        if (!double.IsFinite(defaultMaximumRelativeAspectError)
            || defaultMaximumRelativeAspectError < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultMaximumRelativeAspectError));
        }

        _defaultMaximumSourcePixelError = defaultMaximumSourcePixelError;
        _defaultMaximumRelativeAspectError = defaultMaximumRelativeAspectError;
    }

    public CoordinateMappingValidationResult Validate(
        PixelRect source,
        PixelRect destination,
        double? maximumSourcePixelError = null,
        double? maximumRelativeAspectError = null)
    {
        double allowedSourcePixelError =
            maximumSourcePixelError ?? _defaultMaximumSourcePixelError;
        double allowedRelativeAspectError =
            maximumRelativeAspectError ?? _defaultMaximumRelativeAspectError;

        if (!double.IsFinite(allowedSourcePixelError) || allowedSourcePixelError < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSourcePixelError));
        }

        if (!double.IsFinite(allowedRelativeAspectError) || allowedRelativeAspectError < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRelativeAspectError));
        }

        List<string> errors = [];
        if (source.Width <= 0 || source.Height <= 0)
        {
            errors.Add("The source mapping region is empty.");
        }
        else if (source.Width == 1 || source.Height == 1)
        {
            errors.Add(
                "Magpie's inclusive cursor transform is undefined for a one-pixel source axis.");
        }

        if (destination.Width <= 0 || destination.Height <= 0)
        {
            errors.Add("The destination mapping region is empty.");
        }
        else if (destination.Width == 1 || destination.Height == 1)
        {
            errors.Add(
                "Magpie's inclusive cursor transform is undefined for a one-pixel destination axis.");
        }

        if (errors.Count > 0)
        {
            return new CoordinateMappingValidationResult(
                IsSafe: false,
                MaximumRoundTripError: double.PositiveInfinity,
                RelativeAspectRatioError: double.PositiveInfinity,
                Samples: [],
                Errors: errors);
        }

        double sourceAspect = (double)source.Width / source.Height;
        double destinationAspect = (double)destination.Width / destination.Height;
        double relativeAspectError = Math.Abs(destinationAspect - sourceAspect) / sourceAspect;
        if (relativeAspectError > allowedRelativeAspectError)
        {
            errors.Add(
                $"The destination changes aspect ratio by {relativeAspectError:P2}; "
                + "mouse coordinates may not match the rendered image.");
        }

        double sourceRight = source.X + source.Width - 1;
        double sourceBottom = source.Y + source.Height - 1;
        double sourceCenterX = source.X + (source.Width / 2);
        double sourceCenterY = source.Y + (source.Height / 2);

        (string Name, PixelPoint Point)[] points =
        [
            ("Top left", new PixelPoint(source.X, source.Y)),
            ("Top right", new PixelPoint(sourceRight, source.Y)),
            ("Center", new PixelPoint(sourceCenterX, sourceCenterY)),
            ("Bottom left", new PixelPoint(source.X, sourceBottom)),
            ("Bottom right", new PixelPoint(sourceRight, sourceBottom)),
        ];

        List<CoordinateMappingSample> samples = new(points.Length);
        foreach ((string name, PixelPoint point) in points)
        {
            PixelPoint destinationPoint = SourceToDestination(point, source, destination);
            PixelPoint sourceRoundTrip = DestinationToSource(
                destinationPoint,
                source,
                destination);
            double error = Math.Max(
                Math.Abs(sourceRoundTrip.X - point.X),
                Math.Abs(sourceRoundTrip.Y - point.Y));

            samples.Add(new CoordinateMappingSample(
                name,
                point,
                destinationPoint,
                sourceRoundTrip,
                error));
        }

        double maximumError = samples.Max(sample => sample.ErrorInSourcePixels);
        if (maximumError > allowedSourcePixelError)
        {
            errors.Add(
                $"Mouse coordinate round-trip error is {maximumError:0.###} source pixels; "
                + $"the allowed maximum is {allowedSourcePixelError:0.###}.");
        }

        return new CoordinateMappingValidationResult(
            errors.Count == 0,
            maximumError,
            relativeAspectError,
            samples,
            errors);
    }

    public static PixelPoint SourceToDestination(
        PixelPoint point,
        PixelRect source,
        PixelRect destination)
    {
        ValidateRects(source, destination);
        return new PixelPoint(
            MapAxisSourceToDestination(
                point.X,
                source.X,
                source.Width,
                destination.X,
                destination.Width),
            MapAxisSourceToDestination(
                point.Y,
                source.Y,
                source.Height,
                destination.Y,
                destination.Height));
    }

    public static PixelPoint DestinationToSource(
        PixelPoint point,
        PixelRect source,
        PixelRect destination)
    {
        ValidateRects(source, destination);
        return new PixelPoint(
            MapAxisDestinationToSource(
                point.X,
                source.X,
                source.Width,
                destination.X,
                destination.Width),
            MapAxisDestinationToSource(
                point.Y,
                source.Y,
                source.Height,
                destination.Y,
                destination.Height));
    }

    private static void ValidateRects(PixelRect source, PixelRect destination)
    {
        if (source.Width <= 0
            || source.Height <= 0
            || destination.Width <= 0
            || destination.Height <= 0)
        {
            throw new ArgumentException("Coordinate mapping rectangles must have positive dimensions.");
        }
    }

    // Mirrors Magpie v0.12.1 CursorManager.cpp SrcToScaling. Pixel endpoints
    // are inclusive, so the transform uses (size - 1), not texture dimensions.
    private static double MapAxisSourceToDestination(
        double point,
        int sourceStart,
        int sourceSize,
        int destinationStart,
        int destinationSize)
    {
        int sourceEnd = sourceStart + sourceSize;
        int destinationEnd = destinationStart + destinationSize;
        if (point >= sourceEnd)
        {
            return destinationEnd + point - sourceEnd;
        }

        if (point < sourceStart)
        {
            return destinationStart + point - sourceStart;
        }

        if (sourceSize == 1 || destinationSize == 1)
        {
            return destinationStart;
        }

        double position = (point - sourceStart) / (sourceSize - 1);
        return RoundLikeCpp(position * (destinationSize - 1)) + destinationStart;
    }

    // Mirrors Magpie v0.12.1 CursorManager.cpp ScalingToSrc (Round mode).
    private static double MapAxisDestinationToSource(
        double point,
        int sourceStart,
        int sourceSize,
        int destinationStart,
        int destinationSize)
    {
        int destinationEnd = destinationStart + destinationSize;
        if (point >= destinationEnd)
        {
            return sourceStart + sourceSize + point - destinationEnd;
        }

        if (point < destinationStart)
        {
            return sourceStart + point - destinationStart;
        }

        if (sourceSize == 1 || destinationSize == 1)
        {
            return sourceStart;
        }

        double position = (point - destinationStart) / (destinationSize - 1);
        return RoundLikeCpp(position * (sourceSize - 1)) + sourceStart;
    }

    private static double RoundLikeCpp(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero);
}
