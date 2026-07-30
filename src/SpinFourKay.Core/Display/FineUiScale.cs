namespace SpinFourKay.Core.Display;

/// <summary>
/// A generic/custom-UI readability scale expressed in exact one-percent steps.
/// Integer hundredths are the canonical representation so slider noise cannot
/// accumulate across planning or live-adjustment requests.
/// </summary>
public readonly record struct FineUiScale
{
    public const int MinimumHundredths = 100;

    public const int MaximumHundredths = 200;

    public FineUiScale(int hundredths)
    {
        if (hundredths is < MinimumHundredths or > MaximumHundredths)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hundredths),
                hundredths,
                $"Fine UI scale must be between "
                    + $"{MinimumHundredths / 100.0:0.00}x and "
                    + $"{MaximumHundredths / 100.0:0.00}x.");
        }

        Hundredths = hundredths;
    }

    public int Hundredths { get; }

    public double Factor => Hundredths / 100.0;

    public static FineUiScale FromSlider(double factor)
    {
        if (!double.IsFinite(factor)
            || factor < MinimumHundredths / 100.0
            || factor > MaximumHundredths / 100.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(factor),
                factor,
                $"Fine UI scale must be between "
                    + $"{MinimumHundredths / 100.0:0.00}x and "
                    + $"{MaximumHundredths / 100.0:0.00}x.");
        }

        int hundredths = decimal.ToInt32(
            decimal.Round(
                (decimal)factor * 100m,
                decimals: 0,
                MidpointRounding.AwayFromZero));
        return new FineUiScale(hundredths);
    }

    public override string ToString() => $"{Factor:0.00}x";
}
