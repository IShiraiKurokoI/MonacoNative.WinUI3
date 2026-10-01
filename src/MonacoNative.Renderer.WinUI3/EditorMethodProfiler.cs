using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>
/// Opt-in, UI-thread method profiling. Inclusive/exclusive wall-clock duration and
/// managed allocated bytes are sampled per scope. This is not a CPU sampler or
/// a heap-retained-size measurement; nested calls and awaits need that distinction.
/// </summary>
internal sealed class EditorMethodProfiler
{
    private const int SampleLimit = 1024;
    private readonly Dictionary<string, Measurement> _measurements = new(StringComparer.Ordinal);
    private ProfileScope? _current;

    public bool Enabled { get; set; }

    public IDisposable? Measure(string method)
    {
        if (!Enabled)
            return null; // Disabled hot path does not allocate or start Stopwatch.
        var scope = new ProfileScope(this, method, _current);
        _current = scope;
        return scope;
    }

    private void Complete(ProfileScope scope)
    {
        if (!ReferenceEquals(_current, scope))
            throw new InvalidOperationException("Profiler scopes must end in stack order on the UI thread.");
        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - scope.StartTicks);
        var allocated = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - scope.StartAllocation);
        _current = scope.Parent;
        if (scope.Parent is { } parent)
        {
            parent.ChildTicks += elapsed;
            parent.ChildAllocated += allocated;
        }

        if (!_measurements.TryGetValue(scope.Method, out var measurement))
        {
            measurement = new Measurement();
            _measurements.Add(scope.Method, measurement);
        }
        measurement.Note(elapsed, Math.Max(0, elapsed - scope.ChildTicks),
            allocated, Math.Max(0, allocated - scope.ChildAllocated));
    }

    public MethodProfileReport Snapshot(bool reset = false)
    {
        if (_current is not null)
            throw new InvalidOperationException("Capture the profiler outside timed methods.");
        var allEntries = _measurements.Select(kv => kv.Value.ToEntry(kv.Key)).ToArray();
        var totalExclusiveMs = allEntries.Sum(entry => entry.TotalExclusiveMs);
        var totalExclusiveAllocated = allEntries.Sum(entry => entry.ExclusiveAllocatedBytes);
        var entries = allEntries.Select(entry => entry with
            {
                ExclusiveTimeSharePercent = totalExclusiveMs > 0 ? 100 * entry.TotalExclusiveMs / totalExclusiveMs : 0,
                ExclusiveAllocationSharePercent = totalExclusiveAllocated > 0 ? 100 * entry.ExclusiveAllocatedBytes / totalExclusiveAllocated : 0,
            })
            .OrderByDescending(item => item.TotalExclusiveMs).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        using var process = Process.GetCurrentProcess();
        var report = new MethodProfileReport(
            DateTimeOffset.UtcNow,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.TotalProcessorTime.TotalMilliseconds,
            GC.GetTotalMemory(forceFullCollection: false),
            "UI-thread wall-clock; inclusive scopes can overlap; exclusive subtracts instrumented children. " +
            "AllocatedBytes is managed thread allocation (not retained heap, process memory or native/GPU allocations). " +
            "Percentages are shares of instrumented exclusive time/managed allocations only. " +
            "P95 from the last 1024 calls per method; noninstrumented descendants remain in parent exclusive time.",
            entries);
        if (reset)
            _measurements.Clear();
        return report;
    }

    public (string Json, string Csv) Export(string directory, bool reset = false)
    {
        var report = Snapshot(reset);
        Directory.CreateDirectory(directory);
        var suffix = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var jsonFile = Path.Combine(directory, $"method-profile-{suffix}.json");
        var csvFile = Path.Combine(directory, $"method-profile-{suffix}.csv");
        File.WriteAllText(jsonFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        var csv = new StringBuilder("Method,Calls,TotalInclusiveMs,TotalExclusiveMs,AvgInclusiveMs,P95InclusiveMs,MaxInclusiveMs,TotalAllocatedBytes,ExclusiveAllocatedBytes,AvgAllocatedBytes,ExclusiveTimeSharePercent,ExclusiveAllocationSharePercent\n");
        foreach (var item in report.Methods)
        {
            csv.Append('"')
                .Append(item.Name.Replace("\"", "\"\"", StringComparison.Ordinal))
                .Append('"').Append(',')
                .Append(item.Calls).Append(',')
                .Append(item.TotalInclusiveMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.TotalExclusiveMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.AvgInclusiveMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.P95InclusiveMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.MaxInclusiveMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.TotalAllocatedBytes).Append(',')
                .Append(item.ExclusiveAllocatedBytes).Append(',')
                .Append(item.AvgAllocatedBytes.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.ExclusiveTimeSharePercent.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.ExclusiveAllocationSharePercent.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append('\n');
        }
        File.WriteAllText(csvFile, csv.ToString(), Encoding.UTF8);
        return (jsonFile, csvFile);
    }

    private sealed class Measurement
    {
        private readonly long[] _recent = new long[SampleLimit];
        private int _sampleCount;
        private int _sampleNext;
        public long Calls;
        public long TotalTicks;
        public long ExclusiveTicks;
        public long MaxTicks;
        public long TotalAllocated;
        public long ExclusiveAllocated;

        public void Note(long ticks, long exclusiveTicks, long allocated, long exclusiveAllocated)
        {
            ++Calls;
            TotalTicks += ticks;
            ExclusiveTicks += exclusiveTicks;
            MaxTicks = Math.Max(MaxTicks, ticks);
            TotalAllocated += allocated;
            ExclusiveAllocated += exclusiveAllocated;
            _recent[_sampleNext] = ticks;
            _sampleNext = (_sampleNext + 1) % SampleLimit;
            _sampleCount = Math.Min(SampleLimit, _sampleCount + 1);
        }

        public MethodProfileEntry ToEntry(string name)
        {
            var recent = _recent.AsSpan(0, _sampleCount).ToArray();
            Array.Sort(recent);
            var index = Math.Max(0, (int)Math.Ceiling(0.95 * recent.Length) - 1);
            static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            return new MethodProfileEntry(name, Calls, Ms(TotalTicks), Ms(ExclusiveTicks),
                Calls == 0 ? 0 : Ms(TotalTicks) / Calls,
                recent.Length == 0 ? 0 : Ms(recent[index]), Ms(MaxTicks),
                TotalAllocated, ExclusiveAllocated, Calls == 0 ? 0 : (double)TotalAllocated / Calls);
        }
    }

    private sealed class ProfileScope : IDisposable
    {
        private readonly EditorMethodProfiler _owner;
        private bool _disposed;
        internal ProfileScope? Parent { get; }
        internal string Method { get; }
        internal long StartTicks { get; }
        internal long StartAllocation { get; }
        internal long ChildTicks;
        internal long ChildAllocated;

        internal ProfileScope(EditorMethodProfiler owner, string method, ProfileScope? parent)
        {
            _owner = owner;
            Parent = parent;
            Method = method;
            StartAllocation = GC.GetAllocatedBytesForCurrentThread();
            StartTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner.Complete(this);
        }
    }
}

internal sealed record MethodProfileReport(
    DateTimeOffset CapturedAtUtc,
    long ProcessWorkingSetBytes,
    long ProcessPrivateBytes,
    double ProcessCpuMilliseconds,
    long EstimatedManagedHeapBytes,
    string MeasurementNotes,
    MethodProfileEntry[] Methods);
internal sealed record MethodProfileEntry(
    string Name, long Calls, double TotalInclusiveMs, double TotalExclusiveMs,
    double AvgInclusiveMs, double P95InclusiveMs, double MaxInclusiveMs,
    long TotalAllocatedBytes, long ExclusiveAllocatedBytes, double AvgAllocatedBytes)
{
    public double ExclusiveTimeSharePercent { get; init; }
    public double ExclusiveAllocationSharePercent { get; init; }
}
