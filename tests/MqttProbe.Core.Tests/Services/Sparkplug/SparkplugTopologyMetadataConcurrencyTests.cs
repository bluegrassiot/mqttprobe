using System.Collections;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugTopologyMetadataConcurrencyTests
{
    private static readonly TimeSpan _completionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _blockedProbeTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan _notificationProbeTimeout = TimeSpan.FromSeconds(1);

    private SparkplugMetricMetadataStore _store = null!;
    private SparkplugTopologyService _service = null!;

    [SetUp]
    public void Setup()
    {
        _store = new SparkplugMetricMetadataStore();
        _service = new SparkplugTopologyService(metricMetadataStore: _store);
    }

    [Test]
    public async Task NodeBirth_RacingRemoveNode_DoesNotResurrectMetadata()
    {
        await ApplyBirthRacingRemovalAsync(
            NodeBirth,
            () => _service.RemoveNode("g", "n"),
            assert: () =>
            {
                AssertNodeMetadataRemoved();
                _service.Groups.Should().NotContainKey("g");
            });
    }

    [Test]
    public async Task NodeBirth_RacingClearAll_DoesNotResurrectMetadata()
    {
        await ApplyBirthRacingRemovalAsync(
            NodeBirth,
            _service.ClearAll,
            assert: () =>
            {
                AssertNodeMetadataRemoved();
                _service.Groups.Should().BeEmpty();
            });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NodeBirth_RacingMatchingNodeRemoval_DoesNotResurrectMetadata(bool silent)
    {
        Action remove = silent
            ? () => _service.RemoveMatchingTopicSilent("spBv1.0/g/NBIRTH/n")
            : () => _service.RemoveMatchingTopic("spBv1.0/g/NBIRTH/n");

        await ApplyBirthRacingRemovalAsync(
            NodeBirth,
            remove,
            assert: () =>
            {
                AssertNodeMetadataRemoved();
                _service.Groups.Should().NotContainKey("g");
            });
    }

    [Test]
    public async Task DeviceBirth_RacingParentNodeRemoval_DoesNotResurrectMetadata()
    {
        await ApplyBirthRacingRemovalAsync(
            DeviceBirth,
            () => _service.RemoveNode("g", "n"),
            assert: () =>
            {
                AssertDeviceMetadataRemoved();
                _service.Groups.Should().NotContainKey("g");
            });
    }

    [Test]
    public async Task DeviceBirth_RacingClearAll_DoesNotResurrectMetadata()
    {
        await ApplyBirthRacingRemovalAsync(
            DeviceBirth,
            _service.ClearAll,
            assert: () =>
            {
                AssertDeviceMetadataRemoved();
                _service.Groups.Should().BeEmpty();
            });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeviceBirth_RacingMatchingDeviceRemoval_DoesNotResurrectMetadata(bool silent)
    {
        Action remove = silent
            ? () => _service.RemoveMatchingTopicSilent("spBv1.0/g/DDATA/n/d")
            : () => _service.RemoveMatchingTopic("spBv1.0/g/DDATA/n/d");

        await ApplyBirthRacingRemovalAsync(
            DeviceBirth,
            remove,
            assert: () =>
            {
                AssertDeviceMetadataRemoved();
                _service.Groups["g"].Nodes["n"].Devices.Should().NotContainKey("d");
            });
    }

    [Test]
    public async Task DeviceBirth_RacingRemoveOfflineNodes_DoesNotResurrectMetadata()
    {
        await ApplyBirthRacingRemovalAsync(
            DeviceBirth,
            () => _service.RemoveOfflineNodes(),
            arrange: ApplyNodeDeathAsync,
            assert: () =>
            {
                AssertDeviceMetadataRemoved();
                _service.Groups.Should().NotContainKey("g");
            });
    }

    [Test]
    public async Task NodeBirth_AfterRemoval_FreshBirthRestoresAliasInheritanceAndReplacesIdentity()
    {
        await _service.ApplyTopologyEventsAsync([NodeBirth(DeltaMetrics("int8"))]);
        var original = _service.Groups["g"].Nodes["n"];

        _service.RemoveNode("g", "n").Should().BeTrue();
        await _service.ApplyTopologyEventsAsync([NodeBirth(DeltaMetrics("uint8"))]);

        var replacement = _service.Groups["g"].Nodes["n"];
        replacement.Should().NotBeSameAs(original);
        AssertAliasInheritance("spBv1.0/g/NDATA/n", "uint8", "206");

        await _service.ApplyTopologyEventsAsync([NodeBirth(DeltaMetrics("int8"))]);
        _service.Groups["g"].Nodes["n"].Should().BeSameAs(replacement);
    }

    [Test]
    public async Task DeviceBirth_AfterParentRemoval_FreshBirthRestoresAliasInheritance()
    {
        await _service.ApplyTopologyEventsAsync([DeviceBirth(DeltaMetrics("int8"))]);

        _service.RemoveNode("g", "n").Should().BeTrue();
        await _service.ApplyTopologyEventsAsync([DeviceBirth(DeltaMetrics("uint8"))]);

        AssertAliasInheritance("spBv1.0/g/DDATA/n/d", "uint8", "206");
    }

    [Test]
    public async Task BirthNotification_IsRaisedOutsideTheLifecycleGate()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var birth = NodeBirth(new BlockingMetrics(DeltaMetrics(), entered, release));
        var birthTask = Task.Run(() => _service.ApplyTopologyEventsAsync([birth]));

        var fired = 0;
        var removalObserved = false;
        _service.TopologyChanged += () =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0)
                return;

            var removal = Task.Run(() => _service.RemoveNode("g", "n"));
            removalObserved = removal.Wait(_notificationProbeTimeout);
        };

        try
        {
            entered.Wait(_completionTimeout).Should().BeTrue("the birth did not reach metadata publication");
            release.Set();
            await birthTask.WaitAsync(_completionTimeout);
        }
        finally
        {
            release.Set();
            await birthTask.WaitAsync(_completionTimeout);
        }

        removalObserved.Should().BeTrue(
            "notifications must not run while the lifecycle gate is held");
        _service.Groups.Should().NotContainKey("g");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MatchingPredicate_RunsOutsideTheGate_AndCanRemoveAnotherNode(bool silent)
    {
        await _service.ApplyTopologyEventsAsync([NodeBirthFor("a")]);
        await _service.ApplyTopologyEventsAsync([NodeBirthFor("b")]);

        var nestedRemoval = 0;

        var removed = await Task.Run(() =>
        {
            bool Predicate(string topic)
            {
                if (Interlocked.Exchange(ref nestedRemoval, 1) == 0)
                    _service.RemoveNode("g", "b").Should().BeTrue();

                return topic == "spBv1.0/g/NBIRTH/a";
            }

            return silent
                ? _service.RemoveMatchingTopicsSilent(Predicate)
                : _service.RemoveMatchingTopics(Predicate);
        }).WaitAsync(_completionTimeout);

        removed.Should().BeGreaterThan(0);
        nestedRemoval.Should().Be(1);
        _service.Groups.Should().NotContainKey("g");
    }

    private async Task ApplyBirthRacingRemovalAsync(
        Func<IReadOnlyList<MetricSnapshot>, TopologyEvent> birthFactory,
        Action remove,
        Func<Task>? arrange = null,
        Action? assert = null)
    {
        if (arrange is not null)
            await arrange();

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var birth = birthFactory(new BlockingMetrics(DeltaMetrics(), entered, release));
        var birthTask = Task.Run(() => _service.ApplyTopologyEventsAsync([birth]));
        Task? removalTask = null;

        try
        {
            entered.Wait(_completionTimeout).Should().BeTrue("the birth did not reach metadata publication");
            removalTask = Task.Run(remove);
            await AssertStillBlockedAsync(removalTask);
        }
        finally
        {
            release.Set();
            await birthTask.WaitAsync(_completionTimeout);
            if (removalTask is not null)
                await removalTask.WaitAsync(_completionTimeout);
        }

        assert?.Invoke();
    }

    private static async Task AssertStillBlockedAsync(Task removalTask)
    {
        var winner = await Task.WhenAny(removalTask, Task.Delay(_blockedProbeTimeout));
        winner.Should().NotBeSameAs(removalTask,
            "a removal must wait for an in-flight birth transaction");
    }

    private Task ApplyNodeDeathAsync() =>
        _service.ApplyTopologyEventsAsync(
        [
            new NodeDeathEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NDEATH/n",
                GroupId = "g",
                NodeId = "n"
            }
        ]);

    private void AssertAliasInheritance(string topic, string dataType, string value)
    {
        var payload = new Payload();
        payload.Metrics.Add(new Payload.Types.Metric { Alias = 42, IntValue = 206 });

        var events = new SparkplugTopologyExtractor().Extract(
            DecodedPayloadEnvelope.CreateSuccess("sparkplug-b", topic, [], payload.ToString(), typedPayload: payload),
            _store);

        var evt = events.Should().ContainSingle().Subject;
        var metrics = evt switch
        {
            NodeDataEvent nodeData => nodeData.Metrics,
            DeviceDataEvent deviceData => deviceData.Metrics,
            _ => throw new AssertionException($"Unexpected event type {evt.GetType().Name}.")
        };

        var metric = metrics.Should().ContainSingle().Subject;
        metric.Name.Should().Be("Delta");
        metric.DataType.Should().Be(dataType);
        metric.Value.Should().Be(value);
    }

    private void AssertNodeMetadataRemoved()
    {
        _store.ResolveName("g", "n", null, 42).Should().BeNull();
        _store.ResolveDatatype("g", "n", null, 42, "Delta").Should().BeNull();
    }

    private void AssertDeviceMetadataRemoved()
    {
        _store.ResolveName("g", "n", "d", 42).Should().BeNull();
        _store.ResolveDatatype("g", "n", "d", 42, "Delta").Should().BeNull();
    }

    private static TopologyEvent NodeBirth(IReadOnlyList<MetricSnapshot> metrics) =>
        CreateNodeBirth("n", metrics);

    private static TopologyEvent NodeBirthFor(string nodeId) =>
        CreateNodeBirth(nodeId, DeltaMetrics());

    private static TopologyEvent CreateNodeBirth(string nodeId, IReadOnlyList<MetricSnapshot> metrics) =>
        new NodeBirthEvent
        {
            FormatId = "sparkplug-b",
            Topic = $"spBv1.0/g/NBIRTH/{nodeId}",
            SequenceNumber = 1,
            GroupId = "g",
            NodeId = nodeId,
            Metrics = metrics
        };

    private static TopologyEvent DeviceBirth(IReadOnlyList<MetricSnapshot> metrics) =>
        new DeviceBirthEvent
        {
            FormatId = "sparkplug-b",
            Topic = "spBv1.0/g/DBIRTH/n/d",
            SequenceNumber = 1,
            GroupId = "g",
            NodeId = "n",
            DeviceId = "d",
            Metrics = metrics
        };

    private static IReadOnlyList<MetricSnapshot> DeltaMetrics(string dataType = "int8") =>
    [
        new MetricSnapshot { Name = "Delta", DataType = dataType, Value = "5", Alias = 42 }
    ];

    private sealed class BlockingMetrics(
        IReadOnlyList<MetricSnapshot> inner,
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IReadOnlyList<MetricSnapshot>
    {
        public MetricSnapshot this[int index] => inner[index];

        public int Count => inner.Count;

        public IEnumerator<MetricSnapshot> GetEnumerator()
        {
            entered.Set();
            release.Wait(_completionTimeout);
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
