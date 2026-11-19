namespace TickBudget.Core;

/// <summary>
/// Closed-form Black-Scholes prices and Greeks for European options on a
/// non-dividend-paying underlying.
/// </summary>
/// <remarks>
/// Units:
/// <list type="bullet">
/// <item>Delta: price change per 1.00 change in spot.</item>
/// <item>Gamma: delta change per 1.00 change in spot.</item>
/// <item>Vega: price change per 1.00 change in σ (divide by 100 for "per vol point").</item>
/// <item>Theta: price change per year of calendar time, i.e. −∂V/∂T (divide by 365 for per day).</item>
/// </list>
/// All methods are static and take the input by <c>in</c> (read-only reference), so nothing
/// is allocated on the heap.
/// </remarks>
public static class BlackScholes
{
    /// <summary>Fair value of a European call or put.</summary>
    public static double Price(in OptionInput o, OptionType type)
    {
        o.Validate();
        var (d1, d2) = D1D2(o);
        double discK = o.Strike * Math.Exp(-o.Rate * o.Time); // K·e^(−rT)

        return type == OptionType.Call
            ? o.Spot * NormalDistribution.Cdf(d1) - discK * NormalDistribution.Cdf(d2)
            : discK * NormalDistribution.Cdf(-d2) - o.Spot * NormalDistribution.Cdf(-d1);
    }

    /// <summary>∂V/∂S. Call: N(d1). Put: N(d1) − 1.</summary>
    public static double Delta(in OptionInput o, OptionType type)
    {
        o.Validate();
        var (d1, _) = D1D2(o);
        double nd1 = NormalDistribution.Cdf(d1);
        return type == OptionType.Call ? nd1 : nd1 - 1.0;
    }

    /// <summary>∂²V/∂S² = φ(d1) / (S·σ·√T). Same for calls and puts.</summary>
    public static double Gamma(in OptionInput o)
    {
        o.Validate();
        var (d1, _) = D1D2(o);
        return NormalDistribution.Pdf(d1) / (o.Spot * o.Volatility * Math.Sqrt(o.Time));
    }

    /// <summary>∂V/∂σ = S·φ(d1)·√T. Same for calls and puts.</summary>
    public static double Vega(in OptionInput o)
    {
        o.Validate();
        var (d1, _) = D1D2(o);
        return o.Spot * NormalDistribution.Pdf(d1) * Math.Sqrt(o.Time);
    }

    /// <summary>
    /// −∂V/∂T (per year).
    /// Call: −S·φ(d1)·σ/(2√T) − r·K·e^(−rT)·N(d2).
    /// Put:  −S·φ(d1)·σ/(2√T) + r·K·e^(−rT)·N(−d2).
    /// </summary>
    public static double Theta(in OptionInput o, OptionType type)
    {
        o.Validate();
        var (d1, d2) = D1D2(o);
        double decay = -o.Spot * NormalDistribution.Pdf(d1) * o.Volatility / (2.0 * Math.Sqrt(o.Time));
        double rDiscK = o.Rate * o.Strike * Math.Exp(-o.Rate * o.Time);

        return type == OptionType.Call
            ? decay - rDiscK * NormalDistribution.Cdf(d2)
            : decay + rDiscK * NormalDistribution.Cdf(-d2);
    }

    /// <summary>
    /// d1 = (ln(S/K) + (r + σ²/2)·T) / (σ·√T),  d2 = d1 − σ·√T.
    /// Assumes the input is already validated.
    /// </summary>
    private static (double d1, double d2) D1D2(in OptionInput o)
    {
        double volSqrtT = o.Volatility * Math.Sqrt(o.Time);
        double d1 = (Math.Log(o.Spot / o.Strike) + (o.Rate + 0.5 * o.Volatility * o.Volatility) * o.Time) / volSqrtT;
        return (d1, d1 - volSqrtT);
    }
}
