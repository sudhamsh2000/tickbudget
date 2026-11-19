using TickBudget.Core;
using Xunit.Abstractions;

namespace TickBudget.Tests;

public class BlackScholesTests(ITestOutputHelper output)
{
    // ---------- Known value ----------

    [Fact]
    public void Price_MatchesTextbookValue()
    {
        // Textbook case. The reference values are quoted to 4 decimals (10.4506, 5.5735),
        // so the tightest honest tolerance is half the last digit: 5e-5.
        var o = new OptionInput(Spot: 100, Strike: 100, Time: 1, Rate: 0.05, Volatility: 0.2);
        double call = BlackScholes.Price(o, OptionType.Call);
        double put = BlackScholes.Price(o, OptionType.Put);

        output.WriteLine($"call = {call:F10}, put = {put:F10}");
        Assert.InRange(call, 10.4506 - 5e-5, 10.4506 + 5e-5);
        Assert.InRange(put, 5.5735 - 5e-5, 5.5735 + 5e-5);
    }

    // ---------- Put-call parity ----------

    [Fact]
    public void PutCallParity_HoldsForRandomInputs()
    {
        // C − P = S − K·e^(−rT) is a model-free identity, so it must hold to rounding error.
        // Tolerance scales with the size of the numbers: 1e-10 × max(S, K).
        var rng = new Random(42); // fixed seed: the same inputs every run
        double worst = 0;
        for (int i = 0; i < 10_000; i++)
        {
            double s = 1 + rng.NextDouble() * 999;
            var o = new OptionInput(
                Spot: s,
                Strike: s * (0.5 + rng.NextDouble()),
                Time: 0.01 + rng.NextDouble() * 5,
                Rate: -0.02 + rng.NextDouble() * 0.12,
                Volatility: 0.05 + rng.NextDouble() * 0.95);

            double lhs = BlackScholes.Price(o, OptionType.Call) - BlackScholes.Price(o, OptionType.Put);
            double rhs = o.Spot - o.Strike * Math.Exp(-o.Rate * o.Time);
            double scaled = Math.Abs(lhs - rhs) / Math.Max(o.Spot, o.Strike);
            worst = Math.Max(worst, scaled);
            Assert.True(scaled < 1e-10, $"parity broken for {o}: |C-P-(S-Ke^-rT)| / max(S,K) = {scaled:E3}");
        }
        output.WriteLine($"Worst parity error / max(S,K) over 10,000 cases: {worst:E3}");
    }

    // ---------- Greeks vs finite differences ----------

    public static TheoryData<double, double, double, double, double, OptionType> GreekCases()
    {
        var data = new TheoryData<double, double, double, double, double, OptionType>();
        foreach (var type in new[] { OptionType.Call, OptionType.Put })
        {
            data.Add(100, 100, 1.0, 0.05, 0.20, type);   // at the money
            data.Add(120, 100, 0.5, 0.03, 0.25, type);   // call in the money
            data.Add(80, 100, 2.0, 0.01, 0.40, type);    // call out of the money
            data.Add(100, 110, 0.25, -0.01, 0.15, type); // negative rate
        }
        return data;
    }

    // Central differences have truncation error O(h²) and rounding error O(ε/h) (O(ε/h²) for
    // gamma), where ε ≈ 2.2e-16. With the step sizes below both are far below 1e-6 relative,
    // so a mismatch bigger than that means a wrong formula, not numerical noise.
    private const double GreekRelTolerance = 1e-6;

    private static void AssertClose(double expected, double actual, string name)
    {
        double scale = Math.Max(1.0, Math.Abs(expected));
        Assert.True(Math.Abs(expected - actual) <= GreekRelTolerance * scale,
            $"{name}: analytic {expected:R}, finite difference {actual:R}");
    }

    [Theory]
    [MemberData(nameof(GreekCases))]
    public void Delta_MatchesFiniteDifference(double s, double k, double t, double r, double v, OptionType type)
    {
        var o = new OptionInput(s, k, t, r, v);
        double h = 1e-4 * s;
        double fd = (BlackScholes.Price(o with { Spot = s + h }, type)
                   - BlackScholes.Price(o with { Spot = s - h }, type)) / (2 * h);
        AssertClose(BlackScholes.Delta(o, type), fd, "delta");
    }

    [Theory]
    [MemberData(nameof(GreekCases))]
    public void Gamma_MatchesFiniteDifference(double s, double k, double t, double r, double v, OptionType type)
    {
        var o = new OptionInput(s, k, t, r, v);
        double h = 1e-4 * s;
        double fd = (BlackScholes.Price(o with { Spot = s + h }, type)
                   - 2 * BlackScholes.Price(o, type)
                   + BlackScholes.Price(o with { Spot = s - h }, type)) / (h * h);
        AssertClose(BlackScholes.Gamma(o), fd, "gamma");
    }

