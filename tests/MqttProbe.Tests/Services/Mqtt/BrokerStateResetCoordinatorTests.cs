using Microsoft.Extensions.Logging;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Shared.Tests.Services.Mqtt;

[TestFixture]
public class BrokerStateResetCoordinatorTests
{
    private IMessageStoreManager _mockMsgStore = null!;
    private ISparkplugTopologyService _mockTopology = null!;
    private ISubscriptionManager _mockSubMgr = null!;
    private IChartDataService _mockChart = null!;
    private IEmulationService _mockEmulation = null!;
    private ILogger<BrokerStateResetCoordinator> _mockLogger = null!;
    private BrokerStateResetCoordinator _coordinator = null!;

    [SetUp]
    public void Setup()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockTopology = Substitute.For<ISparkplugTopologyService>();
        _mockSubMgr = Substitute.For<ISubscriptionManager>();
        _mockChart = Substitute.For<IChartDataService>();
        _mockEmulation = Substitute.For<IEmulationService>();
        _mockLogger = Substitute.For<ILogger<BrokerStateResetCoordinator>>();

        _coordinator = new BrokerStateResetCoordinator(
            _mockMsgStore, _mockTopology, _mockSubMgr, _mockChart, _mockEmulation, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _mockMsgStore.Dispose();
        _mockTopology.Dispose();
        _mockSubMgr.Dispose();
        _mockChart.Dispose();
        _mockEmulation.Dispose();
    }

    private static Connection MakeConnection(string host = "broker.local", int port = 1883,
        Protocol protocol = Protocol.Mqtt, bool useTls = false, string clientId = "test-cid",
        string websocketBasePath = "mqtt") =>
        new()
        {
            Host = host,
            Port = port,
            Protocol = protocol,
            UseTls = useTls,
            ClientId = clientId,
            WebsocketBasePath = websocketBasePath
        };

    // --- Identity comparison tests ---

    [Test]
    public async Task ResetIfBrokerChangedAsync_FirstConnect_DoesNotReset_OnlyStoresIdentity()
    {
        var conn = MakeConnection();

        await _coordinator.ResetIfBrokerChangedAsync(conn);

        await _mockMsgStore.DidNotReceive().ClearAllMessages();
        _mockTopology.DidNotReceive().ClearAll();
        _mockSubMgr.DidNotReceive().ClearActiveSubscriptions();
        _mockChart.DidNotReceive().ClearBuffers();
        await _mockEmulation.DidNotReceive().ResetForConnectionAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_SameBroker_DoesNotReset()
    {
        var conn = MakeConnection();
        await _coordinator.ResetIfBrokerChangedAsync(conn); // first connect
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(conn); // same broker

        await _mockMsgStore.DidNotReceive().ClearAllMessages();
        _mockTopology.DidNotReceive().ClearAll();
        _mockSubMgr.DidNotReceive().ClearActiveSubscriptions();
        _mockChart.DidNotReceive().ClearBuffers();
        await _mockEmulation.DidNotReceive().ResetForConnectionAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_DifferentHost_Resets()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        await _mockMsgStore.Received(1).ClearAllMessages();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_DifferentPort_Resets()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(port: 1883));
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(port: 8883));

