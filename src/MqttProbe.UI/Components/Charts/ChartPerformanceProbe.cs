using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MqttProbe.UI.Components.Charts;

// Temporary #209 instrumentation, gated by MQTT_PROBE_CHART_PERF_OUTPUT pointing at an absolute
// JSONL path. Samples carry counts and monotonic durations only, never series values.
internal static class ChartPerformanceProbe
{
    internal const string Create = "create";
    internal const string Update = "update";

    private const string OutputVariable = "MQTT_PROBE_CHART_PERF_OUTPUT";
    private const int MaxBufferedLines = 8192;
    private const int FlushThresholdLines = 2048;
    private const int FlushChunkChars = 32 * 1024;
    private const string HealthSuffix = ".health";
    private const string DrainRequestSuffix = ".drain";
    private const string DrainedSuffix = ".drained";

    private static readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _drainBudget = TimeSpan.FromSeconds(2);
    private static readonly Lock _writerGate = new();
    private static readonly SemaphoreSlim _markerGate = new(1, 1);
    private static readonly string? _envPath = ResolveOutputPath();
    private static JsonlWriter? _writer;
    private static CancellationTokenSource? _loopCancellation;
    private static string? _overridePath;

    static ChartPerformanceProbe()
    {
        if (_envPath is null)
            return;

        Record(string.Create(CultureInfo.InvariantCulture,
            $$"""{"k":"dotnet","ph":"start","utc":{{Now()}},"ver":1,"pid":{{Environment.ProcessId}}}"""));
    }

    internal readonly record struct Token(
        string Text,
        string ChartId,
        int Generation,
        int Sequence,
        string Operation);

    internal sealed record Stamp(long Ticks, long Utc);

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct SpecState(bool Live, bool Ready, bool Pending, bool Reset);

    internal static bool Enabled => ActivePath is not null;

    internal static bool IsUsablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    internal static Stamp? Mark() => Enabled ? new Stamp(Stopwatch.GetTimestamp(), Now()) : null;

    internal static Token? SpecPrepared(
        string chartId,
        int generation,
        int sequence,
        string operation,
        Stamp? start,
        ChartRendererSpec spec,
        SpecState state)
    {
        var writer = GetWriter();
        if (writer is null)
            return null;

        var startTicks = start?.Ticks ?? 0;
        var startUtc = start?.Utc ?? 0;
        var endTicks = Stopwatch.GetTimestamp();
        var endUtc = Now();
        var elapsedUs = startTicks == 0
            ? 0
            : (long)Stopwatch.GetElapsedTime(startTicks, endTicks).TotalMicroseconds;
        var points = 0;
        var seriesJson = new StringBuilder(spec.Series.Count * 24).Append('[');
        for (var index = 0; index < spec.Series.Count; index++)
        {
            var series = spec.Series[index];
            points += series.Points.Count;
            if (index > 0)
                seriesJson.Append(',');
            seriesJson.Append("{\"n\":").Append(series.Points.Count);
            seriesJson.Append(",\"t\":");
            if (series.Points.Count > 0)
                seriesJson.Append((long)Math.Round(series.Points[^1][0], MidpointRounding.AwayFromZero));
            else
                seriesJson.Append("null");
            seriesJson.Append('}');
        }

        seriesJson.Append(']');

        writer.Enqueue(string.Create(CultureInfo.InvariantCulture,
            $$"""{"k":"dotnet","ph":"spec","c":"{{chartId}}","g":{{generation}},"s":{{sequence}},"op":"{{operation}}","bTicks":{{startTicks}},"bUtc":{{startUtc}},"eTicks":{{endTicks}},"eUtc":{{endUtc}},"us":{{elapsedUs}},"utc":{{endUtc}},"series":{{spec.Series.Count}},"pts":{{points}},"se":{{seriesJson}},"live":{{(state.Live ? 1 : 0)}},"ready":{{(state.Ready ? 1 : 0)}},"pend":{{(state.Pending ? 1 : 0)}},"reset":{{(state.Reset ? 1 : 0)}}}"""));
        return new Token(
            string.Create(CultureInfo.InvariantCulture, $"{generation}:{sequence}"),
            chartId,
            generation,
            sequence,
            operation);
    }

