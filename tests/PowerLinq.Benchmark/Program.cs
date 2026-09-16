using BenchmarkDotNet.Running;
using PowerLinq.Benchmark;

// BenchmarkSwitcher accepts BenchmarkDotNet's standard arguments:
//   --list flat            lists every benchmark
//   --filter *Visitor*     runs a subset
//   --job dry              a quick sanity run (1 invocation, throwaway numbers)
//   --artifacts <dir>      keeps the baseline apart from the post-refactor run
//
// BenchmarkDotNet adds jobs instead of replacing them; if the user passed --job, the default job
// from here is omitted so each benchmark does not run twice.
bool hasJobOverride = args.Any(arg => arg.Equals("--job", StringComparison.OrdinalIgnoreCase));

BenchmarkSwitcher
    .FromAssembly(typeof(BenchmarkConfig).Assembly)
    .Run(args, BenchmarkConfig.Create(includeDefaultJob: !hasJobOverride));