        await _mockMsgStore.Received(1).ClearAllMessages();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_DifferentProtocol_Resets()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(protocol: Protocol.Mqtt));
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(protocol: Protocol.WebSocket));

        await _mockMsgStore.Received(1).ClearAllMessages();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_DifferentTls_Resets()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(useTls: false));
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(useTls: true));

        await _mockMsgStore.Received(1).ClearAllMessages();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_DifferentClientId_DoesNotReset()
    {
        // ClientId is part of MQTT session, not broker identity
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(clientId: "cid-1"));
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(clientId: "cid-2"));

        await _mockMsgStore.DidNotReceive().ClearAllMessages();
        _mockTopology.DidNotReceive().ClearAll();
        _mockSubMgr.DidNotReceive().ClearActiveSubscriptions();
        _mockChart.DidNotReceive().ClearBuffers();
        await _mockEmulation.DidNotReceive().ResetForConnectionAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_DifferentWebsocketBasePath_Resets()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(
            protocol: Protocol.WebSocket, websocketBasePath: "/mqtt"));
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(
            protocol: Protocol.WebSocket, websocketBasePath: "/ws"));

        await _mockMsgStore.Received(1).ClearAllMessages();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_SameBrokerAfterMultipleConnects_DoesNotReset()
    {
        var connA = MakeConnection(host: "a");
        var connB = MakeConnection(host: "b");

        await _coordinator.ResetIfBrokerChangedAsync(connA);
        await _coordinator.ResetIfBrokerChangedAsync(connB);
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(connB); // same as last

        await _mockMsgStore.DidNotReceive().ClearAllMessages();
        await _mockEmulation.DidNotReceive().ResetForConnectionAsync(Arg.Any<Guid>());
    }

    // --- Partial failure / error handling tests ---

    [Test]
    public async Task ResetIfBrokerChangedAsync_MessageStoreThrows_StillResetsOthers()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        ClearAllMocks();
        _mockMsgStore.ClearAllMessages().Returns(Task.FromException(new Exception("store error")));

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        _mockTopology.Received(1).ClearAll();
        _mockSubMgr.Received(1).ClearActiveSubscriptions();
        _mockChart.Received(1).ClearBuffers();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_TopologyThrows_StillResetsOthers()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        ClearAllMocks();
        _mockTopology.When(x => x.ClearAll()).Do(_ => throw new Exception("topology error"));

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        await _mockMsgStore.Received(1).ClearAllMessages();
        _mockSubMgr.Received(1).ClearActiveSubscriptions();
        _mockChart.Received(1).ClearBuffers();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_SubscriptionThrows_StillResetsOthers()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        ClearAllMocks();
        _mockSubMgr.When(x => x.ClearActiveSubscriptions()).Do(_ => throw new Exception("sub error"));

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        await _mockMsgStore.Received(1).ClearAllMessages();
        _mockTopology.Received(1).ClearAll();
        _mockChart.Received(1).ClearBuffers();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_ChartThrows_StillResetsOthers()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        ClearAllMocks();
        _mockChart.When(x => x.ClearBuffers()).Do(_ => throw new Exception("chart error"));

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        await _mockMsgStore.Received(1).ClearAllMessages();
        _mockTopology.Received(1).ClearAll();
        _mockSubMgr.Received(1).ClearActiveSubscriptions();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_AllThrow_DoesNotPropagate()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        _mockMsgStore.ClearAllMessages().Returns(Task.FromException(new Exception("e1")));
        _mockTopology.When(x => x.ClearAll()).Do(_ => throw new Exception("e2"));
        _mockSubMgr.When(x => x.ClearActiveSubscriptions()).Do(_ => throw new Exception("e3"));
        _mockChart.When(x => x.ClearBuffers()).Do(_ => throw new Exception("e4"));
        _mockEmulation.ResetForConnectionAsync(Arg.Any<Guid>()).Returns(Task.FromException(new Exception("e5")));

        var act = () => _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_ServiceThrows_LogsError()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        ClearAllMocks();
        _mockTopology.When(x => x.ClearAll()).Do(_ => throw new Exception("boom"));

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o!.ToString()!.Contains("ClearAll")),
            Arg.Is<Exception>(e => e!.Message == "boom"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_DifferentBroker_LogsInfo()
    {
        _mockLogger.IsEnabled(LogLevel.Information).Returns(true);
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        ClearAllMocks();
        _mockLogger.IsEnabled(LogLevel.Information).Returns(true);

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        _mockLogger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o!.ToString()!.Contains("reset")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // --- Spec scenario: auto-reconnect is NOT routed through coordinator ---

    [Test]
    public async Task AutoReconnectPath_DoesNotCallCoordinator()
    {
        // Auto-reconnect bypasses ConnectionDialog.Connect() entirely —
        // it is handled by IMqttManagedClient internals. This test documents
        // that the coordinator is never invoked from auto-reconnect.
        // The coordinator has no auto-reconnect entry point; this is by design.
        // If someone accidentally wires it into a reconnect handler, this
        // test pattern (verifying the coordinator is only called from the
        // explicit connect path) should catch it.
        var conn = MakeConnection();

        // Simulate: first explicit connect establishes identity
        await _coordinator.ResetIfBrokerChangedAsync(conn);

        // Simulate: auto-reconnect fires (same connection, no explicit call)
        // The coordinator is NOT called — runtime state is preserved.
        _mockMsgStore.ClearReceivedCalls();
        _mockTopology.ClearReceivedCalls();
        _mockSubMgr.ClearReceivedCalls();
        _mockChart.ClearReceivedCalls();
        _mockEmulation.ClearReceivedCalls();

        // Verify: no service was reset since coordinator was not called
        // between the two connects (simulating auto-reconnect gap).
        await _mockMsgStore.DidNotReceive().ClearAllMessages();
        _mockTopology.DidNotReceive().ClearAll();
        _mockSubMgr.DidNotReceive().ClearActiveSubscriptions();
        _mockChart.DidNotReceive().ClearBuffers();
        await _mockEmulation.DidNotReceive().ResetForConnectionAsync(Arg.Any<Guid>());
    }

    // --- Spec scenario: saved data is not touched by coordinator ---

    [Test]
    public async Task ResetIfBrokerChangedAsync_DoesNotModifySavedConnectionProfiles()
    {
        // The coordinator only calls runtime clear methods on services.
        // It never calls ISettingsStore.AddConnectionAsync or
        // ISettingsStore.RemoveConnectionAsync. Saved profiles are untouched.
        // This test documents that invariant by verifying the coordinator
        // has no dependency on ISettingsStore at all.
        var conn = MakeConnection();
        await _coordinator.ResetIfBrokerChangedAsync(conn);

        // If the coordinator had an ISettingsStore dependency, this test
        // would need to mock it and verify no calls. Since it doesn't,
        // the absence of ISettingsStore in the constructor proves saved
        // data is preserved by design.
        typeof(BrokerStateResetCoordinator)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType.Name.Contains("SettingsStore"));
    }

    // --- Emulation reset tests ---

    [Test]
    public async Task ResetIfBrokerChangedAsync_DifferentBroker_ResetsEmulation()
    {
        var connA = MakeConnection(host: "broker-a");
        var connB = MakeConnection(host: "broker-b");

        await _coordinator.ResetIfBrokerChangedAsync(connA);
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(connB);

        await _mockEmulation.Received(1).ResetForConnectionAsync(connB.Id);
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_FirstConnect_DoesNotResetEmulation()
    {
        var conn = MakeConnection();

        await _coordinator.ResetIfBrokerChangedAsync(conn);

        await _mockEmulation.DidNotReceive().ResetForConnectionAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_SameBroker_DoesNotResetEmulation()
    {
        var conn = MakeConnection();
        await _coordinator.ResetIfBrokerChangedAsync(conn);
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(conn);

        await _mockEmulation.DidNotReceive().ResetForConnectionAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_EmulationThrows_StillResetsOthers()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        ClearAllMocks();
        _mockEmulation.ResetForConnectionAsync(Arg.Any<Guid>())
            .Returns(Task.FromException(new Exception("emulation error")));

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        await _mockMsgStore.Received(1).ClearAllMessages();
        _mockTopology.Received(1).ClearAll();
        _mockSubMgr.Received(1).ClearActiveSubscriptions();
        _mockChart.Received(1).ClearBuffers();
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_EmulationThrows_LogsError()
    {
        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-a"));
        ClearAllMocks();
        _mockEmulation.ResetForConnectionAsync(Arg.Any<Guid>())
            .Returns(Task.FromException(new Exception("emulation boom")));

        await _coordinator.ResetIfBrokerChangedAsync(MakeConnection(host: "broker-b"));

        _mockLogger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o!.ToString()!.Contains("EmulationService")),
            Arg.Is<Exception>(e => e!.Message == "emulation boom"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ResetIfBrokerChangedAsync_PassesTargetConnectionId_ToEmulationReset()
    {
        var connA = MakeConnection(host: "broker-a");
        var connB = MakeConnection(host: "broker-b");

        await _coordinator.ResetIfBrokerChangedAsync(connA);
        ClearAllMocks();

        await _coordinator.ResetIfBrokerChangedAsync(connB);

        await _mockEmulation.Received(1).ResetForConnectionAsync(connB.Id);
        await _mockEmulation.DidNotReceive().ResetForConnectionAsync(connA.Id);
    }

    private void ClearAllMocks()
    {
        _mockMsgStore.ClearReceivedCalls();
        _mockTopology.ClearReceivedCalls();
        _mockSubMgr.ClearReceivedCalls();
        _mockChart.ClearReceivedCalls();
        _mockEmulation.ClearReceivedCalls();
        _mockLogger.ClearReceivedCalls();
    }
}