    internal sealed class InteropWatch
    {
        private readonly Token _token;
        private readonly int _queued;
        private readonly long _waitTicks = Stopwatch.GetTimestamp();
        private readonly long _waitUtc = Now();
        private long _acquiredTicks;
        private long _acquiredUtc;
        private long _dispatchTicks;
        private long _dispatchUtc;

        private InteropWatch(Token token, int queued)
        {
            _token = token;
            _queued = queued;
        }

        internal static InteropWatch? Start(Token? token, int queued) =>
            token is { } value && Enabled ? new InteropWatch(value, queued) : null;

        internal void Acquired()
        {
            _acquiredTicks = Stopwatch.GetTimestamp();
            _acquiredUtc = Now();
        }

        internal void Dispatch()
        {
            _dispatchTicks = Stopwatch.GetTimestamp();
            _dispatchUtc = Now();
        }

        internal void Complete(bool succeeded)
        {
            var endTicks = Stopwatch.GetTimestamp();
            var endUtc = Now();
            var writer = GetWriter();
            if (writer is null)
                return;

            var acquired = _acquiredTicks == 0 ? endTicks : _acquiredTicks;
            var dispatch = _dispatchTicks == 0 ? acquired : _dispatchTicks;
            writer.Enqueue(string.Create(CultureInfo.InvariantCulture,
                $$"""{"k":"dotnet","ph":"interop","c":"{{_token.ChartId}}","g":{{_token.Generation}},"s":{{_token.Sequence}},"op":"{{_token.Operation}}","q":{{_queued}},"wUs":{{Delta(_waitTicks, acquired)}},"hUs":{{Delta(acquired, dispatch)}},"rUs":{{Delta(dispatch, endTicks)}},"tUs":{{Delta(_waitTicks, endTicks)}},"wUtc":{{_waitUtc}},"aUtc":{{_acquiredUtc}},"dUtc":{{_dispatchUtc}},"cUtc":{{endUtc}},"utc":{{endUtc}},"ok":{{(succeeded ? 1 : 0)}}}"""));
        }

        private static long Delta(long from, long to) =>
            (long)Stopwatch.GetElapsedTime(from, to).TotalMicroseconds;
    }
    internal sealed class StorageSampler : IDisposable
    {
        private static readonly long _periodTicks = Stopwatch.Frequency;
        private static int _active;
        private readonly string _chartId;
        private readonly int _generation;
        private readonly Guid[] _seriesIds;
        private readonly Func<Guid, DateTime?> _read;
        private readonly Timer _timer;
        private long _expectedTicks = Stopwatch.GetTimestamp();
        private int _sample;
        private int _stopped;

        private StorageSampler(string chartId, int generation, Guid[] seriesIds, Func<Guid, DateTime?> read)
        {
            _chartId = chartId;
            _generation = generation;
            _seriesIds = [.. seriesIds];
            _read = read;
            _timer = new Timer(_ => Sample(), state: null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            Interlocked.Increment(ref _active);
        }

        internal static int ActiveCount => Volatile.Read(ref _active);

        internal static StorageSampler? Start(string chartId, int generation, Guid[] seriesIds, Func<Guid, DateTime?> read) =>
            Enabled ? new StorageSampler(chartId, generation, seriesIds, read) : null;

        internal void Sample()
        {
            if (Volatile.Read(ref _stopped) != 0)
                return;

            var ticks = Stopwatch.GetTimestamp();
            var utc = Now();
            var expected = _expectedTicks;
            _expectedTicks = ticks + _periodTicks;
            var latenessUs = (long)((ticks - expected) * 1_000_000d / Stopwatch.Frequency);
            var writer = GetWriter();
            if (writer is null)
                return;

            var line = new StringBuilder(256);
            line.Append("{\"k\":\"dotnet\",\"ph\":\"storage\",\"c\":\"").Append(_chartId)
                .Append("\",\"g\":").Append(_generation)
                .Append(",\"n\":").Append(Interlocked.Increment(ref _sample))
                .Append(",\"utc\":").Append(utc)
                .Append(",\"mono\":").Append(ticks)
                .Append(",\"lateUs\":").Append(latenessUs)
                .Append(",\"ts\":[");
            for (var index = 0; index < _seriesIds.Length; index++)
            {
                if (index > 0)
                    line.Append(',');
                line.Append(_read(_seriesIds[index]) is { } last ? ToEpochUtc(last) : "null");
            }

            line.Append("]}");
            if (Volatile.Read(ref _stopped) == 0)
                writer.Enqueue(line.ToString());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            _timer.Dispose();
            Interlocked.Decrement(ref _active);
        }

        private static long ToEpochUtc(DateTime value) =>
            new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }

