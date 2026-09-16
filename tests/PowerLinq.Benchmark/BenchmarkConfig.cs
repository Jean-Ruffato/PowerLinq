using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;

namespace PowerLinq.Benchmark;

public static class BenchmarkConfig
{
    /// <summary>
    /// Shared configuration. <see cref="MemoryDiagnoser"/> is the central piece: the move to an AST
    /// changes the allocation profile above all, not just the time.
    /// </summary>
    /// <param name="includeDefaultJob">
    /// When <c>false</c>, the default job is not added — it lets the job come from the command
    /// line. Without this, a <c>--job dry</c> would be <em>added to</em> the job from here and each
    /// benchmark would run twice.
    /// </param>
    public static IConfig Create(bool includeDefaultJob = true)
    {
        ManualConfig config = ManualConfig.Create(DefaultConfig.Instance)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumn(RankColumn.Arabic)
            .AddExporter(MarkdownExporter.GitHub)
            .AddExporter(HtmlExporter.Default)
            .AddExporter(JsonExporter.Full)
            // CsvMeasurementsExporter feeds the RPlotExporter — the two go together
            .AddExporter(CsvMeasurementsExporter.Default)
            .AddExporter(RPlotExporter.Default)
            .WithSummaryStyle(SummaryStyle.Default.WithRatioStyle(RatioStyle.Trend));

        return includeDefaultJob
            ? config.AddJob(Job.Default
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithId("PowerLinq"))
            : config;
    }
}
