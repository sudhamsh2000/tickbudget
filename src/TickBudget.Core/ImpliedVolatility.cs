namespace TickBudget.Core;

/// <summary>Outcome of an implied-volatility solve.</summary>
public enum ImpliedVolStatus
{
    /// <summary>A volatility was found that reprices the option.</summary>
    Converged,
    /// <summary>Price is NaN, infinite or negative.</summary>
    InvalidPrice,
    /// <summary>Price is below the no-arbitrage lower bound (intrinsic value of the forward).</summary>
    BelowIntrinsic,
    /// <summary>Price is at or above the no-arbitrage upper bound (S for a call, K·e^(−rT) for a put).</summary>
    AboveUpperBound,
    /// <summary>Price is arbitrage-free but needs σ below <see cref="ImpliedVolSettings.MinVolatility"/>.</summary>
    VolatilityBelowRange,
    /// <summary>Price is arbitrage-free but needs σ above <see cref="ImpliedVolSettings.MaxVolatility"/>.</summary>
    VolatilityAboveRange,
    /// <summary>The iteration limit was reached, or the final answer failed its repricing check.</summary>
    NotConverged,
}

/// <summary>
/// Result of <see cref="ImpliedVolatility.Solve"/>. <see cref="Volatility"/> is NaN unless
/// <see cref="Status"/> is <see cref="ImpliedVolStatus.Converged"/>.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Volatility">Implied σ, or NaN on failure.</param>
/// <param name="NewtonSteps">Number of Newton-Raphson steps taken.</param>
/// <param name="BisectionSteps">Number of bisection fallback steps taken.</param>
public readonly record struct ImpliedVolResult(
    ImpliedVolStatus Status,
    double Volatility,
    int NewtonSteps,
    int BisectionSteps)
{
    public bool Success => Status == ImpliedVolStatus.Converged;
    public int Iterations => NewtonSteps + BisectionSteps;

    internal static ImpliedVolResult Fail(ImpliedVolStatus status, int newton = 0, int bisection = 0) =>
        new(status, double.NaN, newton, bisection);
}

/// <summary>Solver limits.</summary>
/// <param name="MinVolatility">Lower edge of the search range for σ.</param>
/// <param name="MaxVolatility">Upper edge of the search range for σ.</param>
/// <param name="VolatilityTolerance">Stop when the step or the bracket in σ is narrower than this.</param>
/// <param name="MaxIterations">Total Newton + bisection steps allowed.</param>
/// <param name="InitialGuess">
/// Starting σ. NaN (the default) means use the Manaster-Koehler guess. A caller can pass a warm
/// start, such as the previous tick's implied vol.
/// </param>
public sealed record ImpliedVolSettings(
    double MinVolatility = 1e-4,
    double MaxVolatility = 5.0,
    double VolatilityTolerance = 1e-12,
    int MaxIterations = 100,
    double InitialGuess = double.NaN)
{
    /// <summary>Shared default instance, so the common call path allocates nothing.</summary>
    public static readonly ImpliedVolSettings Default = new();
}