    private static string? ActivePath => _overridePath ?? _envPath;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? ResolveOutputPath()
    {
        var configured = Environment.GetEnvironmentVariable(OutputVariable);
        return IsUsablePath(configured) ? configured : null;
    }

    private static JsonlWriter? GetWriter()
    {
        var path = ActivePath;
        if (path is null)
            return null;

        lock (_writerGate)
        {
            if (_writer is not null)
                return _writer;

            var writer = new JsonlWriter(path, MaxBufferedLines, FlushChunkChars);
            _writer = writer;
            if (_envPath is not null && _overridePath is null)
            {
                _loopCancellation = new CancellationTokenSource();
                var cancellation = _loopCancellation.Token;
                _ = Task.Run(() => LoopAsync(writer, cancellation), CancellationToken.None);
            }

            return writer;
        }
    }

    private static void Record(string line) => GetWriter()?.Enqueue(line);

    private static async Task LoopAsync(JsonlWriter writer, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastFlush = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
            {
                var drainRequested = File.Exists(writer.Path + DrainRequestSuffix);
                var due = Stopwatch.GetElapsedTime(lastFlush) >= _flushInterval;
                if (!drainRequested && !due && writer.Pending < FlushThresholdLines)
                    continue;

                if (await writer.FlushAsync(drainRequested ? _drainBudget : TimeSpan.Zero).ConfigureAwait(false))
                    lastFlush = Stopwatch.GetTimestamp();

                await WriteHealthAsync(writer).ConfigureAwait(false);
                if (drainRequested && writer.Pending == 0)
                    await WriteDrainedAsync(writer).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The writer was retired; a test or host shutdown owns the remaining state.
        }
    }

    private static async Task WriteHealthAsync(JsonlWriter writer)
    {
        await WriteMarkerAsync(writer.Path + HealthSuffix, StatusJson("health", writer, complete: false)).ConfigureAwait(false);
    }

    private static async Task WriteDrainedAsync(JsonlWriter writer)
    {
        var complete = writer.Pending == 0 && writer.WriteErrors == 0 && writer.Dropped == 0;
        await WriteMarkerAsync(writer.Path + DrainedSuffix, StatusJson("drained", writer, complete)).ConfigureAwait(false);
    }

    private static string StatusJson(string phase, JsonlWriter writer, bool complete) =>
        string.Create(CultureInfo.InvariantCulture,
            $$"""{"k":"dotnet","ph":"{{phase}}","utc":{{Now()}},"ver":1,"written":{{writer.Written}},"pending":{{writer.Pending}},"dropped":{{writer.Dropped}},"writeErrors":{{writer.WriteErrors}},"complete":{{(complete ? "true" : "false")}}}""");

