# TickBudget

A C# options pricing engine built around a **latency budget**.

> **Status: work in progress.** This is a learning project, not a production trading system.
> The full README (math, design decisions, measured benchmarks, limitations) will be written
> once the engine and benchmarks exist. No performance numbers are claimed yet.

## Planned scope

- European option pricing three ways: Black-Scholes (closed form), binomial tree, Monte Carlo
- Greeks and implied volatility
- A separate no-arbitrage guard that rejects impossible prices
- Tail-latency measurement (p50 / p99 / p99.9) with GC activity
- A speed-accuracy frontier across pricing methods
- A latency regression gate against a committed baseline

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build -c Release
dotnet test
```

## Layout

```
src/TickBudget.Core      pricing library
tests/TickBudget.Tests   xUnit tests
bench/TickBudget.Bench   benchmarks (BenchmarkDotNet + tail-latency harness)
docs/                    notes and the generated report
```

## License

MIT. See [LICENSE](LICENSE).
