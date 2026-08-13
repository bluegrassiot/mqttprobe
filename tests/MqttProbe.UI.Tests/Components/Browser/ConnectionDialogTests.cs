using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.TestInfrastructure.Security;
using MqttProbe.UI.Tests.TestHelpers;
// TestHelpers moved to Core.Tests
using MudBlazor;
using MudBlazor.Extensions;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class ConnectionDialogTests : BunitTestContext
{
    private IMqttManagedClient _mockClient = null!;
    private IConnectionSettings _mockConnections = null!;
    private IUiSettings _mockUi = null!;
    private IMessageStoreManager _mockMsgStore = null!;
    private ISubscriptionManager _mockSubMgr = null!;
    private ISessionState _mockSessionState = null!;
    private IMqttOptionsBuilder _mockOptionsBuilder = null!;
    private IBrokerStateResetCoordinator _mockCoordinator = null!;
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;
    private ICertificateAssetStore _mockCertStore = null!;
    private ICertificateFilePicker _mockFilePicker = null!;
    private ICertificateInputCapability _mockInputCapability = null!;
    private IChartDataService _mockChart = null!;

    private Func<MqttClientConnectedEventArgs, Task>? _connectedHandler;
    private Func<MqttConnectingFailedEventArgs, Task>? _failedHandler;

    [SetUp]
    public void SetupMocks()
    {
        _mockClient = Substitute.For<IMqttManagedClient>();
        _mockConnections = Substitute.For<IConnectionSettings>();
        _mockUi = Substitute.For<IUiSettings>();
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockSubMgr = Substitute.For<ISubscriptionManager>();
        _mockSessionState = Substitute.For<ISessionState>();
        _mockOptionsBuilder = Substitute.For<IMqttOptionsBuilder>();
        _mockCoordinator = Substitute.For<IBrokerStateResetCoordinator>();
        _mockCertStore = Substitute.For<ICertificateAssetStore>();
        _mockFilePicker = Substitute.For<ICertificateFilePicker>();
        _mockInputCapability = Substitute.For<ICertificateInputCapability>();
        _mockInputCapability.UsesInputFileComponent.Returns(false);
        _mockChart = Substitute.For<IChartDataService>();
        _mockChart.StartAsync().Returns(Task.CompletedTask);

        var cfg = new AppConfiguration();
        _mockConnections.Connections.Returns(cfg.Connections);
        _mockUi.Ui.Returns(cfg.Ui);
        _mockMsgStore.Start().Returns(Task.CompletedTask);
        _mockOptionsBuilder.Build(Arg.Any<Connection>()).Returns(
            new MqttManagedClientOptions
            {
                ClientOptions = new MqttClientOptionsBuilder().WithTcpServer("localhost").Build()
            });

        _connectedHandler = null;
        _failedHandler = null;
        _mockClient
            .When(x => x.ConnectedAsync += Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(x => _connectedHandler = x.Arg<Func<MqttClientConnectedEventArgs, Task>>());
        _mockClient
            .When(x => x.ConnectingFailedAsync += Arg.Any<Func<MqttConnectingFailedEventArgs, Task>>())
            .Do(x => _failedHandler = x.Arg<Func<MqttConnectingFailedEventArgs, Task>>());

        Services.AddSingleton(_mockClient);
        Services.AddConnectionSettings(_mockConnections);
        Services.AddUiSettings(_mockUi);
        Services.AddSingleton(_mockMsgStore);
        Services.AddSingleton(_mockSubMgr);
        Services.AddSingleton(_mockSessionState);
        Services.AddSingleton(_mockOptionsBuilder);
        Services.AddSingleton(_mockCoordinator);
        Services.AddSingleton(Substitute.For<ILogger<ConnectionDialog>>());
        Services.AddSingleton(_mockChart);
        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        Services.AddSingleton(Substitute.For<IConnectionSessionLifecycle>());
        Services.AddSingleton(_mockCertStore);
        Services.AddSingleton(_mockFilePicker);
        Services.AddSingleton(_mockInputCapability);

        EnsureMudProviders();

        // MudDialog content renders via MudDialogProvider, so assertions should target its DOM.
        _dialogProvider = Render<MudDialogProvider>();
    }

    [TearDown]
    public void TeardownMocks() => _mockClient.Dispose();

    private async Task OpenDialog(AppConfiguration? config = null)
    {
        var cfg = config ?? new AppConfiguration();
        _mockConnections.Connections.Returns(cfg.Connections);
        _mockUi.Ui.Returns(cfg.Ui);
        var dialogService = Services.GetRequiredService<IDialogService>();
        await _dialogProvider.InvokeAsync(async () =>
            await dialogService.ShowAsync<ConnectionDialog>("Connection Setup"));
    }

    private Task SelectConnection(Connection conn) =>
        _dialogProvider.InvokeAsync(async () =>
            await _dialogProvider.FindComponent<ConnectionDialog>().Instance.ConnectionChanged(conn));

    [Test]
    public async Task Renders_WithSavedConnectionList_WhenConnectionsExist()
    {
        var cfg = new AppConfiguration
        {
            Connections =
            [
                new Connection { Name = "Home MQTT", Host = "home.local", Port = 1883 },
                new Connection { Name = "Cloud Broker", Host = "cloud.io", Port = 8883 }
            ]
        };

        await OpenDialog(cfg);

        var items = _dialogProvider.FindComponents<MudSelectItem<Connection?>>();
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Instance.Value != null && i.Instance.Value.Name == "Home MQTT");
        items.Should().Contain(i => i.Instance.Value != null && i.Instance.Value.Name == "Cloud Broker");
    }

    [Test]
    public async Task Renders_EmptyForm_WhenNoConnectionsExist()
    {
        await OpenDialog(new AppConfiguration());

        _dialogProvider.FindComponents<MudSelectItem<Connection?>>().Should().BeEmpty();
        _dialogProvider.Find("button[title='Delete connection']").GetAttribute("disabled").Should().NotBeNull();
    }

    [Test]
    public async Task AddButton_ResetsFormToBlankConnection()
    {
        var cfg = new AppConfiguration { Connections = [new Connection { Name = "Existing", Host = "host", Port = 1883 }] };
        await OpenDialog(cfg);

        _dialogProvider.Find("button[title='New connection']").Click();

        var deleteBtn = _dialogProvider.Find("button[title='Delete connection']");
        deleteBtn.GetAttribute("disabled").Should().NotBeNull();
    }

    [Test]
    public async Task SaveButton_Enabled_WhenCertificateStaged_OnUnchangedConnection()
    {
        var conn = new Connection { Name = "Cert Conn", Host = "localhost", Port = 8883, UseTls = true };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        // An unchanged saved connection is not dirty, so Save is disabled.
        _dialogProvider.Find("button[title='Save connection']")
            .GetAttribute("disabled").Should().NotBeNull("an unchanged saved connection is not dirty");

        // Staging a certificate (as the file picker does) must mark the dialog dirty even
        // though the Connection model itself is unchanged.
        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        await _dialogProvider.InvokeAsync(() =>
            dialog.SetStagedCertificate([1, 2, 3], null, null, ""));

        _dialogProvider.Find("button[title='Save connection']")
            .GetAttribute("disabled").Should().BeNull("staging a certificate should enable Save");
    }

    [Test]
    public async Task DeleteButton_IsDisabled_WhenNoConnectionSelected()
    {
        await OpenDialog();

        var deleteBtn = _dialogProvider.Find("button[title='Delete connection']");
        deleteBtn.GetAttribute("disabled").Should().NotBeNull();
    }

    [Test]
    public async Task ConnectButton_CallsStartAsync_OnManagedClient()
    {
        _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockClient.Received(1).StartAsync(Arg.Any<MqttManagedClientOptions>());
    }

    [Test]
    public async Task ConnectButton_ShowsSpinner_WhileConnecting()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Connect']").Click();

        _dialogProvider.Markup.Should().Contain("Connecting...");
    }

    [Test]
    public async Task ConnectedAsync_Event_ClosesDialog()
    {
        await OpenDialog();
        _connectedHandler.Should().NotBeNull("component should subscribe to ConnectedAsync in OnInitialized");

        await _dialogProvider.InvokeAsync(() => _connectedHandler!(null!));

        _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty();
    }

    [Test]
    public async Task WebSocketProtocol_ShowsBasePathField()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "WS Broker", Host = "ws.example.com", Port = 8083, Protocol = Protocol.WebSocket }]
        };
        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Markup.Should().Contain("WebSocket Base Path");
    }

    [Test]
    public async Task TlsEnabled_ShowsUntrustedCertificateOption()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TLS Broker", Host = "tls.example.com", Port = 8883, UseTls = true }]
        };
        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Markup.Should().Contain("Allow untrusted certificate");
    }

    [Test]
    public async Task Connect_DoesNotCallSubscriptionManagerAdd()
    {
        _mockSubMgr.Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>()).Returns(Task.CompletedTask);
        _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockSubMgr.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>());
    }

    [Test]
    public async Task ConnectedAsync_DoesNotCallSubscriptionManagerAddOrRemove()
    {
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>());
        _mockSubMgr.Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>()).Returns(Task.CompletedTask);
        _mockSubMgr.Remove(Arg.Any<List<string>>()).Returns(Task.CompletedTask);
        _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>()).Returns(Task.CompletedTask);

        var cfg = new AppConfiguration
        {
            Connections =
            [
                new Connection
                {
                    Name = "TestConn",
                    Host = "localhost",
                    Port = 1883,
                    SubscribedTopics = [new() { Topic = "spBv1.0/#" }]
                }
            ]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Connect']").Click();

        _connectedHandler.Should().NotBeNull();
        await _dialogProvider.InvokeAsync(() => _connectedHandler!(null!));

        await _mockSubMgr.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>());
        await _mockSubMgr.DidNotReceive().Remove(Arg.Any<List<string>>());
    }

    [Test]
    public async Task OnConnectTab_RendersSavedSubscriptions()
    {
        var cfg = new AppConfiguration
        {
            Connections =
            [
                new Connection
                {
                    Name = "TestConn",
                    Host = "localhost",
                    Port = 1883,
                    SubscribedTopics =
                    [
                        new() { Topic = "spBv1.0/#", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce },
                        new() { Topic = "sensors/#", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce }
                    ]
                }
            ]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        GoToOnConnectTab();

        _dialogProvider.Markup.Should().Contain("spBv1.0/#");
        _dialogProvider.Markup.Should().Contain("sensors/#");
        _dialogProvider.Markup.Should().Contain("AtMostOnce");
    }

    [Test]
    public async Task OnConnectTab_Add_PersistsTopicWithDefaultQos()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>());
        _mockSubMgr.Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>()).Returns(Task.CompletedTask);
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        GoToOnConnectTab();

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>().Instance;
        editor.TopicDraft = "factory/#";
        await _dialogProvider.InvokeAsync(() => editor.AddForTests());

        await _mockConnections.Received().AddConnectionAsync(
            Arg.Is<Connection>(c =>
                c!.SubscribedTopics.Any(s =>
                    s.Topic == "factory/#" &&
                    s.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce)));
    }

    [Test]
    public async Task OnConnectTab_Add_WhenNotConnected_DoesNotCallSubscriptionManager()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>());
        _mockSubMgr.Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>()).Returns(Task.CompletedTask);
        // Default: _mockClient.IsConnected is false
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        GoToOnConnectTab();

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>().Instance;
        editor.TopicDraft = "factory/#";
        await _dialogProvider.InvokeAsync(() => editor.AddForTests());

        await _mockSubMgr.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>());
    }

    [Test]
    public async Task OnConnectTab_Add_WhenConnectedToActiveConnection_CallsSubscriptionManagerAdd()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>());
        _mockSubMgr.Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>()).Returns(Task.CompletedTask);
        _mockClient.IsConnected.Returns(true);
        var activeId = Guid.NewGuid();
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883, Id = activeId };
        var activeConn = new Connection { Name = "Active", Host = "localhost", Port = 1883, Id = activeId };
        _mockSessionState.SelectedConnection.Returns(activeConn);
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        GoToOnConnectTab();

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>().Instance;
        editor.TopicDraft = "factory/#";
        await _dialogProvider.InvokeAsync(() => editor.AddForTests());

        await _mockSubMgr.Received(1).Add("factory/#");
        await _mockConnections.Received().AddConnectionAsync(
            Arg.Is<Connection>(c =>
                c!.SubscribedTopics.Any(s =>
                    s.Topic == "factory/#" &&
                    s.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce)));
    }

    [Test]
    public async Task OnConnectTab_Add_WhenConnectedToDifferentConnection_DoesNotCallSubscriptionManager()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>());
        _mockSubMgr.Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>()).Returns(Task.CompletedTask);
        _mockClient.IsConnected.Returns(true);
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        var activeConn = new Connection { Name = "Active", Host = "localhost", Port = 1883 };
        _mockSessionState.SelectedConnection.Returns(activeConn);
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        GoToOnConnectTab();

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>().Instance;
        editor.TopicDraft = "factory/#";
        await _dialogProvider.InvokeAsync(() => editor.AddForTests());

        await _mockSubMgr.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>());
    }

    [Test]
    public async Task OnConnectTab_AddDuplicate_DoesNotPersistSecondEntry()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        var conn = new Connection
        {
            Name = "TestConn",
            Host = "localhost",
            Port = 1883,
            SubscribedTopics = [new() { Topic = "dup/#" }]
        };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        GoToOnConnectTab();

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>().Instance;
        editor.TopicDraft = "dup/#";
        await _dialogProvider.InvokeAsync(() => editor.AddForTests());

        conn.SubscribedTopics.Count(s => s.Topic == "dup/#").Should().Be(1);
        await _mockConnections.DidNotReceive().AddConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public async Task OnConnectTab_Remove_PersistsWithoutTopic()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>());
        _mockSubMgr.Remove(Arg.Any<IReadOnlyList<string>>()).Returns(Task.CompletedTask);
        var conn = new Connection
        {
            Name = "TestConn",
            Host = "localhost",
            Port = 1883,
            SubscribedTopics =
            [
                new() { Topic = "keep/#" },
                new() { Topic = "drop/#" }
            ]
        };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        GoToOnConnectTab();

        // Select the "drop/#" row checkbox and click Remove
        var editor = _dialogProvider.FindComponent<SubscriptionEditor>();
        var checkboxes = editor.FindAll("input[type='checkbox']");
        // [0] = header select-all, [1] = keep/#, [2] = drop/#
        checkboxes.Should().HaveCount(3);
        checkboxes[2].Change(true);
        _dialogProvider.Find("button[title='Remove']").Click();

        await _mockConnections.Received().AddConnectionAsync(
            Arg.Is<Connection>(c =>
                c!.SubscribedTopics.All(s => s.Topic != "drop/#") &&
                c.SubscribedTopics.Any(s => s.Topic == "keep/#")));
    }

    [Test]
    public async Task OnConnectTab_Remove_WhenConnectedToActiveConnection_CallsSubscriptionManagerRemove()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        var activeId = Guid.NewGuid();
        var activeConn = new Connection { Name = "Active", Host = "localhost", Port = 1883, Id = activeId };
        _mockSessionState.SelectedConnection.Returns(activeConn);
        _mockClient.IsConnected.Returns(true);
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "keep/#" },
            new() { Topic = "drop/#" }
        });
        _mockSubMgr.Remove(Arg.Any<IReadOnlyList<string>>()).Returns(Task.CompletedTask);
        var conn = new Connection
        {
            Name = "TestConn",
            Host = "localhost",
            Port = 1883,
            Id = activeId,
            SubscribedTopics =
            [
                new() { Topic = "keep/#" },
                new() { Topic = "drop/#" }
            ]
        };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        GoToOnConnectTab();

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>();
        var checkboxes = editor.FindAll("input[type='checkbox']");
        checkboxes[2].Change(true); // select drop/#
        _dialogProvider.Find("button[title='Remove']").Click();

        await _mockSubMgr.Received(1).Remove(
            Arg.Is<IReadOnlyList<string>>(t => t.Contains("drop/#") && t.Count == 1));
    }

    [Test]
    public async Task OnConnectTab_Remove_WhenLiveSubscriptionsEmpty_StillCallsSubscriptionManagerRemove()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        var activeId = Guid.NewGuid();
        var activeConn = new Connection { Name = "Active", Host = "localhost", Port = 1883, Id = activeId };
        _mockSessionState.SelectedConnection.Returns(activeConn);
        _mockClient.IsConnected.Returns(true);
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>());
        _mockSubMgr.Remove(Arg.Any<IReadOnlyList<string>>()).Returns(Task.CompletedTask);
        var conn = new Connection
        {
            Name = "TestConn",
            Host = "localhost",
            Port = 1883,
            Id = activeId,
            SubscribedTopics =
            [
                new() { Topic = "keep/#" },
                new() { Topic = "drop/#" }
            ]
        };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        GoToOnConnectTab();

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>();
        var checkboxes = editor.FindAll("input[type='checkbox']");
        checkboxes[2].Change(true); // select drop/#
        _dialogProvider.Find("button[title='Remove']").Click();

        // Remove must be called with the topic name even though it's not in the live set.
        await _mockSubMgr.Received(1).Remove(
            Arg.Is<IReadOnlyList<string>>(t => t.Contains("drop/#") && t.Count == 1));
    }

    [Test]
    public async Task OnConnectTab_Preset_FillsTopicDraftOnly()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);
        GoToOnConnectTab();

        var chip = _dialogProvider.FindAll("button, .mud-chip")
            .First(e => e.TextContent.Contains("spBv1.0/#"));
        chip.Click();

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>().Instance;
        editor.TopicDraft.Should().Be("spBv1.0/#");
        conn.SubscribedTopics.Should().BeEmpty();
    }

    [Test]
    public async Task OnConnectTab_SelectAllAndRemove_WhenConnectedToActiveConnection_CallsSubscriptionManagerRemove()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        var activeId = Guid.NewGuid();
        var activeConn = new Connection { Name = "Active", Host = "localhost", Port = 1883, Id = activeId };
        _mockSessionState.SelectedConnection.Returns(activeConn);
        _mockClient.IsConnected.Returns(true);
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "topic/a" },
            new() { Topic = "topic/b" }
        });
        _mockSubMgr.Remove(Arg.Any<IReadOnlyList<string>>()).Returns(Task.CompletedTask);
        var conn = new Connection
        {
            Name = "TestConn",
            Host = "localhost",
            Port = 1883,
            Id = activeId,
            SubscribedTopics =
            [
                new() { Topic = "topic/a" },
                new() { Topic = "topic/b" }
            ]
        };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        GoToOnConnectTab();

        // Select all rows via the header checkbox, then click Remove
        var editor = _dialogProvider.FindComponent<SubscriptionEditor>();
        var checkboxes = editor.FindAll("input[type='checkbox']");
        // [0] = header select-all, [1] = topic/a, [2] = topic/b
        checkboxes.Should().HaveCount(3);
        checkboxes[0].Change(true);
        _dialogProvider.Find("button[title='Remove']").Click();

        await _mockSubMgr.Received(1).Remove(
            Arg.Is<IReadOnlyList<string>>(t => t.Contains("topic/a") && t.Contains("topic/b") && t.Count == 2));
    }

    [Test]
    public async Task Connect_SetsSelectedConnection_BeforeCallingStartAsync()
    {
        // Regression: SessionState.SelectedConnection must be assigned BEFORE
        // _managedMqttClient.StartAsync is invoked. Otherwise the SubscriptionManager,
        // which subscribes to IMqttManagedClient.ConnectedAsync during app startup
        // (before this dialog opens), reads the default empty SelectedConnection in
        // its OnConnected handler and fails to re-subscribe to saved topics on
        // app restart. The dialog's OnConnected handler runs too late — by the time
        // it sets SelectedConnection, the SubscriptionManager has already read the
        // default value and given up.
        _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections =
            [
                new Connection
                {
                    Name = "TestConn",
                    Host = "localhost",
                    Port = 1883,
                    SubscribedTopics = [new SubscribedTopic { Topic = "saved/topic" }]
                }
            ]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockClient.Received(1).StartAsync(Arg.Any<MqttManagedClientOptions>());
        Received.InOrder(() =>
        {
            _mockSessionState.SelectedConnection = Arg.Is<Connection>(c => c!.SubscribedTopics.Any(s => s.Topic == "saved/topic"));
            _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>());
        });
    }

    [Test]
    public async Task Connect_StartsMessageStoreAndChart_BeforeCallingStartAsync()
    {
        _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections =
            [
                new Connection
                {
                    Name = "TestConn",
                    Host = "localhost",
                    Port = 1883,
                    SubscribedTopics = [new SubscribedTopic { Topic = "saved/topic" }]
                }
            ]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockClient.Received(1).StartAsync(Arg.Any<MqttManagedClientOptions>());
        Received.InOrder(() =>
        {
            _mockMsgStore.Start();
            _mockChart.StartAsync();
            _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>());
        });
    }

    [Test]
    public async Task ConnectingFailed_DoesNotCallSubscriptionManagerAdd()
    {
        _mockSubMgr.Add(Arg.Any<string>()).Returns(Task.CompletedTask);
        _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Connect']").Click();

        await _dialogProvider.InvokeAsync(() =>
            _failedHandler!(new MqttConnectingFailedEventArgs(new Exception("refused"))));

        await _mockSubMgr.DidNotReceive().Add(Arg.Any<string>());
    }

    [Test]
    public async Task SelectingConnectionFromList_PopulatesFormFields()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "My Broker", Host = "192.168.1.10", Port = 1883 }]
        };
        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Markup.Should().Contain("My Broker");
        _dialogProvider.Markup.Should().Contain("192.168.1.10");
    }

    [Test]
    public async Task FormValidationChanged_False_DisablesSaveAndConnect_EvenWithValidModel()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "Saved", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        DirtyNameField("Saved Edited");
        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled").Should().BeNull();

        await _dialogProvider.InvokeAsync(
            () => _dialogProvider.FindComponent<ConnectionDialog>().Instance.FormValidationChanged(false));

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().NotBeNull("a form-level error such as a failed value conversion must block saving");
        _dialogProvider.Find("button[title='Connect']").GetAttribute("disabled").Should().NotBeNull();
    }


    [Test]
    public async Task ConnectingFailed_ShowsConnectionFailedAlert()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        _failedHandler.Should().NotBeNull("component should subscribe to ConnectingFailedAsync in OnInitialized");

        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Connect']").Click();

        await _dialogProvider.InvokeAsync(() => _failedHandler!(new MqttConnectingFailedEventArgs(new Exception("refused"))));

        _dialogProvider.Markup.Should().Contain("Connection failed. Verify broker, credentials, and transport settings.");
    }

    [Test]
    public async Task Save_CallsConfigurationManagerAddConnectionAsync()
    {
        var conn = new Connection { Name = "Test", Host = "localhost", Port = 1883 };
        var cfg = new AppConfiguration { Connections = [conn] };
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);

        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);
        DirtyNameField("Test Updated");

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled").Should().BeNull();
        _dialogProvider.Find("button[title='Save connection']").Click();

        await _mockConnections.Received(1).AddConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public async Task SaveButton_DoesNotCloseDialog()
    {
        var conn = new Connection { Name = "Test", Host = "localhost", Port = 1883 };
        var cfg = new AppConfiguration { Connections = [conn] };
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        DirtyNameField("Test Updated");

        _dialogProvider.Find("button[title='Save connection']").Click();

        await _mockConnections.Received(1).AddConnectionAsync(Arg.Any<Connection>());
        _dialogProvider.FindAll(".mud-dialog").Should().NotBeEmpty("saving should keep the dialog open");
    }

    [Test]
    public async Task SaveButton_InvalidDirtyInput_StaysDisabled()
    {
        await OpenDialog(new AppConfiguration());

        DirtyNameField("My Broker");
        SetTextField("Host", "localhost");
        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().BeNull("a dirty, valid form should allow saving");

        DirtyNameField("");
        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().NotBeNull("an invalid form must not be saveable even when dirty");
    }

    [Test]
    public async Task SaveButton_ValidDirtyInput_IsEnabled()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "Saved", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);
        DirtyNameField("Saved Edited");

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled").Should().BeNull();
    }

    [Test]
    public async Task SaveButton_ValidUnchangedInput_StaysDisabled()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "Saved", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);
        await _dialogProvider.InvokeAsync(
            () => _dialogProvider.FindComponent<ConnectionDialog>().Instance.FormValidationChanged(true));

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().NotBeNull("a valid form matching an existing saved connection is not dirty");
    }

    [Test]
    public async Task CertPasswordField_ShowsBoundValue_AndEnablesSaveWhenTyped()
    {
        var conn = new Connection { Name = "TLS Conn", Host = "localhost", Port = 8883, UseTls = true };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);
        ActivateTab("Security");

        var pwField = _dialogProvider.FindComponents<MudTextField<string>>()
            .First(f => f.Instance.Label == "PFX Password");

        // Regression: the field must bind to the value of _certPassword (empty), not render
        // the literal parameter name "_certPassword".
        (pwField.Find("input").GetAttribute("value") ?? "").Should().NotBe("_certPassword");

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().NotBeNull("unchanged connection is not dirty");

        pwField.Find("input").Input("mypass");

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().BeNull("entering a certificate password should enable Save");
    }

    [Test]
    public async Task SaveButton_Enabled_WhenBrokerPasswordCleared()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "Saved", Host = "localhost", Port = 1883, Password = "secret" }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().NotBeNull("an unchanged saved connection is not dirty");

        SetTextField("Password", "");

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().BeNull("clearing the broker password is a change and should enable Save");
    }

    [Test]
    public async Task SaveButton_Enabled_WhenBrokerPasswordCleared_OnP12Connection()
    {
        var conn = new Connection
        {
            Name = "P12 Conn",
            Host = "localhost",
            Port = 8883,
            UseTls = true,
            Password = "secret",
            ClientCertificateAssetId = Guid.NewGuid().ToString("D")
        };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().NotBeNull("an unchanged saved connection is not dirty");

        SetTextField("Password", "");

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().BeNull("clearing the broker password on a P12 connection should enable Save");
    }

    private void FillNewConnection()
    {
        DirtyNameField("My Broker");
        SetTextField("Host", "localhost");
    }

    [Test]
    public async Task Connect_WithUnsavedChanges_ShowsSaveBeforeConnectPrompt()
    {
        await OpenDialog(new AppConfiguration());
        FillNewConnection();

        _dialogProvider.Find("button[title='Connect']").Click();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.Markup.Should().Contain("Unsaved changes"));
        await _mockClient.DidNotReceive().StartAsync(Arg.Any<MqttManagedClientOptions>());
    }

    private void DirtyNameField(string newName)
    {
        var nameField = _dialogProvider.FindComponents<MudTextField<string>>()
            .First(f => f.Instance.Label == "Name");
        nameField.Find("input").Input(newName);
    }

    private void SetTextField(string label, string value)
    {
        var field = _dialogProvider.FindComponents<MudTextField<string>>()
            .First(f => f.Instance.Label == label);
        field.Find("input").Input(value);
    }

    private void ActivateTab(string text)
    {
        var tab = _dialogProvider.FindAll(".mud-tab")
            .First(t => t.TextContent.Contains(text));
        tab.Click();
    }

    private void GoToOnConnectTab()
    {
        var tabs = _dialogProvider.FindAll(".mud-tab");
        var onConnect = tabs.First(t => t.TextContent.Contains("On Connect", StringComparison.OrdinalIgnoreCase));
        onConnect.Click();
    }

    [Test]
    public async Task SaveButton_Enables_WhenFreshDialogFilledOut_WithoutClickingAdd()
    {
        await OpenDialog(new AppConfiguration());

        DirtyNameField("My Broker");
        SetTextField("Host", "localhost");

        _dialogProvider.WaitForAssertion(() =>
            _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
                .Should().BeNull("filling out a valid new connection should enable Save without clicking Add first"),
            TimeSpan.FromSeconds(2));
        _dialogProvider.Find("button[title='Connect']").GetAttribute("disabled")
            .Should().BeNull("a valid new connection should also be connectable");
    }

    [Test]
    public async Task SaveButton_Enables_WhenFilledOut_AfterClickingAdd()
    {
        await OpenDialog(new AppConfiguration());

        _dialogProvider.Find("button[title='New connection']").Click();
        DirtyNameField("My Broker");
        SetTextField("Host", "localhost");

        _dialogProvider.WaitForAssertion(() =>
            _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
                .Should().BeNull("filling out a valid new connection after Add should enable Save"),
            TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Delete_CallsConfigurationManagerRemoveConnectionAsync()
    {
        var conn = new Connection { Name = "ToDelete", Host = "localhost", Port = 1883 };
        var cfg = new AppConfiguration { Connections = [conn] };
        _mockConnections.RemoveConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);

        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Delete connection']").Click();

        await _mockConnections.Received(1).RemoveConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public async Task Renders_ThreeTabPanels_ForConnectionSecurityAndOnConnect()
    {
        await OpenDialog(new AppConfiguration());

        var tabPanels = _dialogProvider.FindAll(".mud-tab-panel[role='tabpanel']");
        tabPanels.Should().HaveCount(3, "Connection, Security, and On Connect panels are always rendered");
    }

    [Test]
    public async Task Connect_CallsResetCoordinatorBeforeStartAsync()
    {
        _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Connect']").Click();

        Received.InOrder(() =>
        {
            _mockCoordinator.ResetIfBrokerChangedAsync(Arg.Any<Connection>());
            _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>());
        });
    }

    [Test]
    public async Task Connect_PassesSelectedConnectionToCoordinator()
    {
        _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>()).Returns(Task.CompletedTask);
        var conn = new Connection { Name = "TestConn", Host = "broker.example.com", Port = 8883 };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockCoordinator.Received(1).ResetIfBrokerChangedAsync(
            Arg.Is<Connection>(c => c!.Host == "broker.example.com" && c.Port == 8883));
    }

    [Test]
    public async Task Connect_WhenCoordinatorThrows_StillProceedsWithConnect()
    {
        _mockCoordinator.ResetIfBrokerChangedAsync(Arg.Any<Connection>())
            .Returns(Task.FromException(new Exception("reset failed")));
        _mockClient.StartAsync(Arg.Any<MqttManagedClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockClient.Received(1).StartAsync(Arg.Any<MqttManagedClientOptions>());
    }

    [Test]
    public async Task StepSelector_RendersThreeStepButtons()
    {
        await OpenDialog(new AppConfiguration());

        var stepButtons = _dialogProvider.FindAll(".step-btn");
        stepButtons.Should().HaveCount(3);
        stepButtons[0].TextContent.Should().Contain("Connection");
        stepButtons[1].TextContent.Should().Contain("Security");
        stepButtons[2].TextContent.Should().Contain("On Connect");
    }

    [Test]
    public async Task StepSelector_ClickSecurity_ActivatesSecurityButton()
    {
        await OpenDialog(new AppConfiguration());

        var stepButtons = _dialogProvider.FindAll(".step-btn");
        stepButtons[0].ClassName.Should().Contain("active", "Connection is default");

        stepButtons[1].Click();

        var refreshedButtons = _dialogProvider.FindAll(".step-btn");
        refreshedButtons[1].ClassName.Should().Contain("active", "Security was clicked");
        refreshedButtons[0].ClassName.Should().NotContain("active", "Connection is no longer selected");
        _dialogProvider.Markup.Should().Contain("Turn on Use TLS");
    }

    [Test]
    public async Task StepSelector_ClickOnConnect_ActivatesOnConnectButton()
    {
        await OpenDialog(new AppConfiguration());

        var stepButtons = _dialogProvider.FindAll(".step-btn");
        stepButtons[2].Click();

        var refreshedButtons = _dialogProvider.FindAll(".step-btn");
        refreshedButtons[2].ClassName.Should().Contain("active", "On Connect was clicked");
        refreshedButtons[0].ClassName.Should().NotContain("active", "Connection is no longer selected");
        _dialogProvider.Markup.Should().Contain("No on-connect subscriptions yet");
    }

    [Test]
    public async Task StepSelector_ConnectionIsDefaultStep()
    {
        await OpenDialog(new AppConfiguration());

        var connectionBtn = _dialogProvider.FindAll(".step-btn")
            .First(b => b.TextContent.Contains("Connection"));
        connectionBtn.ClassName.Should().Contain("active");
        _dialogProvider.Markup.Should().Contain("Name");
        _dialogProvider.Markup.Should().Contain("Host");
    }

    [Test]
    public async Task TlsEnabled_ShowsClientCertificateSection()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TLS", Host = "tls.local", Port = 8883, UseTls = true }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Markup.Should().Contain("Client Certificate");
    }

    [Test]
    public async Task TlsDisabled_HidesClientCertificateSection()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "Plain", Host = "plain.local", Port = 1883, UseTls = false }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Markup.Should().NotContain("Client Certificate");
    }

    [Test]
    public async Task ConnectionChanged_WithExistingAsset_ShowsSummary()
    {
        var cert = TestCertFactory.CreateRsaCert();
        _mockCertStore.LoadAsync(Arg.Any<Guid>(), "existing-asset")
            .Returns(new ClientCertificateBundle(cert));

        var cfg = new AppConfiguration
        {
            Connections = [new Connection
            {
                Name = "CertConn", Host = "tls.local", Port = 8883,
                UseTls = true, ClientCertificateAssetId = "existing-asset"
            }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Markup.Should().Contain("Certificate loaded");
    }

    [Test]
    public async Task ConnectionChanged_MissingAsset_ShowsUnavailableError()
    {
        _mockCertStore.LoadAsync(Arg.Any<Guid>(), "missing-asset")
            .Returns((ClientCertificateBundle?)null);

        var cfg = new AppConfiguration
        {
            Connections = [new Connection
            {
                Name = "CertConn", Host = "tls.local", Port = 8883,
                UseTls = true, ClientCertificateAssetId = "missing-asset"
            }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Markup.Should().Contain("unavailable or corrupt");
    }

    [Test]
    public async Task RemoveCertificate_ClearsAssetId_ShowsImportControls()
    {
        var cert = TestCertFactory.CreateRsaCert();
        _mockCertStore.LoadAsync(Arg.Any<Guid>(), "old-asset")
            .Returns(new ClientCertificateBundle(cert));

        var cfg = new AppConfiguration
        {
            Connections = [new Connection
            {
                Name = "CertConn", Host = "tls.local", Port = 8883,
                UseTls = true, ClientCertificateAssetId = "old-asset"
            }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Remove certificate']").Click();

        _dialogProvider.Markup.Should().Contain("PFX/P12");
        _dialogProvider.Markup.Should().NotContain("Certificate loaded");
    }

    [Test]
    public async Task CertificateModeToggle_SwitchesBetweenPfxAndPem()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TLS", Host = "tls.local", Port = 8883, UseTls = true }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        // Default is PFX mode
        _dialogProvider.Markup.Should().Contain("Select PFX/P12 file");

        // Click PEM toggle item
        _dialogProvider.Find(".mud-toggle-item");
        // The first toggle item is PFX (default), the second is PEM
        var toggleItems = _dialogProvider.FindAll(".mud-toggle-item");
        var pemToggle = toggleItems.First(i => i.TextContent.Trim() == "PEM");
        pemToggle.Click();

        _dialogProvider.Markup.Should().Contain("Select certificate PEM");
        _dialogProvider.Markup.Should().Contain("Select private key PEM");
    }

    [Test]
    public async Task PasswordField_RendersForCertInput()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TLS", Host = "tls.local", Port = 8883, UseTls = true }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Markup.Should().Contain("PFX Password");
    }

    [Test]
    public async Task Connect_BlocksWhenCertificateUnavailable()
    {
        _mockCertStore.LoadAsync(Arg.Any<Guid>(), "bad-asset")
            .Returns((ClientCertificateBundle?)null);

        var cfg = new AppConfiguration
        {
            Connections = [new Connection
            {
                Name = "CertConn", Host = "tls.local", Port = 8883,
                UseTls = true, ClientCertificateAssetId = "bad-asset"
            }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Connect']").Click();

        _dialogProvider.Markup.Should().Contain("unavailable or corrupt");
        await _mockClient.DidNotReceive().StartAsync(Arg.Any<MqttManagedClientOptions>());
    }

    [Test]
    public async Task ProtocolAndMqttVersionSelects_HavePopoverFixedAndExplicitOrigins()
    {
        await OpenDialog(new AppConfiguration());

        var protocolSelect = _dialogProvider.FindComponents<MudSelect<Protocol>>()
            .Single(s => s.Instance.Label == "Protocol");
        var mqttVersionSelect = _dialogProvider.FindComponents<MudSelect<MqttVersion>>()
            .Single(s => s.Instance.Label == "MQTT Version");

        protocolSelect.Instance.PopoverFixed.Should().BeTrue();
        protocolSelect.Instance.AnchorOrigin.Should().Be(Origin.BottomLeft);
        protocolSelect.Instance.TransformOrigin.Should().Be(Origin.TopLeft);

        mqttVersionSelect.Instance.PopoverFixed.Should().BeTrue();
        mqttVersionSelect.Instance.AnchorOrigin.Should().Be(Origin.BottomLeft);
        mqttVersionSelect.Instance.TransformOrigin.Should().Be(Origin.TopLeft);
    }

    [Test]
    public async Task OnConnectTab_RendersAutoResubscribeSwitch_BoundToConfig()
    {
        await OpenDialog(new AppConfiguration
        {
            Ui = new UiPreferences { AutoResubscribe = true },
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        });
        await SelectConnection(_dialogProvider
            .FindComponents<MudSelectItem<Connection?>>().First().Instance.Value!);

        var sw = _dialogProvider.FindComponents<MudSwitch<bool>>()
            .Single(s => s.Instance.Label == "Auto-resubscribe on connect");
        sw.Instance.GetState(x => x.Value).Should().BeTrue();
    }

    [Test]
    public async Task OnConnectTab_AutoResubscribeOff_DisablesAddControlsAndShowsAlert()
    {
        await OpenDialog(new AppConfiguration
        {
            Ui = new UiPreferences { AutoResubscribe = false },
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        });
        await SelectConnection(_dialogProvider
            .FindComponents<MudSelectItem<Connection?>>().First().Instance.Value!);
        GoToOnConnectTab();

        _dialogProvider.Markup.Should().Contain("On-connect subscriptions are not applied until Auto-resubscribe is enabled");

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>().Instance;
        editor.Disabled.Should().BeTrue();
    }

    [Test]
    public async Task OnConnectTab_AutoResubscribeOff_AddDoesNotPersistOrAlert()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        await OpenDialog(new AppConfiguration
        {
            Ui = new UiPreferences { AutoResubscribe = false },
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        });
        await SelectConnection(_dialogProvider
            .FindComponents<MudSelectItem<Connection?>>().First().Instance.Value!);
        GoToOnConnectTab();

        var editor = _dialogProvider.FindComponent<SubscriptionEditor>().Instance;
        editor.TopicDraft = "factory/#";
        await _dialogProvider.InvokeAsync(() => editor.AddForTests());

        await _mockConnections.DidNotReceive().AddConnectionAsync(Arg.Any<Connection>());
        editor.TopicDraft.Should().Be("factory/#",
            "the draft topic should remain unchanged when add is blocked");
    }

    [Test]
    public async Task OnConnectTab_AutoResubscribeOff_RemoveDoesNotPersist()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        var conn = new Connection
        {
            Name = "TestConn",
            Host = "localhost",
            Port = 1883,
            SubscribedTopics = [new() { Topic = "keep/#" }]
        };
        await OpenDialog(new AppConfiguration
        {
            Ui = new UiPreferences { AutoResubscribe = false },
            Connections = [conn]
        });
        await SelectConnection(conn);
        GoToOnConnectTab();

        // Select the row checkbox and click Remove
        var editor = _dialogProvider.FindComponent<SubscriptionEditor>();
        editor.Find("input[type='checkbox']").Change(true);
        _dialogProvider.Find("button[title='Remove']").Click();

        await _mockConnections.DidNotReceive().AddConnectionAsync(Arg.Any<Connection>());
        conn.SubscribedTopics.Should().ContainSingle(s => s.Topic == "keep/#");
    }

    [Test]
    public async Task OnConnectTab_AutoResubscribeSwitch_Toggle_CallsSetter()
    {
        _mockUi.SetAutoResubscribeAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        await OpenDialog(new AppConfiguration
        {
            Ui = new UiPreferences { AutoResubscribe = true },
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        });
        await SelectConnection(_dialogProvider
            .FindComponents<MudSelectItem<Connection?>>().First().Instance.Value!);

        var sw = _dialogProvider.FindComponents<MudSwitch<bool>>()
            .Single(s => s.Instance.Label == "Auto-resubscribe on connect");
        await _dialogProvider.InvokeAsync(() => sw.Instance.ValueChanged.InvokeAsync(false));

        await _mockUi.Received(1).SetAutoResubscribeAsync(false);
    }

    [Test]
    public async Task CertState_PreservedAcrossTlsToggle_KeepsStagedSaveEnabled()
    {
        var conn = new Connection { Name = "TLS Conn", Host = "localhost", Port = 8883, UseTls = true };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        // Stage a certificate via the test hook
        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        await _dialogProvider.InvokeAsync(() =>
            dialog.SetStagedCertificate([1, 2, 3], null, null, ""));

        _dialogProvider.Find("button[title='Save connection']")
            .GetAttribute("disabled").Should().BeNull("staging a certificate should enable Save");

        // Toggle TLS off — cert UI should hide but staged state must survive
        var tlsCheckbox = _dialogProvider.FindComponents<MudCheckBox<bool>>()
            .First(c => c.Markup.Contains("Use TLS"));
        await _dialogProvider.InvokeAsync(() => tlsCheckbox.Instance.ValueChanged.InvokeAsync(false));
        _dialogProvider.Render();

        // The cert section's inner markup (PFX toggle) should be hidden via Visible=false
        _dialogProvider.Markup.Should().NotContain("PFX/P12",
            "cert input controls should be hidden when TLS is off");

        _dialogProvider.Find("button[title='Save connection']")
            .GetAttribute("disabled").Should().BeNull("staged cert state must survive TLS toggle");

        // Toggle TLS back on — cert UI reappears, state intact
        await _dialogProvider.InvokeAsync(() => tlsCheckbox.Instance.ValueChanged.InvokeAsync(true));
        _dialogProvider.Render();

        _dialogProvider.Markup.Should().Contain("PFX/P12",
            "cert controls should reappear when TLS is back on");
        _dialogProvider.Markup.Should().Contain("PFX file loaded",
            "staged cert bytes must survive TLS off/on cycle");
    }

    [Test]
    public async Task ConnectionTab_ShowsCleanSessionLabel_ByDefault()
    {
        await OpenDialog(new AppConfiguration());

        // Default MqttVersion is V311, so label should be "Clean session"
        _dialogProvider.Markup.Should().Contain("Clean session");
        _dialogProvider.Markup.Should().NotContain("Clean start");
    }

    [Test]
    public async Task ConnectionTab_ShowsCleanStartLabel_AfterSwitchingToMqtt5()
    {
        await OpenDialog(new AppConfiguration());

        var mqttSelect = _dialogProvider.FindComponents<MudSelect<MqttVersion>>()
            .Single(s => s.Instance.Label == "MQTT Version");
        await _dialogProvider.InvokeAsync(() =>
            mqttSelect.Instance.ValueChanged.InvokeAsync(MqttVersion.V5));
        _dialogProvider.Render();

        _dialogProvider.Markup.Should().Contain("Clean start");
        _dialogProvider.Markup.Should().NotContain("Clean session");
    }

    [Test]
    public async Task CleanStartCheckbox_DefaultsToChecked()
    {
        await OpenDialog(new AppConfiguration());

        var cb = FindCleanStartCheckbox();
        cb.Markup.Should().Contain("mud-checkbox-true",
            "CleanStart defaults to true, so the checkbox should render as checked");
    }

    [Test]
    public async Task CleanStartUnchecked_Mqtt5_SessionExpiryFieldVisible()
    {
        await OpenDialog(new AppConfiguration());

        // Switch to MQTT 5
        var mqttSelect = _dialogProvider.FindComponents<MudSelect<MqttVersion>>()
            .Single(s => s.Instance.Label == "MQTT Version");
        await _dialogProvider.InvokeAsync(() =>
            mqttSelect.Instance.ValueChanged.InvokeAsync(MqttVersion.V5));
        _dialogProvider.Render();

        // Uncheck CleanStart
        var cb = FindCleanStartCheckbox();
        await _dialogProvider.InvokeAsync(() =>
            cb.Instance.ValueChanged.InvokeAsync(false));
        _dialogProvider.Render();

        _dialogProvider.Markup.Should().Contain("Session expiry interval",
            "MQTT 5 with CleanStart off should show session expiry interval field");
    }

    [Test]
    public async Task CleanStartUnchecked_Mqtt311_SessionExpiryFieldHidden()
    {
        await OpenDialog(new AppConfiguration());

        // Default is V311 — uncheck CleanStart
        var cb = FindCleanStartCheckbox();
        await _dialogProvider.InvokeAsync(() =>
            cb.Instance.ValueChanged.InvokeAsync(false));
        _dialogProvider.Render();

        _dialogProvider.Markup.Should().NotContain("Session expiry interval",
            "MQTT 3.1.1 with CleanStart off should NOT show session expiry interval field");
    }

    [Test]
    public async Task CleanStartRechecked_HidesExpiry_RetainsModelValue()
    {
        await OpenDialog(new AppConfiguration());

        // Switch to MQTT 5 and uncheck CleanStart
        var mqttSelect = _dialogProvider.FindComponents<MudSelect<MqttVersion>>()
            .Single(s => s.Instance.Label == "MQTT Version");
        await _dialogProvider.InvokeAsync(() =>
            mqttSelect.Instance.ValueChanged.InvokeAsync(MqttVersion.V5));
        _dialogProvider.Render();

        var cb = FindCleanStartCheckbox();
        await _dialogProvider.InvokeAsync(() =>
            cb.Instance.ValueChanged.InvokeAsync(false));
        _dialogProvider.Render();

        _dialogProvider.Markup.Should().Contain("Session expiry interval");

        // Re-check CleanStart
        await _dialogProvider.InvokeAsync(() =>
            cb.Instance.ValueChanged.InvokeAsync(true));
        _dialogProvider.Render();

        _dialogProvider.Markup.Should().NotContain("Session expiry interval",
            "session expiry field should hide when CleanStart is re-checked");

        // Model value should be retained
        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        var connField = typeof(ConnectionDialog)
            .GetField("_selectedConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var conn = (Connection)connField.GetValue(dialog)!;
        conn.SessionExpiryIntervalSeconds.Should().Be(3600u,
            "the default session expiry value should be retained on the model even after re-checking CleanStart");
    }

    private IRenderedComponent<MudCheckBox<bool>> FindCleanStartCheckbox() =>
        _dialogProvider.FindComponents<MudCheckBox<bool>>()
            .First(c => c.Markup.Contains("Clean session") || c.Markup.Contains("Clean start"));

    [Test]
    public async Task RemoveCert_ThenSave_DoesNotShowCertificateLoaded()
    {
        _mockConnections.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);
        var cert = TestCertFactory.CreateRsaCert();
        _mockCertStore.LoadAsync(Arg.Any<Guid>(), "old-asset")
            .Returns(new ClientCertificateBundle(cert));

        var conn = new Connection
        {
            Name = "CertConn",
            Host = "tls.local",
            Port = 8883,
            UseTls = true,
            ClientCertificateAssetId = "old-asset"
        };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        // Verify cert is loaded
        _dialogProvider.Markup.Should().Contain("Certificate loaded");

        // Remove the certificate
        _dialogProvider.Find("button[title='Remove certificate']").Click();
        _dialogProvider.Markup.Should().NotContain("Certificate loaded");
        _dialogProvider.Markup.Should().Contain("PFX/P12", "import controls should show after remove");

        // Dirty the name so Save is enabled, then save
        DirtyNameField("CertConn Edited");
        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled").Should().BeNull();

        _dialogProvider.Find("button[title='Save connection']").Click();
        await _mockConnections.Received(1).AddConnectionAsync(Arg.Any<Connection>());

        // After save, "Certificate loaded" must NOT reappear
        _dialogProvider.Markup.Should().NotContain("Certificate loaded",
            "removed cert must not reappear after save");
    }

    [Test]
    public async Task TabLabels_AreConnectionSecurityOnConnect()
    {
        await OpenDialog(new AppConfiguration());

        var tabs = _dialogProvider.FindAll(".mud-tab");
        tabs.Should().HaveCount(3);
        tabs[0].TextContent.Should().Contain("Connection");
        tabs[1].TextContent.Should().Contain("Security");
        tabs[2].TextContent.Should().Contain("On Connect");
    }

    [Test]
    public async Task ConnectionTab_Placement_ContainsAllExpectedFields()
    {
        await OpenDialog(new AppConfiguration());

        // Connection tab is the default; all these fields should be in the DOM
        _dialogProvider.Markup.Should().Contain("Host");
        _dialogProvider.Markup.Should().Contain("Port");
        _dialogProvider.Markup.Should().Contain("Protocol");
        _dialogProvider.Markup.Should().Contain("MQTT Version");
        _dialogProvider.Markup.Should().Contain("User");
        _dialogProvider.Markup.Should().Contain("Use TLS");
        _dialogProvider.Markup.Should().Contain("Name");
        _dialogProvider.Markup.Should().Contain("Client ID");
        _dialogProvider.Markup.Should().Contain("Clean session");
    }

    [Test]
    public async Task TlsHint_ShownWhenUseTlsTrue()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TLS", Host = "tls.local", Port = 8883, UseTls = true }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Markup.Should().Contain("Certificate and trust options are on the Security tab.");
    }

    [Test]
    public async Task SecurityTab_TlsOff_ShowsExplanationAndHidesCertControls()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "Plain", Host = "plain.local", Port = 1883, UseTls = false }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        ActivateTab("Security");

        _dialogProvider.Markup.Should().Contain("Turn on Use TLS on the Connection tab");
        _dialogProvider.Markup.Should().NotContain("Allow untrusted certificate");
        _dialogProvider.Markup.Should().NotContain("Client Certificate");
    }

    [Test]
    public async Task SecurityTab_TlsOn_ShowsUntrustedAndClientCert()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TLS", Host = "tls.local", Port = 8883, UseTls = true }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        ActivateTab("Security");

        _dialogProvider.Markup.Should().Contain("Allow untrusted certificate");
        _dialogProvider.Markup.Should().Contain("Client Certificate");
    }

    [Test]
    public async Task AdvancedTiming_CollapsedAtDefaults()
    {
        await OpenDialog(new AppConfiguration());

        var panel = _dialogProvider.FindComponents<MudExpansionPanel>()
            .First(p => p.Instance.Text == "Advanced timing");
        panel.Instance.Expanded.Should().BeFalse("fresh connection has default timing values");
    }

    [Test]
    public async Task AdvancedTiming_ExpandedWhenNonDefault()
    {
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "Custom", Host = "localhost", Port = 1883, ConnectTimeout = 30 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        var panel = _dialogProvider.FindComponents<MudExpansionPanel>()
            .First(p => p.Instance.Text == "Advanced timing");
        panel.Instance.Expanded.Should().BeTrue("non-default timing should expand the panel");
    }

    [Test]
    public async Task PortPairing_MqttMateSwap_TlsToggleOn()
    {
        var conn = new Connection { Name = "Test", Host = "localhost", Port = 1883, Protocol = Protocol.Mqtt, UseTls = false };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        var tlsCheckbox = _dialogProvider.FindComponents<MudCheckBox<bool>>()
            .First(c => c.Markup.Contains("Use TLS"));
        await _dialogProvider.InvokeAsync(() => tlsCheckbox.Instance.ValueChanged.InvokeAsync(true));
        _dialogProvider.Render();

        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        var connField = typeof(ConnectionDialog)
            .GetField("_selectedConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var updatedConn = (Connection)connField.GetValue(dialog)!;
        updatedConn.Port.Should().Be(8883, "toggling TLS on for port 1883 should pair to 8883");

        var portField = _dialogProvider.FindComponents<MudTextField<int>>()
            .First(f => f.Instance.Label == "Port");
        portField.Instance.GetState(x => x.Value).Should().Be(8883);
    }

    [Test]
    public async Task PortPairing_CustomPort_NoOp()
    {
        var conn = new Connection { Name = "Test", Host = "localhost", Port = 8884, Protocol = Protocol.Mqtt, UseTls = false };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        var tlsCheckbox = _dialogProvider.FindComponents<MudCheckBox<bool>>()
            .First(c => c.Markup.Contains("Use TLS"));
        await _dialogProvider.InvokeAsync(() => tlsCheckbox.Instance.ValueChanged.InvokeAsync(true));
        _dialogProvider.Render();

        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        var connField = typeof(ConnectionDialog)
            .GetField("_selectedConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var updatedConn = (Connection)connField.GetValue(dialog)!;
        updatedConn.Port.Should().Be(8884, "custom port should not be changed by TLS toggle");

        var portField = _dialogProvider.FindComponents<MudTextField<int>>()
            .First(f => f.Instance.Label == "Port");
        portField.Instance.GetState(x => x.Value).Should().Be(8884);
    }

    [Test]
    public async Task PortPairing_WebSocket_MateSwap()
    {
        var conn = new Connection { Name = "WS", Host = "ws.local", Port = 8083, Protocol = Protocol.WebSocket, UseTls = false };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        var tlsCheckbox = _dialogProvider.FindComponents<MudCheckBox<bool>>()
            .First(c => c.Markup.Contains("Use TLS"));
        await _dialogProvider.InvokeAsync(() => tlsCheckbox.Instance.ValueChanged.InvokeAsync(true));
        _dialogProvider.Render();

        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        var connField = typeof(ConnectionDialog)
            .GetField("_selectedConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var updatedConn = (Connection)connField.GetValue(dialog)!;
        updatedConn.Port.Should().Be(8084, "toggling TLS on for WebSocket port 8083 should pair to 8084");

        var portField = _dialogProvider.FindComponents<MudTextField<int>>()
            .First(f => f.Instance.Label == "Port");
        portField.Instance.GetState(x => x.Value).Should().Be(8084);
    }

    [Test]
    public async Task TlsToggle_OffThenOn_RetainsUntrustedAndCertSettings()
    {
        var conn = new Connection
        {
            Name = "TLS Conn",
            Host = "localhost",
            Port = 8883,
            UseTls = true,
            AllowUntrustedCertificate = true,
            ClientCertificateAssetId = "keep-me"
        };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        // Toggle TLS off
        var tlsCheckbox = _dialogProvider.FindComponents<MudCheckBox<bool>>()
            .First(c => c.Markup.Contains("Use TLS"));
        await _dialogProvider.InvokeAsync(() => tlsCheckbox.Instance.ValueChanged.InvokeAsync(false));
        _dialogProvider.Render();

        // Toggle TLS back on
        await _dialogProvider.InvokeAsync(() => tlsCheckbox.Instance.ValueChanged.InvokeAsync(true));
        _dialogProvider.Render();

        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        var connField = typeof(ConnectionDialog)
            .GetField("_selectedConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var updatedConn = (Connection)connField.GetValue(dialog)!;
        updatedConn.AllowUntrustedCertificate.Should().BeTrue(
            "AllowUntrustedCertificate should survive TLS off/on toggle");
        updatedConn.ClientCertificateAssetId.Should().Be("keep-me",
            "ClientCertificateAssetId should survive TLS off/on toggle");
    }

    [Test]
    public async Task ProtocolChange_Mqtt1883_Plain_ToWebSocket_SetsPort8083()
    {
        var conn = new Connection { Name = "Test", Host = "localhost", Port = 1883, Protocol = Protocol.Mqtt, UseTls = false };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        var protocolSelect = _dialogProvider.FindComponents<MudSelect<Protocol>>()
            .Single(s => s.Instance.Label == "Protocol");
        await _dialogProvider.InvokeAsync(() =>
            protocolSelect.Instance.ValueChanged.InvokeAsync(Protocol.WebSocket));
        _dialogProvider.Render();

        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        var connField = typeof(ConnectionDialog)
            .GetField("_selectedConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var updatedConn = (Connection)connField.GetValue(dialog)!;
        updatedConn.Port.Should().Be(8083, "switching protocol from MQTT to WebSocket with port 1883 should pair to 8083");

        var portField = _dialogProvider.FindComponents<MudTextField<int>>()
            .First(f => f.Instance.Label == "Port");
        portField.Instance.GetState(x => x.Value).Should().Be(8083);
    }

    [Test]
    public async Task ProtocolChange_Mqtt1883_Plain_ToWebSocket_ThenTlsOn_SetsPort8084()
    {
        var conn = new Connection { Name = "Test", Host = "localhost", Port = 1883, Protocol = Protocol.Mqtt, UseTls = false };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        var protocolSelect = _dialogProvider.FindComponents<MudSelect<Protocol>>()
            .Single(s => s.Instance.Label == "Protocol");
        await _dialogProvider.InvokeAsync(() =>
            protocolSelect.Instance.ValueChanged.InvokeAsync(Protocol.WebSocket));
        _dialogProvider.Render();

        var tlsCheckbox = _dialogProvider.FindComponents<MudCheckBox<bool>>()
            .First(c => c.Markup.Contains("Use TLS"));
        await _dialogProvider.InvokeAsync(() => tlsCheckbox.Instance.ValueChanged.InvokeAsync(true));
        _dialogProvider.Render();

        var dialog = _dialogProvider.FindComponent<ConnectionDialog>().Instance;
        var connField = typeof(ConnectionDialog)
            .GetField("_selectedConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var updatedConn = (Connection)connField.GetValue(dialog)!;
        updatedConn.Port.Should().Be(8084, "after protocol switch to WS (8083) then TLS on should pair to 8084");

        var portField = _dialogProvider.FindComponents<MudTextField<int>>()
            .First(f => f.Instance.Label == "Port");
        portField.Instance.GetState(x => x.Value).Should().Be(8084);
    }

    [Test]
    public async Task AdvancedTiming_AcrossConnectionSwitch()
    {
        var defaultConn = new Connection { Name = "Default", Host = "localhost", Port = 1883 };
        var customConn = new Connection { Name = "Custom", Host = "localhost", Port = 1883, ConnectTimeout = 30 };
        var cfg = new AppConfiguration { Connections = [defaultConn, customConn] };
        await OpenDialog(cfg);

        // Select default-timing connection → collapsed
        await SelectConnection(defaultConn);
        var panel = _dialogProvider.FindComponents<MudExpansionPanel>()
            .First(p => p.Instance.Text == "Advanced timing");
        panel.Instance.Expanded.Should().BeFalse("default timing connection should collapse panel");

        // Select non-default-timing connection → expanded
        await SelectConnection(customConn);
        panel = _dialogProvider.FindComponents<MudExpansionPanel>()
            .First(p => p.Instance.Text == "Advanced timing");
        panel.Instance.Expanded.Should().BeTrue("non-default timing connection should expand panel");

        // Switch back to default-timing connection → collapsed again
        await SelectConnection(defaultConn);
        panel = _dialogProvider.FindComponents<MudExpansionPanel>()
            .First(p => p.Instance.Text == "Advanced timing");
        panel.Instance.Expanded.Should().BeFalse("returning to default timing should collapse panel again");
    }
}