/// <summary>
/// Implied volatility for European options: the σ at which Black-Scholes matches a given price.
/// </summary>
/// <remarks>
/// <para>
/// Algorithm: safeguarded Newton-Raphson. A bracket [lo, hi] known to contain the root is kept
/// and narrowed on every step using the sign of BS(σ) − price (price rises with σ, so the sign
/// says which side the root is on). A Newton step that would leave the bracket, or that has
/// near-zero vega, is replaced by a bisection step. This keeps Newton's speed near the root and
/// bisection's guarantee everywhere else.
/// </para>
/// <para>
/// Newton runs on ln(price) rather than price: σ ← σ − (ln BS(σ) − ln p)·BS(σ)/vega.
/// Out-of-the-money prices fall off exponentially as σ drops, so a straight-line step in price
/// space crawls (a first version needed 23-36 steps for prices of 1e-8 to 1e-14 and failed to
/// converge in 100 steps at 1e-300). On a log scale the curve is close to straight.
/// </para>
/// <para>
/// Conditioning: an in-the-money option is mostly intrinsic value, and its small time value gets
/// lost in rounding. So in-the-money calls are converted to the matching out-of-the-money put via
/// put-call parity (P = C − S + K·e^(−rT)), and vice versa, before solving. Both have the same σ.
/// </para>
/// <para>
/// Errors: invalid market inputs (S, K, T, r) throw <see cref="ArgumentOutOfRangeException"/>,
/// the same as the pricer, because they are programming errors. A bad or impossible
/// <em>price</em> is market data, so it returns a failed <see cref="ImpliedVolResult"/> instead.
/// </para>
/// </remarks>
public static class ImpliedVolatility
{
    public static ImpliedVolResult Solve(
        OptionType type,
        double price,
        double spot,
        double strike,
        double time,
        double rate,
        ImpliedVolSettings? settings = null)
    {
        var cfg = settings ?? ImpliedVolSettings.Default;

        // Validate S, K, T, r with a placeholder σ (the input's σ is not used below).
        var input = new OptionInput(spot, strike, time, rate, Volatility: 1.0);
        input.Validate();

        if (!double.IsFinite(price) || price < 0)
            return ImpliedVolResult.Fail(ImpliedVolStatus.InvalidPrice);

        // ---- No-arbitrage bounds, checked on the option as given ----
        double discK = strike * Math.Exp(-rate * time);
        double forwardIntrinsic = type == OptionType.Call ? spot - discK : discK - spot;
        double lowerBound = Math.Max(forwardIntrinsic, 0.0);
        double upperBound = type == OptionType.Call ? spot : discK;
        // Allow a rounding-sized slack so a price exactly at intrinsic reads as "σ ≈ 0", not arbitrage.
        double eps = 1e-12 * Math.Max(spot, strike);

        if (price < lowerBound - eps) return ImpliedVolResult.Fail(ImpliedVolStatus.BelowIntrinsic);
        if (price >= upperBound) return ImpliedVolResult.Fail(ImpliedVolStatus.AboveUpperBound);

        // ---- Switch to the out-of-the-money side for better conditioning ----
        if (forwardIntrinsic > 0)
        {
            // Call → put:  P = C − (S − K·e^(−rT)).   Put → call:  C = P − (K·e^(−rT) − S).
            price -= forwardIntrinsic;
            type = type == OptionType.Call ? OptionType.Put : OptionType.Call;
        }

        // ---- Range check: is the root inside [MinVolatility, MaxVolatility]? ----
        double lo = cfg.MinVolatility, hi = cfg.MaxVolatility;
        if (price <= BlackScholes.Price(input with { Volatility = lo }, type))
            return ImpliedVolResult.Fail(ImpliedVolStatus.VolatilityBelowRange);
        if (price >= BlackScholes.Price(input with { Volatility = hi }, type))
            return ImpliedVolResult.Fail(ImpliedVolStatus.VolatilityAboveRange);

        // ---- Safeguarded Newton-Raphson on ln(price) ----
        // Default starting guess (Manaster & Koehler, 1982): σ₀ = √(2·|ln(S/K) + rT| / T).
        // It is the inflection point of price as a function of σ. Clamp it inside the bracket.
        double sigma = double.IsNaN(cfg.InitialGuess)
            ? Math.Sqrt(2.0 * Math.Abs(Math.Log(spot / strike) + rate * time) / time)
            : cfg.InitialGuess;
        if (!(sigma > lo && sigma < hi)) sigma = 0.5 * (lo + hi);

        double logPrice = Math.Log(price); // price > BS(MinVolatility) ≥ 0 here, so this is finite
        int newton = 0, bisection = 0;
        while (newton + bisection < cfg.MaxIterations)
        {
            var at = input with { Volatility = sigma };
            double model = BlackScholes.Price(at, type);
            double diff = model - price;
            if (diff == 0) return Verified(at, type, price, newton, bisection);

            // Price rises with σ: too high → root is below σ, too low → root is above σ.
            if (diff > 0) hi = sigma; else lo = sigma;

            // Newton step on g(σ) = ln BS(σ) − ln p, whose derivative is vega / BS(σ).
            // If BS(σ) underflowed to 0 the step is NaN, which fails the bracket test below.
            double vega = BlackScholes.Vega(at);
            double next = sigma - (Math.Log(model) - logPrice) * model / vega;

            // Converged: test the step size BEFORE the bracket test. A converged σ can sit exactly
            // on a bracket edge (the step rounds to 0, so next == lo). A strict bracket test would
            // then reject it and bisect away from the answer. (A NaN step fails this test.)
            if (Math.Abs(next - sigma) < cfg.VolatilityTolerance)
                return Verified(input with { Volatility = next >= lo && next <= hi ? next : sigma },
                                type, price, newton + 1, bisection);

            if (next > lo && next < hi)
            {
                newton++;
            }
            else
            {
                next = 0.5 * (lo + hi); // Newton left the bracket (or vega ≈ 0): bisect
                bisection++;
            }

            if (Math.Abs(next - sigma) < cfg.VolatilityTolerance || hi - lo < cfg.VolatilityTolerance)
                return Verified(input with { Volatility = next }, type, price, newton, bisection);

            sigma = next;
        }

        return ImpliedVolResult.Fail(ImpliedVolStatus.NotConverged, newton, bisection);
    }

    /// <summary>
    /// Final check: reprice at the answer and require the error to be no more than the price
    /// change a σ error of 1e-9 could cause, plus rounding (1e-12·max(S, K)).
    /// Limitation: the rounding term is absolute, so for very cheap options (price below ~1e-10
    /// when S ≈ 100) this check is loose. There, accuracy comes from the σ-based stopping rule.
    /// </summary>
    private static ImpliedVolResult Verified(in OptionInput at, OptionType type, double price, int newton, int bisection)
    {
        double repriced = BlackScholes.Price(at, type);
        double allowed = 1e-9 * BlackScholes.Vega(at) + 1e-12 * Math.Max(at.Spot, at.Strike);
        return Math.Abs(repriced - price) <= allowed
            ? new ImpliedVolResult(ImpliedVolStatus.Converged, at.Volatility, newton, bisection)
            : ImpliedVolResult.Fail(ImpliedVolStatus.NotConverged, newton, bisection);
    }
}
