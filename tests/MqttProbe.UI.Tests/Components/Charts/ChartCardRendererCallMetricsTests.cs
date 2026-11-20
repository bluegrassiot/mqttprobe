using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.UI.Services.Formatting;
using MqttProbe.UI.Tests.TestHelpers;
using ChartCardComponent = MqttProbe.UI.Components.Charts.ChartCard;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartCardRendererCallMetricsTests : BunitTestContext
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private IChartDataService _chartData = null!;
    private IUiSettings _ui = null!;
    private ChartTimeFormatterStub _formatter = null!;
    private FakeTimeProvider _clock = null!;
    private IUxMetricsService _metrics = null!;
    private SlowChartRuntime _runtime = null!;
    private readonly Dictionary<Guid, List<ChartDataPoint>> _points = [];

    [SetUp]
    public void SetupMocks()
    {
        _chartData = Substitute.For<IChartDataService>();
        _chartData.GetPoints(Arg.Any<Guid>())
            .Returns(call => (IReadOnlyList<ChartDataPoint>)PointsFor(call.Arg<Guid>()));
        _chartData.GetLastPointTimestamp(Arg.Any<Guid>())
            .Returns(call => LastTimestampFor(call.Arg<Guid>()));
        _clock = new FakeTimeProvider(_start);
        _formatter = new ChartTimeFormatterStub();
        _ui = SettingsSubstituteRegistration.CreateUiSettings();
        _metrics = Substitute.For<IUxMetricsService>();

        Services.AddSingleton(_chartData);
        Services.AddSingleton(_ui);
        Services.AddSingleton<IDeviceTimeFormatter>(_formatter);
        _runtime = new SlowChartRuntime(JSInterop.JSRuntime);
        Services.AddSingleton<IJSRuntime>(_runtime);
        EnsureMudProviders();
    }

    private IReadOnlyList<ChartDataPoint> PointsFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) ? [.. points] : [];

    private DateTime? LastTimestampFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) && points.Count > 0 ? points[^1].Timestamp : null;

    private void Record(ChartSeries series, double secondsAgo, double value = 1)
    {
        if (!_points.TryGetValue(series.Id, out var points))
            _points[series.Id] = points = [];
        points.Add(new ChartDataPoint(_clock.GetUtcNow().UtcDateTime.AddSeconds(-secondsAgo), value));
    }

    private void RaiseData() => _chartData.OnDataUpdated += Raise.Event<Action>();

    private static ChartConfiguration Chart(params ChartSeries[] series) => new()
    {
        Name = "Chart",
        Series = [.. series]
    };

    private IRenderedComponent<ChartCardComponent> RenderCard(
        ChartConfiguration configuration,
        bool withMetrics = true) =>
        Render<ChartCardComponent>(parameters =>
        {
            parameters.Add(c => c.Configuration, configuration)
                .Add(c => c.Clock, _clock);
            if (withMetrics)
                parameters.Add(c => c.Metrics, _metrics);
        });

    private SlowUpdateModule UseGatedUpdates()
    {
        var module = new SlowUpdateModule();
        _runtime.Module = module;
        return module;
    }

    private void UseModule(bool updateResult, Exception? updateFailure = null)
    {
        var module = JSInterop.SetupModule(ChartRendererInterop.ModulePath);
        module.Setup<bool>("createChart", _ => true).SetResult(true);
        module.Setup<bool>("disposeChart", _ => true).SetResult(true);
        var update = module.Setup<bool>("updateChart", _ => true);
        if (updateFailure is null)
            update.SetResult(updateResult);
        else
            update.SetException(updateFailure);
    }

    [Test]
    public async Task EChartsUpdate_RecordsTheCallOnlyAfterInteropReturns()
    {
        var module = UseGatedUpdates();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 30, 1);
        var cut = RenderCard(Chart(series));
        await module.WaitForCreateAsync(1);

        try
        {
            Record(series, 20, 2);
            RaiseData();
            await module.WaitForUpdateAsync(1);

            _metrics.DidNotReceive().RecordChartRendererCall(
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>());

            module.ReleaseAll();

            cut.WaitForAssertion(
                () => _metrics.Received(1).RecordChartRendererCall(
                    "ECharts",
                    Arg.Is<TimeSpan>(elapsed => elapsed > TimeSpan.Zero),
                    false),
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            module.ReleaseAll();
        }
    }

    [Test]
    public void EChartsUpdate_NotAppliedRecordsAFailedCallAndKeepsTheErrorBanner()
    {
        UseModule(updateResult: false);
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 30, 1);
        var cut = RenderCard(Chart(series));

        Record(series, 20, 2);
        RaiseData();

        cut.WaitForAssertion(
            () => cut.FindAll(".chart-card-echarts-error").Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        _metrics.Received(1).RecordChartRendererCall("ECharts", Arg.Any<TimeSpan>(), true);
    }

    [Test]
    public void EChartsUpdate_FaultRecordsAFailedCallAndKeepsTheErrorDetail()
    {
        UseModule(updateResult: true, updateFailure: new JSException("renderer rejected the snapshot"));
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 30, 1);
        var cut = RenderCard(Chart(series));

        Record(series, 20, 2);
        RaiseData();

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("renderer rejected the snapshot"),
            TimeSpan.FromSeconds(5));
        cut.FindAll(".chart-card-echarts-error").Should().ContainSingle();
        _metrics.Received(1).RecordChartRendererCall("ECharts", Arg.Any<TimeSpan>(), true);
    }

    [Test]
    public async Task UpdateSettledByDisposal_StillRecordsTheApiOutcome()
    {
        var module = UseGatedUpdates();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 30, 1);
        var cut = RenderCard(Chart(series));
        await module.WaitForCreateAsync(1);

        try
        {
            Record(series, 20, 2);
            RaiseData();
            await module.WaitForUpdateAsync(1);

            var dispose = cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
            module.ReleaseAll();
            await dispose;

            _metrics.Received(1).RecordChartRendererCall(
                "ECharts", Arg.Any<TimeSpan>(), false);
        }
        finally
        {
            module.ReleaseAll();
        }
    }

    [Test]
    public void CardWithoutMetrics_StillAppliesEChartsUpdates()
    {
        UseModule(updateResult: true);
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 30, 1);
        var cut = RenderCard(Chart(series), withMetrics: false);

        Record(series, 20, 2);
        RaiseData();

        cut.WaitForAssertion(
            () => JSInterop.Invocations.Should().Contain(invocation => invocation.Identifier == "updateChart"),
            TimeSpan.FromSeconds(5));
        cut.FindAll(".chart-card-echarts-error").Should().BeEmpty();
        _metrics.DidNotReceive().RecordChartRendererCall(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>());
    }
}
