using TickBudget.Core;
using Xunit.Abstractions;

namespace TickBudget.Tests;

public class ImpliedVolatilityTests(ITestOutputHelper output)
{
    private const double S = 100.0;

    // ---------- Round trip ----------

    [Fact]
    public void RoundTrip_RecoversVolatility_AcrossGrid()
    {
        // Price with a known σ, solve back, compare. A case is "well-conditioned" when a
        // rounding-sized error in the given price (~1e-15 relative) moves σ by < 1e-9,
        // i.e. 1e-15·price / vega < 1e-9. Only those cases can be asked to recover σ exactly;
        // for the rest the information is genuinely lost in rounding (see the next test).
        double[] strikes = [50, 70, 90, 100, 110, 130, 200];
        double[] times = [0.02, 0.25, 1, 3];
        double[] vols = [0.05, 0.1, 0.2, 0.5, 1.0, 2.0];
        double[] rates = [0.05, -0.01];

        int wellConditioned = 0, skipped = 0, maxIter = 0, totalNewton = 0, totalBisection = 0;
        double worstErr = 0;
        string worstCase = "";

        foreach (var type in new[] { OptionType.Call, OptionType.Put })
        foreach (var k in strikes)
        foreach (var t in times)
        foreach (var v in vols)
        foreach (var r in rates)
        {
            var o = new OptionInput(S, k, t, r, v);
            double price = BlackScholes.Price(o, type);
            double vega = BlackScholes.Vega(o);
            // Also skip prices that underflow (≤ 1e-250, near the bottom of the double range):
            // a price of 0.0 genuinely means σ ≈ 0, and the solver reports that as below range.
            if (price < 1e-250 || !(1e-15 * price / vega < 1e-9)) { skipped++; continue; }
            wellConditioned++;

            var result = ImpliedVolatility.Solve(type, price, S, k, t, r);
            Assert.True(result.Success, $"{type} {o}: {result}");

            double err = Math.Abs(result.Volatility - v);
            if (err > worstErr) { worstErr = err; worstCase = $"{type} {o}"; }
            maxIter = Math.Max(maxIter, result.Iterations);
            totalNewton += result.NewtonSteps;
            totalBisection += result.BisectionSteps;
            Assert.True(err < 1e-9, $"{type} {o}: σ error {err:E3}");
        }

        output.WriteLine($"Well-conditioned cases: {wellConditioned}, skipped as ill-conditioned: {skipped}");
        output.WriteLine($"Worst |σ̂ − σ|: {worstErr:E3} ({worstCase})");
        output.WriteLine($"Max iterations: {maxIter}; total Newton steps: {totalNewton}; total bisection steps: {totalBisection}");

        // Regression guard. Measured max on this grid is 14. An earlier version took 42 because a
        // converged σ landing exactly on a bracket edge was rejected and bisected away from.
        Assert.True(maxIter <= 20, $"max iterations {maxIter}");
    }

    [Fact]
    public void RoundTrip_TextbookCase()
    {
        var r = ImpliedVolatility.Solve(OptionType.Call, 10.450583572185565, S, 100, 1, 0.05);
        Assert.Equal(ImpliedVolStatus.Converged, r.Status);
        Assert.Equal(0.2, r.Volatility, 1e-12);
        output.WriteLine($"σ̂ = {r.Volatility:R} in {r.NewtonSteps} Newton + {r.BisectionSteps} bisection steps");
    }

    [Fact]
    public void DeepInTheMoney_IllConditioned_RepricesOrFailsCleanly()
    {
        // S=100, K=50, T=0.25, σ=0.2 call: price ≈ 50.62. Measured time value 8.2e-13, only
        // ~115 units of rounding (ulp(50.62) ≈ 7.1e-15). So only 2-3 digits of σ survive in the
        // price (measured: σ̂ = 0.20003). What we can demand: either fail with NaN, or return
        // a σ whose price matches the input.
        var o = new OptionInput(S, 50, 0.25, 0.05, 0.2);
        double price = BlackScholes.Price(o, OptionType.Call);
        var r = ImpliedVolatility.Solve(OptionType.Call, price, S, 50, 0.25, 0.05);
        double timeValue = BlackScholes.Price(o, OptionType.Put); // OTM put = call's time value, by parity
        output.WriteLine($"price = {price:R}, time value = {timeValue:E3}, ulp(price) = {Math.BitIncrement(price) - price:E3}, result = {r}");

        if (r.Success)
            Assert.Equal(price, BlackScholes.Price(o with { Volatility = r.Volatility }, OptionType.Call), 1e-12);
        else
            Assert.True(double.IsNaN(r.Volatility));
    }

    [Theory]
    // Measured prices (r = 3%): 9.5e-5, 7.0e-12, 2.8e-14, 1.5e-8, 8.6e-9.
    [InlineData(OptionType.Call, 150.0, 0.25, 0.20)]
    [InlineData(OptionType.Call, 200.0, 0.25, 0.20)]
    [InlineData(OptionType.Call, 300.0, 0.50, 0.20)]
    [InlineData(OptionType.Put, 50.0, 0.25, 0.25)]
    [InlineData(OptionType.Put, 30.0, 0.50, 0.30)]
    public void DeepOutOfTheMoney_RoundTrip(OptionType type, double k, double t, double v)
    {
        var o = new OptionInput(S, k, t, 0.03, v);
        double price = BlackScholes.Price(o, type);
        var r = ImpliedVolatility.Solve(type, price, S, k, t, 0.03);
        double err = Math.Abs(r.Volatility - v);
        output.WriteLine($"{type} K={k} T={t}: price = {price:E3}, σ̂ = {r.Volatility:R}, |error| = {err:E3}, " +
                         $"{r.NewtonSteps} Newton + {r.BisectionSteps} bisection");

        Assert.Equal(ImpliedVolStatus.Converged, r.Status);
        Assert.True(err < 1e-9, $"σ error {err:E3}");
    }

