namespace PowerLinq.Benchmark.Model;

internal static class SingleRow<T> where T : class
{
    public static readonly List<T> Value = [Activator.CreateInstance<T>()];
}
