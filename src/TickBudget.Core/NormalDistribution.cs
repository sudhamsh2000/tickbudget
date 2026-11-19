namespace TickBudget.Core;

/// <summary>
/// Standard normal distribution: density φ(x) and cumulative distribution N(x).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Cdf"/> uses Hart's (1968) rational approximation in the form published by
/// Graeme West, "Better approximations to cumulative normal functions", Wilmott Magazine (2005).
/// West describes it as accurate to double precision.
/// </para>
/// <para>
/// Measured against MathNet.Numerics 5.0.0 (see <c>NormalDistributionTests</c>):
/// max absolute error 2.2e-16 on [−10, 10]. Relative error is ~1e-14 for |x| ≤ 3 but grows in
/// the far tail, up to ~9e-9 for x &lt; −7 (where N(x) &lt; 1e-12). So it is double precision in
/// absolute terms, not in relative terms deep in the tail. For option prices, which multiply
/// N(·) by S or K, the absolute error is what matters.
/// </para>
/// <para>
/// Why this algorithm: it uses only <see cref="Math.Exp"/> and a few multiply-adds, has no
/// branches inside the polynomial loops, and needs no erf/erfc (which .NET does not provide).
/// That keeps it fast and allocation-free, which matters for the latency work later.
/// </para>
/// </remarks>
public static class NormalDistribution
{
    private const double InvSqrt2Pi = 0.398942280401432677939946059934; // 1/√(2π)
    private const double Sqrt2Pi = 2.50662827463100050241576528481;     // √(2π)

    /// <summary>Standard normal density φ(x) = e^(−x²/2) / √(2π).</summary>
    public static double Pdf(double x) => InvSqrt2Pi * Math.Exp(-0.5 * x * x);

    /// <summary>Standard normal cumulative distribution N(x) = P(Z ≤ x).</summary>
    public static double Cdf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;

        double xAbs = Math.Abs(x);
        double tail; // P(Z > |x|), the upper-tail probability

        if (xAbs > 37.0)
        {
            // e^(−37²/2) ≈ 1e-298: the tail is below anything a double can usefully hold.
            tail = 0.0;
        }
        else
        {
            double e = Math.Exp(-0.5 * xAbs * xAbs);
            if (xAbs < 7.07106781186547)
            {
                // Ratio of two polynomials in |x| (Horner form), times e^(−x²/2).
                double num = 3.52624965998911E-02;
                num = num * xAbs + 0.700383064443688;
                num = num * xAbs + 6.37396220353165;
                num = num * xAbs + 33.912866078383;
                num = num * xAbs + 112.079291497871;
                num = num * xAbs + 221.213596169931;
                num = num * xAbs + 220.206867912376;

                double den = 8.83883476483184E-02;
                den = den * xAbs + 1.75566716318264;
                den = den * xAbs + 16.064177579207;
                den = den * xAbs + 86.7807322029461;
                den = den * xAbs + 296.564248779674;
                den = den * xAbs + 637.333633378831;
                den = den * xAbs + 793.826512519948;
                den = den * xAbs + 440.413735824752;

                tail = e * num / den;
            }
            else
            {
                // Far tail: continued-fraction expansion of the Mills ratio.
                double cf = xAbs + 0.65;
                cf = xAbs + 4.0 / cf;
                cf = xAbs + 3.0 / cf;
                cf = xAbs + 2.0 / cf;
                cf = xAbs + 1.0 / cf;
                tail = e / cf / Sqrt2Pi;
            }
        }

        // Symmetry: N(−a) = tail(a), N(a) = 1 − tail(a).
        return x > 0 ? 1.0 - tail : tail;
    }
}