    [Theory]
    [MemberData(nameof(GreekCases))]
    public void Vega_MatchesFiniteDifference(double s, double k, double t, double r, double v, OptionType type)
    {
        var o = new OptionInput(s, k, t, r, v);
        double h = 1e-4;
        double fd = (BlackScholes.Price(o with { Volatility = v + h }, type)
                   - BlackScholes.Price(o with { Volatility = v - h }, type)) / (2 * h);
        AssertClose(BlackScholes.Vega(o), fd, "vega");
    }

    [Theory]
    [MemberData(nameof(GreekCases))]
    public void Theta_MatchesFiniteDifference(double s, double k, double t, double r, double v, OptionType type)
    {
        var o = new OptionInput(s, k, t, r, v);
        double h = 1e-5; // much smaller than the shortest T in the cases, so T − h > 0
        // Theta is −∂V/∂T: value lost as expiry gets closer.
        double fd = -(BlackScholes.Price(o with { Time = t + h }, type)
                    - BlackScholes.Price(o with { Time = t - h }, type)) / (2 * h);
        AssertClose(BlackScholes.Theta(o, type), fd, "theta");
    }

    // ---------- Edge cases ----------

    [Theory]
    [InlineData(100, 90)]  // in the money for the call
    [InlineData(100, 110)] // in the money for the put
    [InlineData(100, 100)] // at the money
    public void TinyTime_PriceApproachesIntrinsic(double s, double k)
    {
        var o = new OptionInput(s, k, Time: 1e-10, Rate: 0.05, Volatility: 0.2);
        double discK = k * Math.Exp(-o.Rate * o.Time);
        // At the money the time value is about S·σ·√T/√(2π) ≈ 8e-5, so allow 1e-4.
        Assert.Equal(Math.Max(s - discK, 0), BlackScholes.Price(o, OptionType.Call), 1e-4);
        Assert.Equal(Math.Max(discK - s, 0), BlackScholes.Price(o, OptionType.Put), 1e-4);
    }

    [Theory]
    [InlineData(100, 90)]
    [InlineData(100, 110)]
    public void TinyVolatility_PriceApproachesDiscountedIntrinsic(double s, double k)
    {
        // With no randomness the stock grows at r, so the payoff is known now:
        // call = max(S − K·e^(−rT), 0), put = max(K·e^(−rT) − S, 0).
        var o = new OptionInput(s, k, Time: 1, Rate: 0.05, Volatility: 1e-10);
        double discK = k * Math.Exp(-o.Rate * o.Time);
        Assert.Equal(Math.Max(s - discK, 0), BlackScholes.Price(o, OptionType.Call), 1e-9);
        Assert.Equal(Math.Max(discK - s, 0), BlackScholes.Price(o, OptionType.Put), 1e-9);
    }

    [Theory]
    [InlineData(1e-3)] // deep in the money call / deep out of the money put
    [InlineData(1e6)]  // deep out of the money call / deep in the money put
    public void DeepMoneyness_StaysFiniteAndWithinBounds(double k)
    {
        var o = new OptionInput(Spot: 100, Strike: k, Time: 1, Rate: 0.05, Volatility: 0.2);
        double discK = k * Math.Exp(-o.Rate * o.Time);
        double call = BlackScholes.Price(o, OptionType.Call);
        double put = BlackScholes.Price(o, OptionType.Put);

        Assert.True(double.IsFinite(call) && double.IsFinite(put));
        Assert.InRange(call, Math.Max(o.Spot - discK, 0) - 1e-9, o.Spot);
        Assert.InRange(put, Math.Max(discK - o.Spot, 0) - 1e-9, discK);
    }

    public static TheoryData<OptionInput, string> InvalidInputs() => new()
    {
        { new OptionInput(0, 100, 1, 0.05, 0.2), "Spot" },
        { new OptionInput(-1, 100, 1, 0.05, 0.2), "Spot" },
        { new OptionInput(double.NaN, 100, 1, 0.05, 0.2), "Spot" },
        { new OptionInput(double.PositiveInfinity, 100, 1, 0.05, 0.2), "Spot" },
        { new OptionInput(100, 0, 1, 0.05, 0.2), "Strike" },
        { new OptionInput(100, 100, 0, 0.05, 0.2), "Time" },
        { new OptionInput(100, 100, -0.5, 0.05, 0.2), "Time" },
        { new OptionInput(100, 100, 1, double.NaN, 0.2), "Rate" },
        { new OptionInput(100, 100, 1, 0.05, 0), "Volatility" },
        { new OptionInput(100, 100, 1, 0.05, double.NaN), "Volatility" },
        { default, "Spot" }, // default(OptionInput) is all zeros and skips any constructor
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void InvalidInput_ThrowsWithParameterName(OptionInput o, string param)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Price(o, OptionType.Call));
        Assert.Equal(param, ex.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Delta(o, OptionType.Put));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Gamma(o));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Vega(o));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlackScholes.Theta(o, OptionType.Call));
    }
}
