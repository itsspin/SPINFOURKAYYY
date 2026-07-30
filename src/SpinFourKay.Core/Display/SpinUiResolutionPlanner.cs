using System.Collections.ObjectModel;

namespace SpinFourKay.Core.Display;

/// <summary>
/// A scaling plan whose source matches a known SpinUI character-layout size.
/// Resolution compatibility does not prove that a character has loaded or saved
/// the matching layout, so callers must obtain that confirmation from the user.
/// </summary>
public sealed record SpinUiResolutionPlan(
    ResolutionPlan ResolutionPlan,
    double RelativeAspectError)
{
    public PixelSize TargetResolution => ResolutionPlan.TargetResolution;

    public PixelSize SourceResolution => ResolutionPlan.SourceResolution;

    public PixelRect DestinationContent => ResolutionPlan.DestinationContent;

    public double ActualUiScale => ResolutionPlan.ActualUiScale;

    public ScalingFilter Filter => ResolutionPlan.Filter;

    public bool UsesIntegerScale => ResolutionPlan.UsesIntegerScale;

    public bool RequiresCharacterLayoutConfirmation { get; } = true;

    public int LeftMarginPixels => DestinationContent.X;

    public int TopMarginPixels => DestinationContent.Y;

    public int RightMarginPixels =>
        TargetResolution.Width
        - DestinationContent.X
        - DestinationContent.Width;

    public int BottomMarginPixels =>
        TargetResolution.Height
        - DestinationContent.Y
        - DestinationContent.Height;

    public bool HasPillarboxing =>
        LeftMarginPixels > 0 || RightMarginPixels > 0;

    public bool HasLetterboxing =>
        TopMarginPixels > 0 || BottomMarginPixels > 0;
}

/// <summary>
/// Produces plans only from source sizes for which SpinUI layouts are known.
/// The generic <see cref="ResolutionPlanner"/> remains available for non-SpinUI
/// clients and intentionally calculated custom sizes.
/// </summary>
public sealed class SpinUiResolutionPlanner
{
    public const double MaximumRelativeAspectError = 0.02;

    public const double PreferredUiScale = 1.5;

    private static readonly ReadOnlyCollection<PixelSize> KnownSources =
        Array.AsReadOnly(
        [
            new PixelSize(1920, 1080),
            new PixelSize(2048, 1080),
            new PixelSize(2560, 1080),
            new PixelSize(2560, 1440),
            new PixelSize(3440, 1440),
            new PixelSize(3840, 1600),
            new PixelSize(3840, 2160),
        ]);

    private readonly IReadOnlyList<PixelSize> _validatedSources = KnownSources;

    public IReadOnlyList<PixelSize> ValidatedSourceResolutions => _validatedSources;

    public bool IsValidatedSource(PixelSize sourceResolution)
    {
        return _validatedSources.Contains(sourceResolution);
    }

    public IReadOnlyList<SpinUiResolutionPlan> GetRecommendedPlans(
        PixelSize targetResolution,
        ScalingFilter filter = ScalingFilter.Fsr)
    {
        ValidateFilter(filter);

        return _validatedSources
            .Where(source => IsSmallerInBothDimensions(source, targetResolution))
            .Select(source => TryCreatePlan(targetResolution, source, filter))
            .Where(plan => plan is not null)
            .Select(plan => plan!)
            .OrderBy(
                plan => Math.Abs(plan.ActualUiScale - PreferredUiScale))
            .ThenBy(plan => plan.RelativeAspectError)
            .ThenByDescending(plan => plan.SourceResolution.Area)
            .ToArray();
    }

    public SpinUiResolutionPlan CreateExactSourcePlan(
        PixelSize targetResolution,
        PixelSize sourceResolution,
        ScalingFilter filter = ScalingFilter.Fsr)
    {
        ValidateFilter(filter);
        if (!IsValidatedSource(sourceResolution))
        {
            throw new ArgumentException(
                $"{sourceResolution} is not a validated SpinUI source resolution.",
                nameof(sourceResolution));
        }

        if (!IsSmallerInBothDimensions(sourceResolution, targetResolution))
        {
            throw new ArgumentException(
                "A SpinUI source must be smaller than the target in both dimensions; "
                + "1x and no-op plans are not offered.",
                nameof(sourceResolution));
        }

        double aspectError = CalculateRelativeAspectError(
            sourceResolution,
            targetResolution);
        if (aspectError > MaximumRelativeAspectError)
        {
            throw new ArgumentException(
                $"{sourceResolution} differs from the target aspect ratio by "
                + $"{aspectError:P2}, above the {MaximumRelativeAspectError:P0} limit.",
                nameof(sourceResolution));
        }

        SpinUiResolutionPlan plan = CreatePlan(
            targetResolution,
            sourceResolution,
            filter,
            aspectError);
        if (plan.ActualUiScale <= 1.0)
        {
            throw new ArgumentException(
                "The fitted SpinUI plan would remain at 1x and is not an upscale.",
                nameof(sourceResolution));
        }

        if (filter == ScalingFilter.NearestNeighbor && !plan.UsesIntegerScale)
        {
            throw new ArgumentException(
                "Nearest-neighbor requires an exact integer SpinUI scale with no "
                + "fractional fit.",
                nameof(filter));
        }

        return plan;
    }

    public static double CalculateRelativeAspectError(
        PixelSize sourceResolution,
        PixelSize targetResolution)
    {
        return Math.Abs(
            sourceResolution.AspectRatio - targetResolution.AspectRatio)
            / targetResolution.AspectRatio;
    }

    private static SpinUiResolutionPlan? TryCreatePlan(
        PixelSize target,
        PixelSize source,
        ScalingFilter filter)
    {
        double aspectError = CalculateRelativeAspectError(source, target);
        if (aspectError > MaximumRelativeAspectError)
        {
            return null;
        }

        SpinUiResolutionPlan plan = CreatePlan(
            target,
            source,
            filter,
            aspectError);
        if (plan.ActualUiScale <= 1.0)
        {
            return null;
        }

        if (filter == ScalingFilter.NearestNeighbor && !plan.UsesIntegerScale)
        {
            return null;
        }

        return plan;
    }

    private static SpinUiResolutionPlan CreatePlan(
        PixelSize target,
        PixelSize source,
        ScalingFilter filter,
        double aspectError)
    {
        PixelRect destination = CalculateFittedDestination(source, target);
        double actualScale = Math.Min(
            (double)destination.Width / source.Width,
            (double)destination.Height / source.Height);
        ResolutionPlan resolutionPlan = new(
            target,
            source,
            destination,
            actualScale,
            filter,
            ResolutionPresetKind.Custom);
        return new SpinUiResolutionPlan(resolutionPlan, aspectError);
    }

    private static PixelRect CalculateFittedDestination(
        PixelSize source,
        PixelSize target)
    {
        double scale = Math.Min(
            (double)target.Width / source.Width,
            (double)target.Height / source.Height);
        int width = Math.Min(target.Width, (int)Math.Floor(source.Width * scale));
        int height = Math.Min(target.Height, (int)Math.Floor(source.Height * scale));
        return new PixelRect(
            (target.Width - width) / 2,
            (target.Height - height) / 2,
            width,
            height);
    }

    private static bool IsSmallerInBothDimensions(PixelSize source, PixelSize target)
    {
        return source.Width < target.Width && source.Height < target.Height;
    }

    private static void ValidateFilter(ScalingFilter filter)
    {
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(
                nameof(filter),
                filter,
                "The scaling filter is not supported.");
        }
    }
}
