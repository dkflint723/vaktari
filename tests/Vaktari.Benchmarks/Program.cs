using BenchmarkDotNet.Running;

namespace Vaktari.Benchmarks;

/// <summary>
/// The numbers behind the claims.
///
/// **Nothing in the project measured anything until now** — the caps, the
/// batch sizes and the timers were reasoned about, and the one figure ever
/// written down (the sixteen bytes a creation time adds to an entry) was
/// measured by hand and recorded in a comment. These are the pure pieces the
/// listing's cost is made of: the natural sort, the name rules, the tree
/// walk, the path arithmetic. Run one, or all:
///
///   dotnet run -c Release --project tests/Vaktari.Benchmarks -- --filter '*Sorting*'
///   dotnet run -c Release --project tests/Vaktari.Benchmarks -- --filter '*'
///
/// Release, always: a Debug build measures the JIT's caution, not the code.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
