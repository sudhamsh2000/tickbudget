using TickBudget.Core;
using Xunit.Abstractions;
using MathNetNormal = MathNet.Numerics.Distributions.Normal;

namespace TickBudget.Tests;

public class NormalDistributionTests(ITestOutputHelper output)
{
    // Tolerance for |our N(x) − MathNet N(x)|. Set just above the error measured on this
    // machine, so a mistyped coefficient fails the test instead of hiding under a loose bound.
    private const double CdfAbsTolerance = 1e-14;

    [Fact]
    public void Cdf_MatchesMathNet()
    {
        // Dense grid over [−10, 10], step 0.001 (20,001 points). Outside ±10 both tails are < 1e-23.
        double maxAbsErr = 0, worstX = 0;
        for (int i = -10_000; i <= 10_000; i++)
        {
            double x = i / 1000.0;
            double err = Math.Abs(NormalDistribution.Cdf(x) - MathNetNormal.CDF(0, 1, x));
            if (err > maxAbsErr) { maxAbsErr = err; worstX = x; }
        }

        output.WriteLine($"Max |N(x) - MathNet| on [-10, 10] step 0.001: {maxAbsErr:E3} at x = {worstX}");
        Assert.True(maxAbsErr < CdfAbsTolerance, $"max abs error {maxAbsErr:E3} at x = {worstX}");
    }

    [Theory]
    // Relative error grows smoothly as x moves into the tail. Tolerances are ~2-3x the error
    // measured on the dev machine (Apple M2, .NET 10.0.404) in each band:
    //   [-3, -1]: 1.5e-14   [-5, -3]: 4.6e-11   [-6, -5]: 5.4e-10
    //   [-7.07, -6]: 2.9e-9 (rational branch)   [-37, -7.08]: 8.9e-9 (continued-fraction branch)
    [InlineData(-3.0, -1.0, 5e-14)]
    [InlineData(-5.0, -3.0, 1e-10)]
    [InlineData(-6.0, -5.0, 1e-9)]
    [InlineData(-7.07, -6.0, 6e-9)]
    [InlineData(-37.0, -7.08, 2e-8)]
    public void Cdf_LowerTail_RelativeError(double from, double to, double tolerance)
    {
        // In the lower tail the values are tiny, so absolute error says little. Check relative error.
        double maxRelErr = 0, worstX = 0;
        for (double x = from; x <= to; x += 0.001)
        {
            double reference = MathNetNormal.CDF(0, 1, x);
            double rel = Math.Abs(NormalDistribution.Cdf(x) - reference) / reference;
            if (rel > maxRelErr) { maxRelErr = rel; worstX = x; }
        }

        output.WriteLine($"Max relative error on [{from}, {to}] step 0.001: {maxRelErr:E3} at x = {worstX:F3}");
        Assert.True(maxRelErr < tolerance, $"max relative error {maxRelErr:E3} at x = {worstX:F3}");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.96)]
    [InlineData(6.0)]
    [InlineData(8.0)]
    public void Cdf_IsSymmetric(double x) =>
        Assert.Equal(1.0, NormalDistribution.Cdf(x) + NormalDistribution.Cdf(-x), 1e-15);

    [Fact]
    public void Cdf_HandlesExtremes()
    {
        Assert.Equal(0.5, NormalDistribution.Cdf(0.0), 15);
        Assert.Equal(0.0, NormalDistribution.Cdf(double.NegativeInfinity));
        Assert.Equal(1.0, NormalDistribution.Cdf(double.PositiveInfinity));
        Assert.True(double.IsNaN(NormalDistribution.Cdf(double.NaN)));
    }

    [Fact]
    public void Pdf_MatchesMathNet()
    {
        // Relative tolerance: "15 decimal places" can fail when two values 1e-17 apart
        // round to different 15th digits.
        for (int i = -100; i <= 100; i++)
        {
            double x = i / 10.0;
            double expected = MathNetNormal.PDF(0, 1, x);
            Assert.True(Math.Abs(NormalDistribution.Pdf(x) - expected) <= 1e-14 * expected, $"x = {x}");
        }
    }
}
