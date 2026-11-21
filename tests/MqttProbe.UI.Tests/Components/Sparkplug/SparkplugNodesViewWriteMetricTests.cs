using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesViewWriteMetricTests : BunitTestContext
{
    private const string WriteSelector = "button.spb-write-btn";
    private ISparkplugTopologyService _topology = null!;
    private ISparkplugCommandService _commands = null!;
    private SparkplugSettings _settings = null!;
    private IDialogService _dialogService = null!;
    private IDialogReference _dialogRef = null!;
    private ISnackbar _snackbar = null!;
    private DialogParameters? _dialogParameters;

    [SetUp]
    public void SetupMocks()
    {
        _topology = Substitute.For<ISparkplugTopologyService>();
        _topology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(_topology);
        _commands = Substitute.For<ISparkplugCommandService>();
        Services.AddSingleton(_commands);

        _settings = new SparkplugSettings { AllowMetricWrites = true };
        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(_settings);
        Services.AddSparkplugSettings(sparkplugSettings);

        _dialogParameters = null;
        _dialogRef = Substitute.For<IDialogReference>();
        _dialogService = Substitute.For<IDialogService>();
        _dialogService.ShowAsync<WriteMetricDialog>(
                Arg.Any<string?>(),
                Arg.Do<DialogParameters>(value => _dialogParameters = value),
                Arg.Any<DialogOptions>())
            .Returns(_dialogRef);
        Services.AddSingleton(_dialogService);
        _snackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_snackbar);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
        EnsureMudProviders();
    }

    [Test]
    public async Task NodeMetric_Write_PrefillsUnroundedValueAndSendsNcmd()
    {
        _dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok("2.5")));
        _commands.WriteNodeMetricAsync("factory", "edge-01", "Flow Rate", "2.5")
            .Returns(SparkplugCommandResult.Published);
        var cut = RenderNode(NodeWithDevice());

        await cut.InvokeAsync(() => MetricRow(cut, "Flow Rate").QuerySelector(WriteSelector)!.Click());

        _dialogParameters.Should().NotBeNull();
        _dialogParameters!.Get<string>("Topic").Should().Be("spBv1.0/factory/NCMD/edge-01");
        _dialogParameters.Get<string>("MetricName").Should().Be("Flow Rate");
        _dialogParameters.Get<string>("DataType").Should().Be("double");
        _dialogParameters.Get<string>("CurrentValue").Should().Be("12.3457");
        _dialogParameters.Get<string>("InitialValue").Should().Be("12.345678901234567");
        await _commands.Received(1).WriteNodeMetricAsync("factory", "edge-01", "Flow Rate", "2.5");
        _snackbar.Received(1).Add("Wrote Flow Rate on factory/edge-01.", Severity.Info);
    }

    [Test]
    public async Task DeviceMetric_Write_SendsDcmd()
    {
        _dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok("7")));
        _commands.WriteDeviceMetricAsync("factory", "edge-01", "sensor-A", "Setpoint", "7")
            .Returns(SparkplugCommandResult.Published);
        var cut = RenderNode(NodeWithDevice());
        cut.Find(".spb-device-row").Click();

        await cut.InvokeAsync(() => MetricRow(cut, "Setpoint").QuerySelector(WriteSelector)!.Click());

        _dialogParameters!.Get<string>("Topic").Should().Be("spBv1.0/factory/DCMD/edge-01/sensor-A");
        _dialogParameters.Get<string>("InitialValue").Should().Be("5");
        await _commands.Received(1).WriteDeviceMetricAsync("factory", "edge-01", "sensor-A", "Setpoint", "7");
        await _commands.DidNotReceiveWithAnyArgs().WriteNodeMetricAsync(default!, default!, default!, default!);
        _snackbar.Received(1).Add("Wrote Setpoint on factory/edge-01/sensor-A.", Severity.Info);
    }

    [Test]
    public async Task Write_Cancelled_SendsNothing()
    {
        _dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        var cut = RenderNode(NodeWithDevice());

        await cut.InvokeAsync(() => MetricRow(cut, "Flow Rate").QuerySelector(WriteSelector)!.Click());

        await _commands.DidNotReceiveWithAnyArgs().WriteNodeMetricAsync(default!, default!, default!, default!);
        _snackbar.DidNotReceiveWithAnyArgs().Add(default(string)!, default);
    }

    [TestCase(SparkplugCommandResult.NotAllowed,
        "Metric writes are not allowed. Enable Allow metric writes in Settings, Sparkplug.")]
    [TestCase(SparkplugCommandResult.TargetNotFound,
        "Write not sent: Flow Rate is not in the current birth certificate for factory/edge-01.")]
    [TestCase(SparkplugCommandResult.InvalidValue,
        "Write not sent: the value is not valid for Flow Rate (double).")]
    [TestCase(SparkplugCommandResult.Failed,
        "Write to Flow Rate on factory/edge-01 was not sent. Check the broker connection.")]
    public async Task Write_Failure_ShowsWarning(SparkplugCommandResult result, string message)
    {
        _dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok("1")));
        _commands.WriteNodeMetricAsync("factory", "edge-01", "Flow Rate", "1").Returns(result);
        var cut = RenderNode(NodeWithDevice());

        await cut.InvokeAsync(() => MetricRow(cut, "Flow Rate").QuerySelector(WriteSelector)!.Click());

        _snackbar.Received(1).Add(message, Severity.Warning);
    }

    [Test]
    public void WritesOff_DisablesTheButton()
    {
        _settings.AllowMetricWrites = false;

        var cut = RenderNode(NodeWithDevice());

        MetricRow(cut, "Flow Rate").QuerySelector(WriteSelector)!.HasAttribute("disabled").Should().BeTrue();
    }

    private static SpbNode NodeWithDevice()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics =
        [
            new SpbMetricSnapshot("Flow Rate", "double", "12.3457", DateTime.UtcNow, EditValue: "12.345678901234567")
        ];
        var device = new SpbDevice
        {
            DeviceId = "sensor-A",
            NodeId = "edge-01",
            GroupId = "factory",
            Status = SpbNodeStatus.Online
        };
        device.Metrics = [new SpbMetricSnapshot("Setpoint", "int32", "5", DateTime.UtcNow)];
        node.Devices["sensor-A"] = device;
        return node;
    }

    private IRenderedComponent<SparkplugNodesView> RenderNode(SpbNode node)
    {
        var group = new SpbGroup();
        group.Nodes[node.NodeId] = node;
        _topology.Groups.Returns(new Dictionary<string, SpbGroup> { [node.GroupId] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(row => row.TextContent.Contains(node.NodeId)).Click();
        return cut;
    }

    private static AngleSharp.Dom.IElement MetricRow(
        IRenderedComponent<SparkplugNodesView> cut, string metricName) =>
        cut.FindAll("tr.spb-metric-row").Single(row =>
            row.QuerySelector(".spb-td-name")?.GetAttribute("title") == metricName);
}