    private static async Task WriteMarkerAsync(string path, string content)
    {
        await _markerGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, content, CancellationToken.None).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A missing marker makes the external harness time out and invalidate the evidence.
        }
        finally
        {
            _markerGate.Release();
        }
    }

    internal static async Task UseOutputPathForTestsAsync(string? path)
    {
        await StopWriterAsync().ConfigureAwait(false);
        _overridePath = path;
    }

    internal static async Task StopWriterAsync()
    {
        JsonlWriter? writer;
        CancellationTokenSource? cancellation;
        lock (_writerGate)
        {
            writer = _writer;
            cancellation = _loopCancellation;
            _writer = null;
            _loopCancellation = null;
        }

        if (cancellation is not null)
            await cancellation.CancelAsync().ConfigureAwait(false);

        cancellation?.Dispose();
        if (writer is not null)
            await writer.FlushAsync(TimeSpan.Zero).ConfigureAwait(false);
    }

    internal static async Task<bool> FlushForTestsAsync(bool drain)
    {
        var writer = GetWriter();
        if (writer is null)
            return false;

        var wrote = await writer.FlushAsync(drain ? _drainBudget : TimeSpan.Zero).ConfigureAwait(false);
        await WriteHealthAsync(writer).ConfigureAwait(false);
        if (drain && writer.Pending == 0)
            await WriteDrainedAsync(writer).ConfigureAwait(false);
        return wrote;
    }

    internal sealed class JsonlWriter(string path, int maxLines, int chunkChars)
    {
        private readonly ConcurrentQueue<string> _pending = new();
        private readonly Lock _flushLock = new();
        private int _reserved;
        private bool _flushing;
        private long _written;
        private long _dropped;
        private long _writeErrors;

        internal string Path { get; } = path;

        internal long Written => Interlocked.Read(ref _written);

        internal long Dropped => Interlocked.Read(ref _dropped);

        internal long WriteErrors => Interlocked.Read(ref _writeErrors);

        internal int Pending => Volatile.Read(ref _reserved);

        internal void Enqueue(string line)
        {
            if (Interlocked.Increment(ref _reserved) > maxLines)
            {
                Interlocked.Decrement(ref _reserved);
                Interlocked.Increment(ref _dropped);
                return;
            }

            _pending.Enqueue(line);
        }

        internal async Task<bool> FlushAsync(TimeSpan budget)
        {
            lock (_flushLock)
            {
                if (_flushing)
                    return false;
                _flushing = true;
            }

            try
            {
                var deadline = budget > TimeSpan.Zero
                    ? Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency)
                    : 0;
                var wroteAny = false;
                while (true)
                {
                    var snapshot = _pending.ToArray();
                    if (snapshot.Length == 0)
                        break;

                    var (chunk, take, skipped) = BuildChunk(snapshot);
                    if (take > 0 && !await AppendAsync(chunk).ConfigureAwait(false))
                        return false;

                    for (var index = 0; index < take + skipped; index++)
                    {
                        _pending.TryDequeue(out _);
                        Interlocked.Decrement(ref _reserved);
                    }

                    if (skipped > 0)
                        Interlocked.Add(ref _dropped, skipped);

                    Interlocked.Add(ref _written, take);
                    wroteAny |= take > 0;

                    if (deadline == 0 || Stopwatch.GetTimestamp() >= deadline)
                        break;
                }

                return wroteAny;
            }
            finally
            {
                lock (_flushLock)
                {
                    _flushing = false;
                }
            }
        }

        private (string Chunk, int Count, int Skipped) BuildChunk(string[] snapshot)
        {
            var chunk = new StringBuilder(chunkChars);
            var bytes = 0;
            var take = 0;
            var skipped = 0;
            while (take + skipped < snapshot.Length)
            {
                var line = snapshot[take + skipped];
                var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (lineBytes > chunkChars)
                {
                    skipped++;
                    continue;
                }

                if (bytes + lineBytes > chunkChars)
                    break;

                chunk.Append(line).Append('\n');
                bytes += lineBytes;
                take++;
            }

            return (chunk.ToString(), take, skipped);
        }

        private async Task<bool> AppendAsync(string chunk)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                await File.AppendAllTextAsync(Path, chunk, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Interlocked.Increment(ref _writeErrors);
                return false;
            }
        }
    }
}
