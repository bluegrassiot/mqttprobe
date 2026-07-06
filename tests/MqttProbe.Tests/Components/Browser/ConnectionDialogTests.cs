using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Telemetry;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class ConnectionDialogTests : BunitTestContext
{
    private IManagedMqttClient _mockClient = null!;
    private ISettingsStore _mockConfigMgr = null!;
    private IMessageStoreManager _mockMsgStore = null!;
    private ISubscriptionManager _mockSubMgr = null!;
    private ISessionState _mockSessionState = null!;
    private IMqttOptionsBuilder _mockOptionsBuilder = null!;
    private IBrokerStateResetCoordinator _mockCoordinator = null!;
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;

    private Func<MqttClientConnectedEventArgs, Task>? _connectedHandler;
    private Func<ConnectingFailedEventArgs, Task>? _failedHandler;

    [SetUp]
    public void SetupMocks()
    {
        _mockClient = Substitute.For<IManagedMqttClient>();
        _mockConfigMgr = Substitute.For<ISettingsStore>();
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockSubMgr = Substitute.For<ISubscriptionManager>();
        _mockSessionState = Substitute.For<ISessionState>();
        _mockOptionsBuilder = Substitute.For<IMqttOptionsBuilder>();
        _mockCoordinator = Substitute.For<IBrokerStateResetCoordinator>();

        _mockConfigMgr.Config.Returns(new AppConfiguration());
        _mockMsgStore.Start().Returns(Task.CompletedTask);
        _mockOptionsBuilder.Build(Arg.Any<Connection>()).Returns(
            new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(b => b.WithTcpServer("localhost"))
                .Build());

        _connectedHandler = null;
        _failedHandler = null;
        _mockClient
            .When(x => x.ConnectedAsync += Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(x => _connectedHandler = x.Arg<Func<MqttClientConnectedEventArgs, Task>>());
        _mockClient
            .When(x => x.ConnectingFailedAsync += Arg.Any<Func<ConnectingFailedEventArgs, Task>>())
            .Do(x => _failedHandler = x.Arg<Func<ConnectingFailedEventArgs, Task>>());

        Services.AddSingleton(_mockClient);
        Services.AddSingleton(_mockConfigMgr);
        Services.AddSingleton(_mockMsgStore);
        Services.AddSingleton(_mockSubMgr);
        Services.AddSingleton(_mockSessionState);
        Services.AddSingleton(_mockOptionsBuilder);
        Services.AddSingleton(_mockCoordinator);
        Services.AddSingleton(Substitute.For<ILogger<ConnectionDialog>>());
        Services.AddSingleton(Substitute.For<IChartDataService>());
        Services.AddSingleton(Substitute.For<IUxTelemetryService>());

        EnsureMudProviders();

        // MudDialog content renders via MudDialogProvider, so assertions should target its DOM.
        _dialogProvider = Render<MudDialogProvider>();
    }

    [TearDown]
    public void TeardownMocks() => _mockClient.Dispose();

    private async Task OpenDialog(AppConfiguration? config = null)
    {
        _mockConfigMgr.Config.Returns(config ?? new AppConfiguration());
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
    public async Task DeleteButton_IsDisabled_WhenNoConnectionSelected()
    {
        await OpenDialog();

        var deleteBtn = _dialogProvider.Find("button[title='Delete connection']");
        deleteBtn.GetAttribute("disabled").Should().NotBeNull();
    }

    [Test]
    public async Task ConnectButton_CallsStartAsync_OnManagedClient()
    {
        _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockClient.Received(1).StartAsync(Arg.Any<ManagedMqttClientOptions>());
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
    public async Task Connect_WithSubscribeToEverythingChecked_DoesNotCallSubscriptionManagerAdd()
    {
        _mockSubMgr.Add(Arg.Any<string>()).Returns(Task.CompletedTask);
        _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockSubMgr.DidNotReceive().Add(Arg.Any<string>());
    }

    [Test]
    public async Task ConnectedAsync_WithSubscribeToEverythingChecked_CallsSubscriptionManagerAdd()
    {
        _mockSubMgr.Add(Arg.Any<string>()).Returns(Task.CompletedTask);
        _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Connect']").Click();

        _connectedHandler.Should().NotBeNull();
        await _dialogProvider.InvokeAsync(() => _connectedHandler!(null!));

        await _mockSubMgr.Received(1).Add("spBv1.0/#");
    }

    [Test]
    public async Task ConnectedAsync_WithSubscribeToEverythingUnchecked_DoesNotCallSubscriptionManagerAdd()
    {
        _mockSubMgr.Add(Arg.Any<string>()).Returns(Task.CompletedTask);
        _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        // Toggle the "On Connect" subscription checkbox off (defaults to true).
        // The dialog has two checkbox inputs (Use TLS, Subscribe to Sparkplug B);
        // the Sparkplug checkbox is the one whose surrounding label mentions it.
        var checkboxes = _dialogProvider.FindAll("input[type='checkbox']").ToList();
        var sparkplugCheckbox = checkboxes
            .First(c => c.Closest("label")?.TextContent.Contains("Sparkplug B") == true);
        sparkplugCheckbox.Change(false);

        _dialogProvider.Find("button[title='Connect']").Click();
        await _dialogProvider.InvokeAsync(() => _connectedHandler!(null!));

        await _mockSubMgr.DidNotReceive().Add(Arg.Any<string>());
    }

    [Test]
    public async Task Connect_SetsSelectedConnection_BeforeCallingStartAsync()
    {
        // Regression: SessionState.SelectedConnection must be assigned BEFORE
        // _managedMqttClient.StartAsync is invoked. Otherwise the SubscriptionManager,
        // which subscribes to IManagedMqttClient.ConnectedAsync during app startup
        // (before this dialog opens), reads the default empty SelectedConnection in
        // its OnConnected handler and fails to re-subscribe to saved topics on
        // app restart. The dialog's OnConnected handler runs too late — by the time
        // it sets SelectedConnection, the SubscriptionManager has already read the
        // default value and given up.
        _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections =
            [
                new Connection
                {
                    Name = "TestConn",
                    Host = "localhost",
                    Port = 1883,
                    SubscribedTopics = ["saved/topic"]
                }
            ]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockClient.Received(1).StartAsync(Arg.Any<ManagedMqttClientOptions>());
        Received.InOrder(() =>
        {
            _mockSessionState.SelectedConnection = Arg.Is<Connection>(c => c.SubscribedTopics.Contains("saved/topic"));
            _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>());
        });
    }

    [Test]
    public async Task ConnectingFailed_DoesNotCallSubscriptionManagerAdd()
    {
        _mockSubMgr.Add(Arg.Any<string>()).Returns(Task.CompletedTask);
        _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Connect']").Click();

        await _dialogProvider.InvokeAsync(() =>
            _failedHandler!(new ConnectingFailedEventArgs(null, new Exception("refused"))));

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

        await _dialogProvider.InvokeAsync(() => _failedHandler!(new ConnectingFailedEventArgs(null, new Exception("refused"))));

        _dialogProvider.Markup.Should().Contain("Connection failed. Verify broker, credentials, and transport settings.");
    }

    [Test]
    public async Task Save_CallsConfigurationManagerAddConnectionAsync()
    {
        var conn = new Connection { Name = "Test", Host = "localhost", Port = 1883 };
        var cfg = new AppConfiguration { Connections = [conn] };
        _mockConfigMgr.Config.Returns(cfg);
        _mockConfigMgr.AddConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);

        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);
        DirtyNameField("Test Updated");

        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled").Should().BeNull();
        _dialogProvider.Find("button[title='Save connection']").Click();

        await _mockConfigMgr.Received(1).AddConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public async Task SaveButton_InvalidDirtyInput_StaysDisabled()
    {
        await OpenDialog(new AppConfiguration());

        DirtyNameField("My Broker");
        ActivateTab("Transport");
        SetTextField("Host", "localhost");
        _dialogProvider.Find("button[title='Save connection']").GetAttribute("disabled")
            .Should().BeNull("a dirty, valid form should allow saving");

        ActivateTab("Identity");
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

    private void FillNewConnection()
    {
        DirtyNameField("My Broker");
        ActivateTab("Transport");
        SetTextField("Host", "localhost");
    }

    private AngleSharp.Dom.IElement FindMessageBoxButton(string text) =>
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains(text));

    [Test]
    public async Task Connect_WithUnsavedChanges_ShowsSaveBeforeConnectPrompt()
    {
        await OpenDialog(new AppConfiguration());
        FillNewConnection();

        _dialogProvider.Find("button[title='Connect']").Click();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.Markup.Should().Contain("Unsaved changes"));
        await _mockClient.DidNotReceive().StartAsync(Arg.Any<ManagedMqttClientOptions>());
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

    [Test]
    public async Task SaveButton_Enables_WhenFreshDialogFilledOut_WithoutClickingAdd()
    {
        await OpenDialog(new AppConfiguration());

        DirtyNameField("My Broker");
        ActivateTab("Transport");
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
        ActivateTab("Transport");
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
        _mockConfigMgr.Config.Returns(cfg);
        _mockConfigMgr.RemoveConnectionAsync(Arg.Any<Connection>()).Returns(Task.CompletedTask);

        await OpenDialog(cfg);

        await SelectConnection(cfg.Connections[0]);
        _dialogProvider.Find("button[title='Delete connection']").Click();

        await _mockConfigMgr.Received(1).RemoveConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public async Task Renders_ThreeTabPanels_ForIdentityTransportAndOnConnect()
    {
        await OpenDialog(new AppConfiguration());

        var tabPanels = _dialogProvider.FindAll(".mud-tab-panel[role='tabpanel']");
        tabPanels.Should().HaveCount(3, "Identity, Transport, and On Connect panels are always rendered");
    }

    [Test]
    public async Task Connect_CallsResetCoordinatorBeforeStartAsync()
    {
        _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>()).Returns(Task.CompletedTask);
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
            _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>());
        });
    }

    [Test]
    public async Task Connect_PassesSelectedConnectionToCoordinator()
    {
        _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>()).Returns(Task.CompletedTask);
        var conn = new Connection { Name = "TestConn", Host = "broker.example.com", Port = 8883 };
        var cfg = new AppConfiguration { Connections = [conn] };
        await OpenDialog(cfg);
        await SelectConnection(conn);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockCoordinator.Received(1).ResetIfBrokerChangedAsync(
            Arg.Is<Connection>(c => c.Host == "broker.example.com" && c.Port == 8883));
    }

    [Test]
    public async Task Connect_WhenCoordinatorThrows_StillProceedsWithConnect()
    {
        _mockCoordinator.ResetIfBrokerChangedAsync(Arg.Any<Connection>())
            .Returns(Task.FromException(new Exception("reset failed")));
        _mockClient.StartAsync(Arg.Any<ManagedMqttClientOptions>()).Returns(Task.CompletedTask);
        var cfg = new AppConfiguration
        {
            Connections = [new Connection { Name = "TestConn", Host = "localhost", Port = 1883 }]
        };
        await OpenDialog(cfg);
        await SelectConnection(cfg.Connections[0]);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _mockClient.Received(1).StartAsync(Arg.Any<ManagedMqttClientOptions>());
    }

    [Test]
    public async Task StepSelector_RendersThreeStepButtons()
    {
        await OpenDialog(new AppConfiguration());

        var stepButtons = _dialogProvider.FindAll(".step-btn");
        stepButtons.Should().HaveCount(3);
        stepButtons[0].TextContent.Should().Contain("Identity");
        stepButtons[1].TextContent.Should().Contain("Transport");
        stepButtons[2].TextContent.Should().Contain("On Connect");
    }

    [Test]
    public async Task StepSelector_ClickTransport_ActivatesTransportButton()
    {
        await OpenDialog(new AppConfiguration());

        var stepButtons = _dialogProvider.FindAll(".step-btn");
        stepButtons[0].ClassName.Should().Contain("active", "Identity is default");

        stepButtons[1].Click();

        var refreshedButtons = _dialogProvider.FindAll(".step-btn");
        refreshedButtons[1].ClassName.Should().Contain("active", "Transport was clicked");
        refreshedButtons[0].ClassName.Should().NotContain("active", "Identity is no longer selected");
        _dialogProvider.Markup.Should().Contain("Protocol");
        _dialogProvider.Markup.Should().Contain("Host");
    }

    [Test]
    public async Task StepSelector_ClickOnConnect_ActivatesOnConnectButton()
    {
        await OpenDialog(new AppConfiguration());

        var stepButtons = _dialogProvider.FindAll(".step-btn");
        stepButtons[2].Click();

        var refreshedButtons = _dialogProvider.FindAll(".step-btn");
        refreshedButtons[2].ClassName.Should().Contain("active", "On Connect was clicked");
        refreshedButtons[0].ClassName.Should().NotContain("active", "Identity is no longer selected");
        _dialogProvider.Markup.Should().Contain("Sparkplug B");
    }

    [Test]
    public async Task StepSelector_IdentityIsDefaultStep()
    {
        await OpenDialog(new AppConfiguration());

        var identityBtn = _dialogProvider.FindAll(".step-btn")
            .First(b => b.TextContent.Contains("Identity"));
        identityBtn.ClassName.Should().Contain("active");
        _dialogProvider.Markup.Should().Contain("Name");
    }
}
