using Microsoft.Extensions.Logging;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Emulation;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class EmulatorConfigurationStoreTests
{
    private string _filePath = null!;
    private EmulatorConfigurationStore _store = null!;
    private CapturingLogger<EmulatorConfigurationStore> _logger = null!;

    [SetUp]
    public void Setup()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"emulators_test_{Guid.NewGuid()}.json");
        _logger = new CapturingLogger<EmulatorConfigurationStore>();
        _store = new EmulatorConfigurationStore(_filePath, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }

    [Test]
    public async Task LoadAsync_FileDoesNotExist_StartsEmptyWithDefaultInterval()
    {
        await _store.LoadAsync();

        _store.Nodes.Should().BeEmpty();
        _store.PublishIntervalMs.Should().Be(500);
    }

    [Test]
    public async Task AddAsync_PersistsToFile()
    {
        await _store.LoadAsync();
        await _store.AddAsync(new EmulatorNodeConfig { NodeId = "Press-01" });

        File.Exists(_filePath).Should().BeTrue();
        var store2 = new EmulatorConfigurationStore(_filePath);
        await store2.LoadAsync();
        store2.Nodes.Should().HaveCount(1);
        store2.Nodes[0].NodeId.Should().Be("Press-01");
    }

    [Test]
    public async Task LoadAsync_RoundTrip_PreservesAllProperties()
    {
        await _store.LoadAsync();
        var node = new EmulatorNodeConfig
        {
            Type = EmulatorNodeType.Generic,
            GroupId = "BoilerRoom",
            NodeId = "Press-07",
            PayloadFormat = GenericPayloadFormat.Hex,
            TopicTemplate = "{group}/{node}/{device}/{metric}",
            Devices =
            [
                new EmulatorDeviceConfig
                {
                    DeviceId = "FlowMeter-1",
                    Metrics =
                    [
                        new EmulatorMetricConfig
                        {
                            Name = "Flow Rate (kg/min)",
                            ValueType = MetricValueType.Int64,
                            Waveform = WaveformKind.RandomWalk,
                            Min = 10,
                            Max = 30,
                            PeriodSeconds = 45,
                            StepAmplitude = 2.5,
                            ConstantValue = 7,
                            BooleanValue = true,
                            TrueProbability = 0.75
                        }
                    ]
                }
            ]
        };
        await _store.AddAsync(node);

        var store2 = new EmulatorConfigurationStore(_filePath);
        await store2.LoadAsync();
        var loaded = store2.Nodes[0];
        loaded.Id.Should().Be(node.Id);
        loaded.Type.Should().Be(EmulatorNodeType.Generic);
        loaded.GroupId.Should().Be("BoilerRoom");
        loaded.NodeId.Should().Be("Press-07");
        loaded.PayloadFormat.Should().Be(GenericPayloadFormat.Hex);
        loaded.TopicTemplate.Should().Be("{group}/{node}/{device}/{metric}");
        loaded.Devices.Should().HaveCount(1);
        var device = loaded.Devices[0];
        device.Id.Should().Be(node.Devices[0].Id);
        device.DeviceId.Should().Be("FlowMeter-1");
        device.Metrics.Should().HaveCount(1);
        var metric = device.Metrics[0];
        metric.Id.Should().Be(node.Devices[0].Metrics[0].Id);
        metric.Name.Should().Be("Flow Rate (kg/min)");
        metric.ValueType.Should().Be(MetricValueType.Int64);
        metric.Waveform.Should().Be(WaveformKind.RandomWalk);
        metric.Min.Should().Be(10);
        metric.Max.Should().Be(30);
        metric.PeriodSeconds.Should().Be(45);
        metric.StepAmplitude.Should().Be(2.5);
        metric.ConstantValue.Should().Be(7);
        metric.BooleanValue.Should().BeTrue();
        metric.TrueProbability.Should().Be(0.75);
    }

    [Test]
    public async Task UpdateAsync_UpdatesExistingNode()
    {
        await _store.LoadAsync();
        var node = new EmulatorNodeConfig { NodeId = "Original" };
        await _store.AddAsync(node);

        node.NodeId = "Updated";
        await _store.UpdateAsync(node);

        var store2 = new EmulatorConfigurationStore(_filePath);
        await store2.LoadAsync();
        store2.Nodes.Should().HaveCount(1);
        store2.Nodes[0].NodeId.Should().Be("Updated");
    }

    [Test]
    public async Task RemoveAsync_RemovesNodeById()
    {
        await _store.LoadAsync();
        var node1 = new EmulatorNodeConfig { NodeId = "Node-1" };
        var node2 = new EmulatorNodeConfig { NodeId = "Node-2" };
        await _store.AddAsync(node1);
        await _store.AddAsync(node2);

        await _store.RemoveAsync(node1.Id);

        _store.Nodes.Should().HaveCount(1);
        _store.Nodes[0].NodeId.Should().Be("Node-2");
    }

    [Test]
    public async Task RemoveAllAsync_RemovesEveryNodeAndPersists()
    {
        await _store.LoadAsync();
        await _store.AddAsync(new EmulatorNodeConfig { NodeId = "Node-1" });
        await _store.AddAsync(new EmulatorNodeConfig { NodeId = "Node-2" });

        await _store.RemoveAllAsync();

        _store.Nodes.Should().BeEmpty();
        var store2 = new EmulatorConfigurationStore(_filePath);
        await store2.LoadAsync();
        store2.Nodes.Should().BeEmpty();
    }

    [Test]
    public async Task SetPublishIntervalAsync_PersistsInterval()
    {
        await _store.LoadAsync();
        await _store.SetPublishIntervalAsync(250);

        _store.PublishIntervalMs.Should().Be(250);
        var store2 = new EmulatorConfigurationStore(_filePath);
        await store2.LoadAsync();
        store2.PublishIntervalMs.Should().Be(250);
    }

    [Test]
    public async Task LoadAsync_CorruptFile_StartsEmptyAndLogsWarning()
    {
        await File.WriteAllTextAsync(_filePath, "not valid json {{{{");

        await _store.LoadAsync();

        _store.Nodes.Should().BeEmpty();
        _store.PublishIntervalMs.Should().Be(500);
        _logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Failed to load emulator configurations", StringComparison.Ordinal) &&
            entry.Exception != null);
    }

    [Test]
    public async Task LoadAsync_EmptyFile_StartsEmpty()
    {
        await File.WriteAllTextAsync(_filePath, "");
        await _store.LoadAsync();
        _store.Nodes.Should().BeEmpty();
        _store.PublishIntervalMs.Should().Be(500);
    }

    [Test]
    public async Task Nodes_ReturnsStableSnapshotAfterMutations()
    {
        await _store.LoadAsync();
        await _store.AddAsync(new EmulatorNodeConfig { NodeId = "Node-1" });
        var snapshot = _store.Nodes;

        await _store.AddAsync(new EmulatorNodeConfig { NodeId = "Node-2" });

        snapshot.Should().HaveCount(1);
        snapshot[0].NodeId.Should().Be("Node-1");
        _store.Nodes.Should().HaveCount(2);
    }

    [Test]
    public async Task AddAsync_FiresNodesChanged()
    {
        var fired = false;
        _store.NodesChanged += () => fired = true;

        await _store.AddAsync(new EmulatorNodeConfig());

        fired.Should().BeTrue();
    }

    [Test]
    public async Task UpdateAsync_FiresNodesChanged()
    {
        var node = new EmulatorNodeConfig();
        await _store.AddAsync(node);
        var fired = false;
        _store.NodesChanged += () => fired = true;

        await _store.UpdateAsync(node);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task RemoveAsync_FiresNodesChanged()
    {
        var node = new EmulatorNodeConfig();
        await _store.AddAsync(node);
        var fired = false;
        _store.NodesChanged += () => fired = true;

        await _store.RemoveAsync(node.Id);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task RemoveAllAsync_FiresNodesChanged()
    {
        await _store.AddAsync(new EmulatorNodeConfig());
        var fired = false;
        _store.NodesChanged += () => fired = true;

        await _store.RemoveAllAsync();

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetPublishIntervalAsync_FiresNodesChanged()
    {
        var fired = false;
        _store.NodesChanged += () => fired = true;

        await _store.SetPublishIntervalAsync(1000);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SaveAsync_WritesDocumentedSchema()
    {
        await _store.LoadAsync();
        await _store.AddAsync(new EmulatorNodeConfig());

        var json = await File.ReadAllTextAsync(_filePath);

        json.Should().Contain("\"version\"");
        json.Should().Contain("\"publishIntervalMs\"");
        json.Should().Contain("\"nodes\"");
        json.Should().Contain("\"SparkplugB\"");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
