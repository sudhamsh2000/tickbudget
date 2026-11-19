namespace TickBudget.Core;

/// <summary>Whether the option is the right to buy (call) or to sell (put).</summary>
public enum OptionType
{
    Call,
    Put,
}

/// <summary>
/// Inputs for pricing one European option.
/// </summary>
/// <param name="Spot">S: current price of the underlying. Must be &gt; 0.</param>
/// <param name="Strike">K: price at which the option can be exercised. Must be &gt; 0.</param>
/// <param name="Time">T: time to expiry in years. Must be &gt; 0.</param>
/// <param name="Rate">r: continuously compounded risk-free rate. Any finite value (negative rates exist).</param>
/// <param name="Volatility">σ: annualised volatility of log returns. Must be &gt; 0.</param>
/// <remarks>
/// A <c>readonly record struct</c> lives on the stack (no heap allocation, no GC pressure),
/// cannot be mutated after creation, and gets value equality for free.
/// Because <c>default(OptionInput)</c> bypasses any constructor, validation lives in
/// <see cref="Validate"/>, which every pricer calls.
/// </remarks>
public readonly record struct OptionInput(
    double Spot,
    double Strike,
    double Time,
    double Rate,
    double Volatility)
{
    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> if any input is invalid.
    /// </summary>
    public void Validate()
    {
        // Written as !(x > 0) rather than x <= 0 so NaN is rejected too
        // (every comparison with NaN is false).
        if (!(Spot > 0) || double.IsInfinity(Spot))
            throw new ArgumentOutOfRangeException(nameof(Spot), Spot, "Spot must be finite and > 0.");
        if (!(Strike > 0) || double.IsInfinity(Strike))
            throw new ArgumentOutOfRangeException(nameof(Strike), Strike, "Strike must be finite and > 0.");
        if (!(Time > 0) || double.IsInfinity(Time))
            throw new ArgumentOutOfRangeException(nameof(Time), Time, "Time must be finite and > 0.");
        if (!double.IsFinite(Rate))
            throw new ArgumentOutOfRangeException(nameof(Rate), Rate, "Rate must be finite.");
        if (!(Volatility > 0) || double.IsInfinity(Volatility))
            throw new ArgumentOutOfRangeException(nameof(Volatility), Volatility, "Volatility must be finite and > 0.");
    }
}
