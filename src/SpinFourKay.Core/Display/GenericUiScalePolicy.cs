namespace SpinFourKay.Core.Display;

/// <summary>
/// Pure policy for the simplified generic/custom-UI scale controls.
/// </summary>
public static class GenericUiScalePolicy
{
    public static FineUiScale Step(FineUiScale current, int hundredthSteps)
    {
        long candidate = (long)current.Hundredths + hundredthSteps;
        int bounded = checked((int)Math.Clamp(
            candidate,
            FineUiScale.MinimumHundredths,
            FineUiScale.MaximumHundredths));
        return new FineUiScale(bounded);
    }

    public static ScalingFilter NormalizeFilter(
        FineUiScale scale,
        ScalingFilter requestedFilter)
    {
        if (!Enum.IsDefined(requestedFilter))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedFilter),
                requestedFilter,
                "The requested scaling filter is not supported.");
        }

        return scale.Hundredths == FineUiScale.MinimumHundredths
                || (requestedFilter == ScalingFilter.NearestNeighbor
                    && scale.Hundredths != 200)
            ? ScalingFilter.Fsr
            : requestedFilter;
    }
}
