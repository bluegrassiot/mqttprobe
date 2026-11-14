using ApexCharts;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.UI.Services.Formatting;
using MqttProbe.UI.Tests.TestHelpers;
using ChartCardComponent = MqttProbe.UI.Components.Charts.ChartCard;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartCardRendererTests : BunitTestContext
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private IChartDataService _chartData = null!;
    private IUiSettings _ui = null!;
    private ChartTimeFormatterStub _formatter = null!;
    private FakeTimeProvider _clock = null!;
    private Bunit.BunitJSModuleInterop _module = null!;
    private GatedChartRuntime _runtime = null!;
    private readonly Dictionary<Guid, List<ChartDataPoint>> _points = [];

    [SetUp]
    public void SetupMocks()
    {
        _chartData = Substitute.For<IChartDataService>();
        _chartData.GetPoints(Arg.Any<Guid>())
            .Returns(call => (IReadOnlyList<ChartDataPoint>)PointsFor(call.Arg<Guid>()));
        _clock = new FakeTimeProvider(_start);
        _formatter = new ChartTimeFormatterStub();
        _ui = SettingsSubstituteRegistration.CreateUiSettings();

        Services.AddSingleton(_chartData);
        Services.AddSingleton(_ui);
        Services.AddSingleton<IDeviceTimeFormatter>(_formatter);
        _runtime = new GatedChartRuntime(JSInterop.JSRuntime);
        Services.AddSingleton<IJSRuntime>(_runtime);
        EnsureMudProviders();

        _module = JSInterop.SetupModule(ChartRendererInterop.ModulePath);
    }

    private void AllowAdapterCalls()
    {
        _module.Setup<bool>("createChart", _ => true).SetResult(true);
        _module.Setup<bool>("updateChart", _ => true).SetResult(true);
        _module.Setup<bool>("disposeChart", _ => true).SetResult(true);
    }

    private IReadOnlyList<ChartDataPoint> PointsFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) ? [.. points] : [];

    private GatedChartModule GateAdapterCreation()
    {
        var module = new GatedChartModule();
        _runtime.Module = module;
        return module;
    }

    private void Record(ChartSeries series, double secondsAgo, double value = 1)
    {
        if (!_points.TryGetValue(series.Id, out var points))
            _points[series.Id] = points = [];
        points.Add(new ChartDataPoint(_clock.GetUtcNow().UtcDateTime.AddSeconds(-secondsAgo), value));
    }

    private IRenderedComponent<ChartCardComponent> RenderCard(
        ChartConfiguration configuration,
        ChartRendererKind renderer = ChartRendererKind.Apex,
        Action? onRollback = null) =>
        Render<ChartCardComponent>(parameters => parameters
            .Add(c => c.Configuration, configuration)
            .Add(c => c.Clock, _clock)
            .Add(c => c.Renderer, renderer)
            .Add(c => c.OnRendererRollback, EventCallback.Factory.Create(this, () => onRollback?.Invoke())));

    private void Rerender(IRenderedComponent<ChartCardComponent> cut, ChartConfiguration configuration, ChartRendererKind renderer) =>
        cut.Render(parameters => parameters
            .Add(c => c.Configuration, configuration)
            .Add(c => c.Clock, _clock)
            .Add(c => c.Renderer, renderer));

    private static ChartConfiguration Chart(params ChartSeries[] series) => new()
    {
        Name = "Chart",
        Series = [.. series]
    };

    private ChartRendererSpec CreateSpec(int index = 0) =>
        JSInterop.Invocations["createChart"][index].Arguments[3]
            .Should().BeOfType<ChartRendererSpec>().Subject;

    private ChartRendererSpec UpdateSpec(int index = 0) =>
        JSInterop.Invocations["updateChart"][index].Arguments[1]
            .Should().BeOfType<ChartRendererSpec>().Subject;

    private bool UpdateResetsViewport(int index = 0) =>
        JSInterop.Invocations["updateChart"][index].Arguments[2].Should().BeOfType<bool>().Subject;

    private static long Epoch(double secondsAgo) =>
        new DateTimeOffset(_start.UtcDateTime.AddSeconds(-secondsAgo)).ToUnixTimeMilliseconds();

    private static void AssertPoint(double[] point, long epochMs, double value)
    {
        point[0].Should().Be(epochMs);
        point[1].Should().Be(value);
    }

    [Test]
    public void DefaultRenderer_KeepsApexAndNeverLoadsTheEChartsModule()
    {
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);

        var cut = RenderCard(Chart(series));

        cut.WaitForAssertion(
            () => cut.FindComponents<ApexChart<ChartDataPoint>>().Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        cut.FindAll(".chart-card-echarts").Should().BeEmpty();
        JSInterop.Invocations.Identifiers.Should().NotContain("createChart");
        JSInterop.Invocations.Should().NotContain(invocation =>
            invocation.Identifier == "import"
            && Equals(invocation.Arguments[0], ChartRendererInterop.ModulePath));
    }

    [Test]
    public void EChartsSelection_RendersTheAdapterWithTheProductionSeries()
    {
        AllowAdapterCalls();
        var temp = new ChartSeries { DisplayName = "Temp", Color = "#F97316" };
        var flow = new ChartSeries { DisplayName = "Flow", Color = "#A78BFA" };
        Record(temp, 3, 21.5);
        Record(temp, 1, 22.25);
        Record(flow, 2, 3);
        var chart = Chart(temp, flow);
        chart.Type = MqttProbe.Core.Models.Chart.ChartType.Bar;

        var cut = RenderCard(chart, ChartRendererKind.ECharts);

        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));
        cut.FindAll(".chart-card-echarts").Should().ContainSingle();
        cut.FindComponents<ApexChart<ChartDataPoint>>().Should().BeEmpty();

        var spec = CreateSpec();
        spec.Type.Should().Be("bar");
        spec.Series.Should().HaveCount(2);
        spec.Series[0].Id.Should().Be(temp.Id.ToString(), "series identity is the stored chart series id");
        spec.Series[0].Name.Should().Be("Temp");
        spec.Series[0].Color.Should().Be("#F97316");
        spec.Series[0].Points.Should().HaveCount(2);
        AssertPoint(spec.Series[0].Points[0], Epoch(3), 21.5);
        AssertPoint(spec.Series[0].Points[1], Epoch(1), 22.25);
        spec.Series[1].Id.Should().Be(flow.Id.ToString());
        AssertPoint(spec.Series[1].Points[0], Epoch(2), 3);
        spec.Zone.Should().Be(MessageTimePreferences.ZoneLocal);
        spec.ClockFormat.Should().Be(MessageTimePreferences.ClockFormat24Hour);
    }

    [Test]
    public void EChartsSelection_KeepsIrregularTimestampsExactlyAsBuffered()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 37, 1);
        Record(series, 33, 2);
        Record(series, 4, 3);

        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);

        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));
        var points = CreateSpec().Series[0].Points;
        points.Should().HaveCount(3, "the renderer receives the buffered points without resampling");
        points.Select(point => point[0]).Should().Equal([Epoch(37), Epoch(33), Epoch(4)]);
        points.Select(point => point[1]).Should().Equal([1, 2, 3]);
    }

    [Test]
    public void EChartsSelection_KeepsDuplicateDisplayNamesAsDistinctSeries()
    {
        AllowAdapterCalls();
        var first = new ChartSeries { DisplayName = "Temp", Color = "#F97316" };
        var second = new ChartSeries { DisplayName = "Temp", Color = "#A78BFA" };
        Record(first, 1, 21.5);
        Record(second, 1, 22.5);

        var cut = RenderCard(Chart(first, second), ChartRendererKind.ECharts);

        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));
        var spec = CreateSpec();
        spec.Series.Should().HaveCount(2);
        spec.Series.Select(series => series.Name).Should().Equal("Temp", "Temp");
        spec.Series.Select(series => series.Id).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void EChartsSelection_SendsTheStandardFontProfileByDefault()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);

        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);

        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));
        CreateSpec().FontProfile.Should().Be(FontProfiles.Standard);
    }

    [Test]
    public void AccessibleFontProfile_ReachesTheAdapterAndIsReappliedWhenItChanges()
    {
        AllowAdapterCalls();
        _ui.Ui.FontProfile = FontProfiles.Accessible;
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));
        CreateSpec().FontProfile.Should().Be(FontProfiles.Accessible);

        _ui.Ui.FontProfile = FontProfiles.Standard;
        _ui.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        UpdateSpec().FontProfile.Should().Be(FontProfiles.Standard);
        UpdateSpec().Series[0].Points.Should().ContainSingle(
            "a font profile change must not resample history");
    }

    [Test]
    public void TimeWindowChange_ReappliesTheAdapterWithTheFilteredPoints()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        var now = DateTime.UtcNow;
        _points[series.Id] =
        [
            new ChartDataPoint(now.AddMinutes(-10), 1),
            new ChartDataPoint(now.AddSeconds(-10), 2)
        ];
        var chart = Chart(series);
        var cut = RenderCard(chart, ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));
        CreateSpec().Series[0].Points.Should().HaveCount(2);

        chart.TimeWindowMinutes = 1;
        Rerender(cut, chart, ChartRendererKind.ECharts);

        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        UpdateSpec().Series[0].Points.Should().ContainSingle().Which[1].Should().Be(2);
    }

    [Test]
    public async Task TimeWindowChange_ResetsThePinnedAdapterViewportAndTheZoneCaption()
    {
        AllowAdapterCalls();
        var transition = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-2)).ToUnixTimeMilliseconds();
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ItemFor = epoch => epoch < transition
            ? ("EDT", "Eastern Daylight Time", "UTC-04:00")
            : ("EST", "Eastern Standard Time", "UTC-05:00");
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(DateTime.UtcNow.AddMinutes(-4), 1),
            new ChartDataPoint(DateTime.UtcNow.AddMinutes(-1), 2)
        ]);
        var chart = Chart(series);
        var cut = RenderCard(chart, ChartRendererKind.ECharts);
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(transition, transition + 60_000));
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));

        chart.TimeWindowMinutes = 30;
        Rerender(cut, chart, ChartRendererKind.ECharts);

        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        UpdateResetsViewport().Should().BeTrue(
            "the renderer must clear the pinned window the host just cleared");
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));

        _chartData.OnDataUpdated += Raise.Event<Action>();
        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 2), TimeSpan.FromSeconds(5));
        UpdateResetsViewport(1).Should().BeFalse("a later data update keeps the reset window");
    }

    [Test]
    public void DataUpdatesReachTheAdapterAndKeepTheApexChartOutOfTheTree()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 3, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));

        Record(series, 0, 23);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        UpdateSpec().Series[0].Points.Should().HaveCount(2);
        UpdateSpec().Series[0].Points[1].Should().Equal(Epoch(0), 23);
        cut.FindComponents<ApexChart<ChartDataPoint>>().Should().BeEmpty();
        JSInterop.Invocations.Identifiers.Should().NotContain("requestAnimationFrame");
    }

    [Test]
    public void SwitchingBackToApexKeepsBufferedDataAndTheSavedConfiguration()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 3, 21.5);
        Record(series, 1, 22.25);
        var chart = Chart(series);
        var cut = RenderCard(chart, ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));

        Rerender(cut, chart, ChartRendererKind.Apex);

        cut.WaitForAssertion(
            () => cut.FindComponents<ApexPointSeries<ChartDataPoint>>().Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        var items = cut.FindComponent<ApexPointSeries<ChartDataPoint>>().Instance.Items;
        items.Select(point => point.Value).Should()
            .Equal([21.5, 22.25], "history is read from the untouched buffer");
        cut.Instance.Configuration.Should().BeSameAs(chart);
        chart.Series.Should().ContainSingle().Which.Id.Should().Be(series.Id);
        cut.FindAll(".chart-card-echarts").Should().BeEmpty();
        _module.VerifyInvoke("disposeChart", 1);
    }

    [Test]
    public void AdapterFailure_IsVisibleAndOffersARollbackToApex()
    {
        _module.Setup<bool>("updateChart", _ => true).SetResult(true);
        _module.Setup<bool>("disposeChart", _ => true).SetResult(true);
        _module.Setup<bool>("createChart", _ => true)
            .SetException(new JSException("Failed to fetch dynamically imported module"));
        var rolledBack = false;
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);

        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts, () => rolledBack = true);

        cut.WaitForAssertion(
            () => cut.FindAll(".chart-card-echarts-error").Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        cut.Markup.Should().Contain("ECharts could not start");
        cut.Markup.Should().Contain("Failed to fetch dynamically imported module");
        cut.FindComponents<ApexChart<ChartDataPoint>>().Should().BeEmpty(
            "a broken renderer must not silently look like an empty chart");

        cut.Find(".chart-card-echarts-error button").Click();

        rolledBack.Should().BeTrue("the card asks its host for the Apex renderer");
    }

    [Test]
    public async Task DisposingTheCardReleasesTheAdapterInstance()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));

        await cut.Instance.DisposeAsync();

        _module.VerifyInvoke("disposeChart", 1);
        JSInterop.Invocations.Identifiers.Should().NotContain("updateChart");
    }

    [Test]
    public void SwitchingRenderers_DoesNotLeaveDuplicateAdapterInstances()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var chart = Chart(series);
        var cut = RenderCard(chart, ChartRendererKind.Apex);
        cut.WaitForAssertion(
            () => cut.FindComponents<ApexChart<ChartDataPoint>>().Should().ContainSingle(),
            TimeSpan.FromSeconds(5));

        Rerender(cut, chart, ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));

        Rerender(cut, chart, ChartRendererKind.Apex);
        cut.WaitForAssertion(() => _module.VerifyInvoke("disposeChart", 1), TimeSpan.FromSeconds(5));

        Rerender(cut, chart, ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 2), TimeSpan.FromSeconds(5));
        _module.VerifyInvoke("disposeChart", 1);
        cut.FindComponents<ApexChart<ChartDataPoint>>().Should().BeEmpty();
    }

    [Test]
    public void RemovingEverySeriesDisposesTheAdapterAndAddingThemBackCreatesANewChart()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var withSeries = Chart(series);
        var cut = RenderCard(withSeries, ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));
        var firstElement = JSInterop.Invocations["createChart"][0].Arguments[0];

        Rerender(cut, Chart(), ChartRendererKind.ECharts);

        cut.WaitForAssertion(() => _module.VerifyInvoke("disposeChart", 1), TimeSpan.FromSeconds(5));
        cut.FindAll(".chart-card-echarts").Should().BeEmpty();

        Rerender(cut, withSeries, ChartRendererKind.ECharts);

        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 2), TimeSpan.FromSeconds(5));
        cut.FindAll(".chart-card-echarts").Should().ContainSingle();
        JSInterop.Invocations["createChart"][1].Arguments[0].Should().NotBe(firstElement,
            "the re-added plot is a new element, so the adapter must draw into it");
        CreateSpec(1).Series.Should().ContainSingle();
        JSInterop.Invocations.Identifiers.Should().NotContain("updateChart",
            "the disposed chart must not receive data meant for the new container");
    }

    [Test]
    public void EChartsHover_KeepsLiveUpdatesAndThePauseControl()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));

        cut.Find(".chart-card-plot").MouseOver();

        cut.WaitForAssertion(
            () => cut.Find(".chart-card-status .mud-chip").TextContent.Trim().Should().Be("Live"),
            TimeSpan.FromSeconds(5));
        cut.FindAll("button[title='Resume live updates']").Should().BeEmpty(
            "hovering must not replace the pause control with a resume control");
        cut.Find("button[title='Pause live updates (keeps the view still)']").Should().NotBeNull();

        Record(series, 0, 23);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        UpdateSpec().Series[0].Points.Should().HaveCount(2,
            "an ECharts card keeps applying live data while the pointer is over the plot");
    }

    [Test]
    public async Task EChartsManualPause_SurvivesHoverAndPointerLeave()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));

        cut.Find("button[title='Pause live updates (keeps the view still)']").Click();
        cut.WaitForAssertion(
            () => cut.Find(".chart-card-status .mud-chip").TextContent.Trim().Should().Be("Paused"),
            TimeSpan.FromSeconds(5));

        cut.Find(".chart-card-plot").MouseOver();
        cut.Find(".chart-card-status .mud-chip").TextContent.Trim().Should().Be("Paused");
        cut.Find("button[title='Resume live updates']").Should().NotBeNull(
            "the manual pause control stays visible and invokable while hovering");

        cut.Find(".chart-card-plot").TriggerEvent("onmouseleave", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find(".chart-card-status .mud-chip").TextContent.Trim().Should().Be("Paused",
            "a manual pause must outlive the pointer");

        Record(series, 0, 23);
        _chartData.OnDataUpdated += Raise.Event<Action>();
        await cut.InvokeAsync(() => { });

        JSInterop.Invocations.Identifiers.Should().NotContain("updateChart",
            "a manual pause keeps buffered data out of the renderer");

        cut.Find("button[title='Resume live updates']").Click();
        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        UpdateSpec().Series[0].Points.Should().HaveCount(2);
    }

    [Test]
    public void PausedEChartsCard_DefersUpdatesUntilResumed()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));

        cut.Find("button[title='Pause live updates (keeps the view still)']").Click();
        cut.WaitForAssertion(
            () => cut.Find(".chart-card-status .mud-chip").TextContent.Trim().Should().Be("Paused"),
            TimeSpan.FromSeconds(5));

        Record(series, 0, 22);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        cut.WaitForAssertion(
            () => cut.FindAll(".chart-card-echarts").Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        JSInterop.Invocations.Identifiers.Should().NotContain("updateChart",
            "a paused card keeps the applied data");

        cut.Find("button[title='Resume live updates']").Click();
        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        UpdateSpec().Series[0].Points.Should().HaveCount(2);
    }

    [Test]
    public async Task DataArrivingDuringAdapterCreationIsAppliedWhenCreationCompletes()
    {
        var module = GateAdapterCreation();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 3, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        await module.CreateStarted.WaitAsync(TimeSpan.FromSeconds(5));
        module.Creates[0].Series[0].Points.Should().ContainSingle();

        Record(series, 0, 23);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        module.ReleaseCreate();

        cut.WaitForAssertion(() => module.Updates.Should().ContainSingle(), TimeSpan.FromSeconds(5));
        module.Updates[0].Series[0].Points.Should().HaveCount(2);
        module.Updates[0].Series[0].Points[1].Should().Equal(Epoch(0), 23);
    }

    [Test]
    public async Task TimePreferencesChangingDuringAdapterCreationReachTheNewChart()
    {
        var module = GateAdapterCreation();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 3, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        await module.CreateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };
        _ui.UiPreferencesChanged += Raise.Event<Action>();

        module.ReleaseCreate();

        cut.WaitForAssertion(() => module.Updates.Should().ContainSingle(), TimeSpan.FromSeconds(5));
        module.Updates[0].Zone.Should().Be(MessageTimePreferences.ZoneUtc);
        module.Updates[0].ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);
    }

    [Test]
    public async Task PausedCardDoesNotApplyDataThatArrivesDuringAdapterCreation()
    {
        var module = GateAdapterCreation();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 3, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        await module.CreateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        cut.Find("button[title='Pause live updates (keeps the view still)']").Click();
        Record(series, 0, 23);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        module.ReleaseCreate();
        cut.Find("button[title='Resume live updates']").Click();

        cut.WaitForAssertion(() => module.Updates.Should().ContainSingle(), TimeSpan.FromSeconds(5));
        module.Updates[0].Series[0].Points.Should().HaveCount(2);
    }

    [Test]
    public async Task PausingDuringAdapterCreationDefersQueuedDataUntilResume()
    {
        var module = GateAdapterCreation();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 3, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        await module.CreateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Record(series, 2, 22);
        _chartData.OnDataUpdated += Raise.Event<Action>();
        await cut.InvokeAsync(() => { });

        cut.Find("button[title='Pause live updates (keeps the view still)']").Click();
        cut.WaitForAssertion(
            () => cut.Find(".chart-card-status .mud-chip").TextContent.Trim().Should().Be("Paused"),
            TimeSpan.FromSeconds(5));

        Record(series, 0, 23);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        await cut.InvokeAsync(module.ReleaseCreate);
        await cut.InvokeAsync(() => { });

        module.Updates.Should().BeEmpty(
            "data queued before pausing must stay undrawn while the card is paused");
        cut.FindAll(".chart-card-echarts-error").Should().BeEmpty();

        cut.Find("button[title='Resume live updates']").Click();

        cut.WaitForAssertion(() => module.Updates.Should().ContainSingle(), TimeSpan.FromSeconds(5));
        module.Updates[0].Series[0].Points.Select(point => point[1]).Should().Equal([21.5, 22, 23],
            "resuming draws the latest buffer, including data that arrived while paused");
    }

    [Test]
    public async Task PreferenceChangesWhilePausedDoNotApplyQueuedData()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 3, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));

        cut.Find("button[title='Pause live updates (keeps the view still)']").Click();
        cut.WaitForAssertion(
            () => cut.Find(".chart-card-status .mud-chip").TextContent.Trim().Should().Be("Paused"),
            TimeSpan.FromSeconds(5));

        Record(series, 0, 23);
        _chartData.OnDataUpdated += Raise.Event<Action>();

        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };
        _ui.UiPreferencesChanged += Raise.Event<Action>();
        await cut.InvokeAsync(() => { });

        JSInterop.Invocations.Identifiers.Should().NotContain("updateChart",
            "a preference change must not smuggle buffered data into a paused chart");

        cut.Find("button[title='Resume live updates']").Click();

        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        UpdateSpec().Zone.Should().Be(MessageTimePreferences.ZoneUtc);
        UpdateSpec().Series[0].Points.Select(point => point[1]).Should().Equal([21.5, 23],
            "the deferred preference update applies the latest buffered data on resume");
    }

    [Test]
    public async Task SwitchingRenderersDuringAdapterCreationDisposesTheStaleChart()
    {
        var module = GateAdapterCreation();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var chart = Chart(series);
        var cut = RenderCard(chart, ChartRendererKind.ECharts);
        await module.CreateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Rerender(cut, chart, ChartRendererKind.Apex);
        module.ReleaseCreate();

        cut.WaitForAssertion(
            () => cut.FindComponents<ApexPointSeries<ChartDataPoint>>().Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => module.Disposed.Should().BeGreaterThan(0), TimeSpan.FromSeconds(5));
        module.Updates.Should().BeEmpty();

        Rerender(cut, chart, ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => module.Creates.Should().HaveCount(2), TimeSpan.FromSeconds(5));
        cut.FindAll(".chart-card-echarts").Should().ContainSingle();
    }

    [Test]
    public async Task StaleAdapterCreationFailureAfterSwitchingAwayAndBackDoesNotPoisonTheNewSelection()
    {
        var module = GateAdapterCreation();
        module.FailNextCreate(new JSException("the detached container cannot host a chart"));
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var chart = Chart(series);
        var cut = RenderCard(chart, ChartRendererKind.ECharts);
        await module.CreateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Rerender(cut, chart, ChartRendererKind.Apex);
        Rerender(cut, chart, ChartRendererKind.ECharts);

        await cut.InvokeAsync(module.ReleaseCreate);
        await cut.InvokeAsync(() => { });

        cut.FindAll(".chart-card-echarts-error").Count.Should().Be(0,
            "a failure from the detached generation must not describe the current selection");
        cut.WaitForAssertion(() => module.Creates.Should().HaveCount(2), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(
            () => cut.FindAll(".chart-card-echarts").Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        cut.FindAll(".chart-card-echarts-error").Should().BeEmpty(
            "a failure from the detached generation must not describe the current selection");
        module.Creates[1].Series[0].Points.Should().ContainSingle(
            "the new selection draws the buffered data itself");
    }

    [Test]
    public void TimePreferenceChange_ReappliesTheAdapterWithTheNewClockFormat()
    {
        AllowAdapterCalls();
        var series = new ChartSeries { DisplayName = "Temp" };
        Record(series, 1, 21.5);
        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        cut.WaitForAssertion(() => _module.VerifyInvoke("createChart", 1), TimeSpan.FromSeconds(5));

        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };
        _ui.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        UpdateSpec().Zone.Should().Be(MessageTimePreferences.ZoneUtc);
        UpdateSpec().ClockFormat.Should().Be(MessageTimePreferences.ClockFormat12Hour);
        UpdateSpec().Series[0].Points.Should().HaveCount(1, "a preference change does not resample history");
    }

    [Test]
    public async Task EChartsViewportChangesDriveTheExistingZoneCaption()
    {
        AllowAdapterCalls();
        var transition = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ItemFor = epoch => epoch < transition
            ? ("EDT", "Eastern Daylight Time", "UTC-04:00")
            : ("EST", "Eastern Standard Time", "UTC-05:00");
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 11, 1, 5, 58, 0, DateTimeKind.Utc), 1),
            new ChartDataPoint(new DateTime(2026, 11, 1, 6, 2, 0, DateTimeKind.Utc), 2)
        ]);

        var cut = RenderCard(Chart(series), ChartRendererKind.ECharts);
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(transition, transition + 60_000));
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(null, null));
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ChangingChartType_ClearsAPinnedViewportAndTheZoneCaption()
    {
        AllowAdapterCalls();
        var transition = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ItemFor = epoch => epoch < transition
            ? ("EDT", "Eastern Daylight Time", "UTC-04:00")
            : ("EST", "Eastern Standard Time", "UTC-05:00");
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 11, 1, 5, 58, 0, DateTimeKind.Utc), 1),
            new ChartDataPoint(new DateTime(2026, 11, 1, 6, 2, 0, DateTimeKind.Utc), 2)
        ]);
        var chart = Chart(series);
        var cut = RenderCard(chart, ChartRendererKind.ECharts);
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(transition, transition + 60_000));
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));

        chart.Type = MqttProbe.Core.Models.Chart.ChartType.Bar;
        Rerender(cut, chart, ChartRendererKind.ECharts);

        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RenamingASeries_KeepsAPinnedViewport()
    {
        AllowAdapterCalls();
        var transition = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "America/New_York";
        _formatter.ItemFor = epoch => epoch < transition
            ? ("EDT", "Eastern Daylight Time", "UTC-04:00")
            : ("EST", "Eastern Standard Time", "UTC-05:00");
        var series = new ChartSeries { DisplayName = "Temp" };
        _chartData.GetPoints(series.Id).Returns(
        [
            new ChartDataPoint(new DateTime(2026, 11, 1, 5, 58, 0, DateTimeKind.Utc), 1),
            new ChartDataPoint(new DateTime(2026, 11, 1, 6, 2, 0, DateTimeKind.Utc), 2)
        ]);
        var chart = Chart(series);
        var cut = RenderCard(chart, ChartRendererKind.ECharts);
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EDT / EST"),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Instance.OnEChartsViewportChanged(transition, transition + 60_000));
        cut.WaitForAssertion(
            () => cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST"),
            TimeSpan.FromSeconds(5));

        series.DisplayName = "Temperature";
        Rerender(cut, chart, ChartRendererKind.ECharts);

        cut.WaitForAssertion(() => _module.VerifyInvoke("updateChart", 1), TimeSpan.FromSeconds(5));
        cut.Find("button.chart-zone-token").TextContent.Trim().Should().Be("EST",
            "a rename is not a data shape change, so the pinned window stays");
    }

    private sealed class GatedChartRuntime(IJSRuntime inner) : IJSRuntime
    {
        internal GatedChartModule? Module { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            IsModuleImport(identifier, args)
                ? new ValueTask<TValue>((TValue)(object)Module!)
                : inner.InvokeAsync<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            IsModuleImport(identifier, args)
                ? new ValueTask<TValue>((TValue)(object)Module!)
                : inner.InvokeAsync<TValue>(identifier, cancellationToken, args);

        private bool IsModuleImport(string identifier, object?[]? args) =>
            Module is not null
            && identifier == "import"
            && args is [string path]
            && path == ChartRendererInterop.ModulePath;
    }

    private sealed class GatedChartModule : IJSObjectReference
    {
        private readonly TaskCompletionSource _createGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _createStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<Exception> _createFailures = new();

        internal Task CreateStarted => _createStarted.Task;
        internal List<ChartRendererSpec> Creates { get; } = [];
        internal List<ChartRendererSpec> Updates { get; } = [];
        internal int Disposed { get; private set; }

        internal void ReleaseCreate() => _createGate.TrySetResult();

        internal void FailNextCreate(Exception failure) => _createFailures.Enqueue(failure);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            switch (identifier)
            {
                case "createChart":
                    Creates.Add((ChartRendererSpec)args![3]!);
                    _createStarted.TrySetResult();
                    await _createGate.Task;
                    if (_createFailures.Count > 0)
                        throw _createFailures.Dequeue();
                    return (TValue)(object)true;
                case "updateChart":
                    Updates.Add((ChartRendererSpec)args![1]!);
                    return (TValue)(object)true;
                case "disposeChart":
                    Disposed++;
                    return (TValue)(object)true;
                default:
                    return default!;
            }
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