    // ---------- Bisection fallback and iteration limit ----------

    // The default Manaster-Koehler start sits at the inflection point, where Newton does not
    // overshoot, so the fallback rarely fires on its own. A bad warm start (e.g. the previous
    // tick's σ after a market jump) is what triggers it, as starting from σ₀ = 4.5 here does
    // (measured: 5 Newton + 4 bisection steps).
    private static readonly OptionInput FarGuessCase = new(S, 150, 0.05, 0.02, 0.3);
    private static readonly ImpliedVolSettings BadGuess = new(InitialGuess: 4.5);

    [Fact]
    public void BisectionFallback_IsUsed_AndStillConverges()
    {
        double price = BlackScholes.Price(FarGuessCase, OptionType.Call);
        var r = ImpliedVolatility.Solve(OptionType.Call, price, S, 150, 0.05, 0.02, BadGuess);
        output.WriteLine($"price = {price:E3}, result = {r}");

        Assert.Equal(ImpliedVolStatus.Converged, r.Status);
        Assert.True(r.BisectionSteps > 0, "expected the bisection fallback to be exercised");
        Assert.Equal(0.3, r.Volatility, 1e-9);
    }

    [Fact]
    public void IterationLimit_ReturnsNotConverged_WithNaN()
    {
        double price = BlackScholes.Price(FarGuessCase, OptionType.Call);
        var r = ImpliedVolatility.Solve(OptionType.Call, price, S, 150, 0.05, 0.02,
            BadGuess with { MaxIterations = 2 });

        Assert.Equal(ImpliedVolStatus.NotConverged, r.Status);
        Assert.False(r.Success);
        Assert.True(double.IsNaN(r.Volatility));
    }

    // ---------- Prices that must be rejected ----------

    [Fact]
    public void CallBelowIntrinsic_IsRejected()
    {
        // S=110, K=100, r=5%, T=1: K·e^(−rT) ≈ 95.12, so any call below ≈ 14.88 is free money.
        var r = ImpliedVolatility.Solve(OptionType.Call, 10.0, 110, 100, 1, 0.05);
        Assert.Equal(ImpliedVolStatus.BelowIntrinsic, r.Status);
        Assert.True(double.IsNaN(r.Volatility));
    }

    [Fact]
    public void PutBelowIntrinsic_IsRejected()
    {
        // S=80, K=100, r=5%, T=1: put lower bound ≈ 95.12 − 80 = 15.12.
        var r = ImpliedVolatility.Solve(OptionType.Put, 12.0, 80, 100, 1, 0.05);
        Assert.Equal(ImpliedVolStatus.BelowIntrinsic, r.Status);
    }

    [Theory]
    [InlineData(OptionType.Call, 100.0)]  // call worth at least the whole stock
    [InlineData(OptionType.Call, 150.0)]
    [InlineData(OptionType.Put, 96.0)]    // put above K·e^(−rT) ≈ 95.12
    public void AboveUpperBound_IsRejected(OptionType type, double price)
    {
        var r = ImpliedVolatility.Solve(type, price, S, 100, 1, 0.05);
        Assert.Equal(ImpliedVolStatus.AboveUpperBound, r.Status);
        Assert.True(double.IsNaN(r.Volatility));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void InvalidPrice_IsRejected(double price)
    {
        var r = ImpliedVolatility.Solve(OptionType.Call, price, S, 100, 1, 0.05);
        Assert.Equal(ImpliedVolStatus.InvalidPrice, r.Status);
        Assert.True(double.IsNaN(r.Volatility));
    }

    [Fact]
    public void PriceAtIntrinsic_IsBelowRange_NotArbitrage()
    {
        // Exactly at the forward intrinsic value means σ = 0: allowed, but outside [1e-4, 5].
        double intrinsic = S - 90 * Math.Exp(-0.05);
        Assert.Equal(ImpliedVolStatus.VolatilityBelowRange,
            ImpliedVolatility.Solve(OptionType.Call, intrinsic, S, 90, 1, 0.05).Status);
        Assert.Equal(ImpliedVolStatus.VolatilityBelowRange,
            ImpliedVolatility.Solve(OptionType.Call, 0.0, S, 120, 1, 0.05).Status);
    }

    [Fact]
    public void VeryHighVolatility_IsAboveRange()
    {
        var o = new OptionInput(S, 100, 1, 0.05, 6.0);
        double price = BlackScholes.Price(o, OptionType.Call);
        Assert.Equal(ImpliedVolStatus.VolatilityAboveRange,
            ImpliedVolatility.Solve(OptionType.Call, price, S, 100, 1, 0.05).Status);
    }

    [Fact]
    public void InvalidMarketInputs_Throw()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => ImpliedVolatility.Solve(OptionType.Call, 5.0, -100, 100, 1, 0.05));
        Assert.Equal("Spot", ex.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ImpliedVolatility.Solve(OptionType.Call, 5.0, 100, 100, 0, 0.05));
    }
}
