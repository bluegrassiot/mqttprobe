using System.Text;
using System.Text.Json;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartPerformanceProbeTests
{
    private readonly List<string> _paths = [];

    [TearDown]
    public async Task DisableProbe()
    {
        await ChartPerformanceProbe.UseOutputPathForTestsAsync(null);
        foreach (var path in _paths)
        {
            foreach (var suffix in new[] { "", ".health", ".drained", ".drain", ".tmp" })
                TryDelete(path + suffix);
        }

        _paths.Clear();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("chart-perf.jsonl")]
    [TestCase(@"relative\chart-perf.jsonl")]
    public void IsUsablePath_rejects_a_relative_or_empty_target(string? path) =>
        ChartPerformanceProbe.IsUsablePath(path).Should().BeFalse();

    [Test]
    public void IsUsablePath_accepts_an_absolute_target()
    {
        var path = Path.Combine(Path.GetTempPath(), "mqttprobe-chart-perf.jsonl");

        ChartPerformanceProbe.IsUsablePath(path).Should().BeTrue();
    }

    [Test]
    public void IsUsablePath_rejects_a_drive_relative_target()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ChartPerformanceProbe.IsUsablePath(@"C:chart-perf.jsonl").Should().BeFalse(
            "a drive-relative path resolves against the process's current directory");
    }

    [Test]
    public async Task Writer_flushes_every_enqueued_line_once()
    {
        var path = TempPath();
        var writer = new ChartPerformanceProbe.JsonlWriter(path, 64, 4096);

        writer.Enqueue("""{"n":1}""");
        writer.Enqueue("""{"n":2}""");
        var wrote = await writer.FlushAsync(TimeSpan.Zero);

        wrote.Should().BeTrue();
        writer.Written.Should().Be(2);
        writer.Pending.Should().Be(0);
        writer.Dropped.Should().Be(0);
        writer.WriteErrors.Should().Be(0);
        File.ReadAllLines(path).Should().Equal("""{"n":1}""", """{"n":2}""");
    }

    [Test]
    public async Task Writer_keeps_lines_pending_and_counts_a_failed_append()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mqttprobe-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var writer = new ChartPerformanceProbe.JsonlWriter(directory, 64, 4096);

        try
        {
            writer.Enqueue("""{"n":1}""");
            (await writer.FlushAsync(TimeSpan.Zero)).Should().BeFalse();

            writer.WriteErrors.Should().Be(1);
            writer.Written.Should().Be(0);
            writer.Pending.Should().Be(1, "a failed append must not drop or report the line as written");

            (await writer.FlushAsync(TimeSpan.Zero)).Should().BeFalse();
            writer.WriteErrors.Should().Be(2);
            writer.Pending.Should().Be(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Writer_keeps_concurrent_enqueues_and_flushes_exact()
    {
        var path = TempPath();
        var writer = new ChartPerformanceProbe.JsonlWriter(path, 1024, 512);
        var enqueues = Enumerable.Range(0, 4)
            .Select(worker => Task.Run(() =>
            {
                for (var index = 0; index < 250; index++)
                    writer.Enqueue($"{{\"worker\":{worker},\"index\":{index}}}");
            }))
            .ToArray();
        var flushes = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => writer.FlushAsync(TimeSpan.Zero)))
            .ToArray();

        await Task.WhenAll(enqueues);
        await Task.WhenAll(flushes);
        for (var attempt = 0; attempt < 200 && writer.Pending > 0; attempt++)
            await writer.FlushAsync(TimeSpan.Zero);

        writer.Written.Should().Be(1000);
        writer.Pending.Should().Be(0);
        writer.Dropped.Should().Be(0);
        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(1000);
        lines.Distinct(StringComparer.Ordinal).Should().HaveCount(1000);
    }

    [Test]
    public async Task Writer_caps_buffered_lines_and_counts_the_overflow()
    {
        var path = TempPath();
        var writer = new ChartPerformanceProbe.JsonlWriter(path, maxLines: 4, chunkChars: 4096);

        for (var index = 0; index < 6; index++)
            writer.Enqueue($"{{\"n\":{index}}}");

        writer.Dropped.Should().Be(2);
        writer.Pending.Should().Be(4);
        await writer.FlushAsync(TimeSpan.Zero);
        writer.Written.Should().Be(4);
        File.ReadAllLines(path).Should().HaveCount(4);
    }

    [Test]
    public async Task Flush_writes_health_and_a_completed_drain_marker()
    {
        var path = TempPath();
        await ChartPerformanceProbe.UseOutputPathForTestsAsync(path);

        var spec = new ChartRendererSpec(
            "line",
            false,
            "utc",
            "24h",
            "standard",
            [new ChartRendererSeries("series-a", "Alpha", "#2563EB", [[1, 2]])]);
        var state = new ChartPerformanceProbe.SpecState(Live: true, Ready: true, Pending: false, Reset: false);
        var token = ChartPerformanceProbe.SpecPrepared(
            "chart-a", 1, 2, ChartPerformanceProbe.Update, ChartPerformanceProbe.Mark(), spec, state);
        var watch = ChartPerformanceProbe.InteropWatch.Start(token, 0);
        watch.Should().NotBeNull();
        watch!.Acquired();
        watch.Dispatch();
        watch.Complete(succeeded: true);

        (await ChartPerformanceProbe.FlushForTestsAsync(drain: true)).Should().BeTrue();

        File.ReadAllLines(path).Should().Contain(line => line.Contains("\"ph\":\"spec\"", StringComparison.Ordinal));
        File.ReadAllText(path + ".health").Should().Contain("\"writeErrors\":0");
        var drained = File.ReadAllText(path + ".drained");
        drained.Should().Contain("\"complete\":true");
        drained.Should().Contain("\"pending\":0");
    }

    [Test]
    public async Task Spec_records_actual_series_counts_and_last_timestamps_without_names_or_values()
    {
        var path = TempPath();
        await ChartPerformanceProbe.UseOutputPathForTestsAsync(path);

        var spec = new ChartRendererSpec(
            "line",
            false,
            "utc",
            "24h",
            "standard",
            [
                new ChartRendererSeries("first", "Secret Temp", "#2563EB", [[1000, 1.53], [2000, 2.75]]),
                new ChartRendererSeries("second", "Hidden", "#F97316", [])
            ]);
        var state = new ChartPerformanceProbe.SpecState(Live: true, Ready: true, Pending: true, Reset: false);
        var token = ChartPerformanceProbe.SpecPrepared(
            "chart-a", 3, 7, ChartPerformanceProbe.Update, ChartPerformanceProbe.Mark(), spec, state);
        token!.Value.Text.Should().Be("3:7");
        await ChartPerformanceProbe.FlushForTestsAsync(drain: true);

        var line = File.ReadAllLines(path).Single(candidate => candidate.Contains("\"ph\":\"spec\"", StringComparison.Ordinal));
        line.Should().NotContain("Secret Temp").And.NotContain("Hidden")
            .And.NotContain("1.53").And.NotContain("2.75").And.NotContain("\"points\"");
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        root.GetProperty("c").GetString().Should().Be("chart-a");
        root.GetProperty("g").GetInt32().Should().Be(3);
        root.GetProperty("s").GetInt32().Should().Be(7);
        root.GetProperty("op").GetString().Should().Be(ChartPerformanceProbe.Update);
        root.GetProperty("series").GetInt32().Should().Be(2);
        root.GetProperty("pts").GetInt32().Should().Be(2);
        root.GetProperty("bTicks").GetInt64().Should().BePositive();
        root.GetProperty("eTicks").GetInt64().Should().BeGreaterThanOrEqualTo(root.GetProperty("bTicks").GetInt64());
        root.GetProperty("bUtc").GetInt64().Should().BePositive();
        root.GetProperty("eUtc").GetInt64().Should().BeGreaterThanOrEqualTo(root.GetProperty("bUtc").GetInt64());
        root.GetProperty("us").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("live").GetInt32().Should().Be(1);
        root.GetProperty("ready").GetInt32().Should().Be(1);
        root.GetProperty("pend").GetInt32().Should().Be(1);
        root.GetProperty("reset").GetInt32().Should().Be(0);
        var series = root.GetProperty("se");
        series.GetArrayLength().Should().Be(2);
        series[0].GetProperty("n").GetInt32().Should().Be(2);
        series[0].GetProperty("t").GetInt64().Should().Be(2000);
        series[1].GetProperty("n").GetInt32().Should().Be(0);
        series[1].GetProperty("t").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Test]
    public async Task Interop_watch_splits_gate_wait_dispatch_and_completion_for_the_same_token()
    {
        var path = TempPath();
        await ChartPerformanceProbe.UseOutputPathForTestsAsync(path);

        var spec = new ChartRendererSpec(
            "line",
            false,
            "utc",
            "24h",
            "standard",
            [new ChartRendererSeries("first", "Alpha", "#2563EB", [[1000, 2]])]);
        var token = ChartPerformanceProbe.SpecPrepared(
            "chart-b",
            4,
            9,
            ChartPerformanceProbe.Create,
            ChartPerformanceProbe.Mark(),
            spec,
            new ChartPerformanceProbe.SpecState(Live: true, Ready: false, Pending: false, Reset: false));
        var watch = ChartPerformanceProbe.InteropWatch.Start(token, queued: 2);
        watch.Should().NotBeNull();
        watch!.Acquired();
        watch.Dispatch();
        watch.Complete(succeeded: true);
        await ChartPerformanceProbe.FlushForTestsAsync(drain: true);

        var line = File.ReadAllLines(path).Single(candidate => candidate.Contains("\"ph\":\"interop\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        root.GetProperty("c").GetString().Should().Be("chart-b");
        root.GetProperty("g").GetInt32().Should().Be(4);
        root.GetProperty("s").GetInt32().Should().Be(9);
        root.GetProperty("op").GetString().Should().Be(ChartPerformanceProbe.Create);
        root.GetProperty("q").GetInt32().Should().Be(2);
        root.GetProperty("wUs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("hUs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("rUs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("tUs").GetInt64().Should().BeGreaterThanOrEqualTo(root.GetProperty("wUs").GetInt64());
        var waitUtc = root.GetProperty("wUtc").GetInt64();
        var acquiredUtc = root.GetProperty("aUtc").GetInt64();
        var dispatchUtc = root.GetProperty("dUtc").GetInt64();
        var completionUtc = root.GetProperty("cUtc").GetInt64();
        waitUtc.Should().BePositive();
        acquiredUtc.Should().BeGreaterThanOrEqualTo(waitUtc);
        dispatchUtc.Should().BeGreaterThanOrEqualTo(acquiredUtc);
        completionUtc.Should().BeGreaterThanOrEqualTo(dispatchUtc);
        root.GetProperty("ok").GetInt32().Should().Be(1);
        root.GetProperty("utc").GetInt64().Should().Be(completionUtc);

        ChartPerformanceProbe.InteropWatch.Start(token: null, queued: 0).Should().BeNull();
    }

    [Test]
    public async Task Storage_sampler_reports_utc_monotonic_and_per_series_timestamps()
    {
        var path = TempPath();
        await ChartPerformanceProbe.UseOutputPathForTestsAsync(path);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var last = new DateTime(2026, 11, 15, 6, 30, 0, DateTimeKind.Utc);
        var sampler = ChartPerformanceProbe.StorageSampler.Start(
            "chart-a",
            5,
            [first, second],
            seriesId => seriesId == first ? last : null);

        try
        {
            ChartPerformanceProbe.StorageSampler.ActiveCount.Should().Be(1);
            sampler.Should().NotBeNull();
            sampler!.Sample();
            sampler.Sample();
            await ChartPerformanceProbe.FlushForTestsAsync(drain: true);

            var lines = File.ReadAllLines(path)
                .Where(candidate => candidate.Contains("\"ph\":\"storage\"", StringComparison.Ordinal))
                .ToList();
            lines.Should().HaveCount(2);
            lines.Should().OnlyContain(line => !line.Contains("Alpha", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(lines[0]);
            var root = document.RootElement;
            root.GetProperty("c").GetString().Should().Be("chart-a");
            root.GetProperty("g").GetInt32().Should().Be(5);
            root.GetProperty("n").GetInt32().Should().Be(1);
            root.GetProperty("utc").GetInt64().Should().BePositive();
            root.GetProperty("mono").GetInt64().Should().BePositive();
            root.TryGetProperty("lateUs", out _).Should().BeTrue();
            var timestamps = root.GetProperty("ts");
            timestamps.GetArrayLength().Should().Be(2);
            timestamps[0].GetInt64().Should().Be(new DateTimeOffset(last).ToUnixTimeMilliseconds());
            timestamps[1].ValueKind.Should().Be(JsonValueKind.Null);

            var before = File.ReadAllLines(path).Length;
            sampler.Dispose();
            ChartPerformanceProbe.StorageSampler.ActiveCount.Should().Be(0);
            sampler.Sample();
            await ChartPerformanceProbe.FlushForTestsAsync(drain: true);
            File.ReadAllLines(path).Length.Should().Be(before, "a disposed sampler must not emit another sample");
        }
        finally
        {
            sampler?.Dispose();
        }
    }

    [Test]
    public void Storage_sampler_does_not_start_when_the_probe_is_disabled()
    {
        ChartPerformanceProbe.Enabled.Should().BeFalse();
        ChartPerformanceProbe.StorageSampler.ActiveCount.Should().Be(0);
        ChartPerformanceProbe.StorageSampler.Start("chart-a", 1, [Guid.NewGuid()], _ => null)
            .Should().BeNull();
        ChartPerformanceProbe.StorageSampler.ActiveCount.Should().Be(0);
    }

    [Test]
    public async Task Writer_accepts_a_chunk_at_the_exact_utf8_byte_boundary()
    {
        var path = TempPath();
        var writer = new ChartPerformanceProbe.JsonlWriter(path, maxLines: 64, chunkChars: 20);

        writer.Enqueue("123456789");
        writer.Enqueue("123456789");
        writer.Enqueue("123456789");

        (await writer.FlushAsync(TimeSpan.Zero)).Should().BeTrue();
        writer.Written.Should().Be(2, "two 10-byte lines fill the 20-byte chunk exactly");
        writer.Pending.Should().Be(1);

        await writer.FlushAsync(TimeSpan.Zero);
        writer.Written.Should().Be(3);
        writer.Dropped.Should().Be(0);
        new FileInfo(path).Length.Should().Be(30);
    }

    [Test]
    public async Task Writer_never_emits_a_chunk_larger_than_the_byte_cap()
    {
        var path = TempPath();
        var writer = new ChartPerformanceProbe.JsonlWriter(path, maxLines: 64, chunkChars: 16);

        writer.Enqueue("abcdefgh");
        writer.Enqueue("ijklmnop");
        writer.Enqueue("qrstuvwx");

        (await writer.FlushAsync(TimeSpan.Zero)).Should().BeTrue();
        writer.Written.Should().Be(1, "an 18-byte chunk would overshoot the 16-byte cap");

        for (var attempt = 0; attempt < 10 && writer.Pending > 0; attempt++)
            await writer.FlushAsync(TimeSpan.Zero);

        writer.Written.Should().Be(3);
        writer.Pending.Should().Be(0);
        new FileInfo(path).Length.Should().Be(27);
    }

    [Test]
    public async Task Writer_skips_a_single_oversize_record_and_counts_it()
    {
        var path = TempPath();
        var writer = new ChartPerformanceProbe.JsonlWriter(path, maxLines: 64, chunkChars: 16);

        writer.Enqueue("short");
        writer.Enqueue(new string('x', 40));
        writer.Enqueue("tail");

        for (var attempt = 0; attempt < 10 && writer.Pending > 0; attempt++)
            await writer.FlushAsync(TimeSpan.Zero);

        writer.Written.Should().Be(2);
        writer.Dropped.Should().Be(1);
        writer.Pending.Should().Be(0);
        File.ReadAllLines(path).Should().Equal("short", "tail");
        new FileInfo(path).Length.Should().Be(11);
    }

    [Test]
    public async Task Writer_counts_utf8_bytes_not_characters()
    {
        var path = TempPath();
        var writer = new ChartPerformanceProbe.JsonlWriter(path, maxLines: 64, chunkChars: 10);

        writer.Enqueue("éééé");
        writer.Enqueue("漢漢漢漢");
        writer.Enqueue("ab");

        for (var attempt = 0; attempt < 10 && writer.Pending > 0; attempt++)
            await writer.FlushAsync(TimeSpan.Zero);

        writer.Written.Should().Be(2);
        writer.Dropped.Should().Be(1);
        writer.Pending.Should().Be(0);
        var lines = File.ReadAllLines(path);
        lines.Should().Equal("éééé", "ab");
        lines.Should().OnlyContain(line => Encoding.UTF8.GetByteCount(line) + 1 <= 10);
        File.ReadAllText(path).Should().NotContain("漢漢漢漢");
    }

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "mqttprobe-perf-" + Guid.NewGuid().ToString("N") + ".jsonl");
        _paths.Add(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
