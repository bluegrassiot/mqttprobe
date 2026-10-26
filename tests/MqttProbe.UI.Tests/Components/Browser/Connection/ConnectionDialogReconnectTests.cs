using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Components.Browser.Connection;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class ConnectionDialogReconnectTests : BunitTestContext
{
    private static readonly TimeSpan _signalBound = TimeSpan.FromSeconds(10);

    private FakeManagedClient _client = null!;
    private IConnectionSettings _mockConnections = null!;
    private IUiSettings _mockUi = null!;
    private IMessageStoreManager _mockMsgStore = null!;
    private ISubscriptionManager _mockSubMgr = null!;
    private ITopicExcludeService _mockExcludeService = null!;
    private ISessionState _mockSessionState = null!;
    private IMqttOptionsBuilder _mockOptionsBuilder = null!;
    private IBrokerStateResetCoordinator _mockCoordinator = null!;
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;
    private ICertificateAssetStore _mockCertStore = null!;
    private ICertificateFilePicker _mockFilePicker = null!;
    private ICertificateInputCapability _mockInputCapability = null!;
    private IChartDataService _mockChart = null!;
    private IConnectionSessionLifecycle _mockSessionLifecycle = null!;
    private IUxMetricsService _mockUxMetrics = null!;

    private ManualResetEventSlim _rendererHeld = null!;
    private ManualResetEventSlim _eventScheduled = null!;
    private ManualResetEventSlim _invalidated = null!;
    private ManualResetEventSlim _releaseRenderer = null!;

    [SetUp]
    public void SetupMocks()
    {
        _client = new FakeManagedClient();
        _mockConnections = Substitute.For<IConnectionSettings>();
        _mockUi = Substitute.For<IUiSettings>();
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockSubMgr = Substitute.For<ISubscriptionManager>();
        _mockExcludeService = Substitute.For<ITopicExcludeService>();
        _mockExcludeService.TopicExcludes.Returns(Array.Empty<string>());
        _mockExcludeService.ValidateAdd(Arg.Any<string>()).Returns(new TopicExcludeValidationResult(true));
        _mockSessionState = Substitute.For<ISessionState>();
        // A substitute answers these with empty values, which the dialog reads as a retained asset.
        _mockSessionState.RetainedUnsavedAssetId.Returns((string?)null);
        _mockSessionState.RetainedUnsavedAssetOwnerId.Returns(Guid.Empty);
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
        _mockOptionsBuilder.BuildAsync(Arg.Any<Connection>(), Arg.Any<CertificateSessionResource>()).Returns(
            Task.FromResult(new MqttManagedClientOptions
            {
                ClientOptions = new MqttClientOptionsBuilder().WithTcpServer("localhost").Build()
            }));

        Services.AddSingleton<IMqttManagedClient>(_client);
        Services.AddConnectionSettings(_mockConnections);
        Services.AddUiSettings(_mockUi);
        Services.AddSingleton(_mockMsgStore);
        Services.AddSingleton(_mockSubMgr);
        Services.AddSingleton(_mockExcludeService);
        Services.AddSingleton(_mockSessionState);
        Services.AddSingleton(_mockOptionsBuilder);
        Services.AddSingleton(_mockCoordinator);
        Services.AddSingleton(Substitute.For<ILogger<ConnectionDialog>>());
        Services.AddSingleton(_mockChart);
        _mockUxMetrics = Substitute.For<IUxMetricsService>();
        Services.AddSingleton(_mockUxMetrics);
        _mockSessionLifecycle = Substitute.For<IConnectionSessionLifecycle>();
        _mockSessionLifecycle.StopActiveConnectionAsync().Returns(Task.CompletedTask);
        _mockSessionLifecycle.StopAttemptAsync(
            Arg.Any<MqttConnectAttemptId>(), Arg.Any<CertificateSessionResource?>()).Returns(Task.CompletedTask);
        Services.AddSingleton(_mockSessionLifecycle);
        Services.AddSingleton(_mockCertStore);
        Services.AddSingleton(_mockFilePicker);
        Services.AddSingleton(_mockInputCapability);

        _rendererHeld = new ManualResetEventSlim(false);
        _eventScheduled = new ManualResetEventSlim(false);
        _invalidated = new ManualResetEventSlim(false);
        _releaseRenderer = new ManualResetEventSlim(false);

        EnsureMudProviders();
        _dialogProvider = Render<MudDialogProvider>();
    }

    [TearDown]
    public void TeardownMocks()
    {
        _releaseRenderer.Set();
        _client.Dispose();
        _rendererHeld.Dispose();
        _eventScheduled.Dispose();
        _invalidated.Dispose();
        _releaseRenderer.Dispose();
    }

    private async Task OpenDialog(AppConfiguration config)
    {
        _mockConnections.Connections.Returns(config.Connections);
        _mockUi.Ui.Returns(config.Ui);
        var dialogService = Services.GetRequiredService<IDialogService>();
        await _dialogProvider.InvokeAsync(async () =>
            await dialogService.ShowAsync<ConnectionDialog>("Connection Setup"));
    }

    private Task SelectConnection(Connection conn) =>
        _dialogProvider.InvokeAsync(async () =>
            await _dialogProvider.FindComponent<ConnectionDialog>().Instance.ConnectionChanged(conn));

    private ConnectionDialog Dialog() => _dialogProvider.FindComponent<ConnectionDialog>().Instance;

    private Task FlushDispatcherAsync() => _dialogProvider.InvokeAsync(() => { });

    private void SetTextField(string label, string value) =>
        _dialogProvider.FindComponents<MudTextField<string>>()
            .First(f => f.Instance.Label == label)
            .Find("input").Input(value);

    private void ConnectAndWaitForPending()
    {
        _dialogProvider.Find("button[title='Connect']").Click();
        Dialog().IsConnectingForTests.Should().BeTrue(
            "clicking Connect on a valid saved connection starts a pending attempt");
    }

    private async Task FailPendingAttemptAsync()
    {
        var completion = _client.PendingCompletion
            ?? throw new InvalidOperationException("no connect attempt is pending");
        var id = _client.LiveAttemptId;
        // Captured before the outcome lands, because a reconciled reconnect closes the dialog.
        var dialog = Dialog();
        var handledBefore = dialog.HandledOutcomeCountForTests;

        await _dialogProvider.InvokeAsync(() =>
            completion.TrySetResult(new MqttConnectOutcome(id, MqttConnectStatus.Failed, new InvalidOperationException("refused"))));
        await FlushDispatcherAsync();

        dialog.HandledOutcomeCountForTests.Should().BeGreaterThan(handledBefore,
            "the dialog should have processed the failed attempt outcome");
    }

    // A synchronous work item keeps the renderer busy, so a notification raised elsewhere queues.
    private Task HoldRendererAsync(Func<Task>? invalidateOnRenderer = null)
    {
        var held = Task.Run(() => _dialogProvider.InvokeAsync(() =>
        {
            _rendererHeld.Set();
            WaitFor(_eventScheduled, "the queued connected notification");
            if (invalidateOnRenderer is not null)
                invalidateOnRenderer().Wait(_signalBound);
            _invalidated.Set();
            _releaseRenderer.Wait(_signalBound);
        }));

        WaitFor(_rendererHeld, "a held renderer work item");
        return held;
    }

    private Task ScheduleConnectedEventAsync()
    {
        var scheduled = Task.CompletedTask;
        var worker = Task.Run(() =>
        {
            scheduled = _client.RaiseConnectedAsync();
            _eventScheduled.Set();
        });

        WaitFor(_eventScheduled, "the connected listener to queue its callback");
        return Task.WhenAll(worker, scheduled);
    }

    private static void WaitFor(ManualResetEventSlim signal, string what)
    {
        if (!signal.Wait(_signalBound))
            throw new TimeoutException($"timed out after {_signalBound} waiting for {what}");
    }

    [Test]
    public async Task ConnectAttempt_SetsChartConnectionBeforeStartingCollection()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();

        Received.InOrder(() =>
        {
            _mockChart.SetConnection(conn.Id);
            _mockChart.StartAsync();
        });
    }

    [Test]
    public async Task CanceledConnectDuringMessageStoreStartup_DoesNotBindOrStartChartCollection()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        var messageStoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockMsgStore.Start().Returns(messageStoreStarted.Task);
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        var connect = _dialogProvider.Find("button[title='Connect']").ClickAsync();
        await _dialogProvider.InvokeAsync(() => Task.CompletedTask);
        Dialog().IsConnectingForTests.Should().BeTrue(
            "clicking Connect on a valid saved connection starts a pending attempt");
        await _mockMsgStore.Received(1).Start();

        _dialogProvider.Find(".connection-dialog-content").KeyDown(key: "Escape");
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty("closing cancels the pending startup"));

        messageStoreStarted.TrySetResult();
        await connect.WaitAsync(_signalBound);

        _mockChart.DidNotReceive().SetConnection(Arg.Any<Guid>());
        await _mockChart.DidNotReceive().StartAsync();
    }

    [Test]
    public async Task CanceledConnectDuringChartStartup_StopsTheChartCollectionItStarted()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        var chartStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockChart.StartAsync().Returns(chartStarted.Task);
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        var connect = _dialogProvider.Find("button[title='Connect']").ClickAsync();
        await _dialogProvider.InvokeAsync(() => Task.CompletedTask);
        Dialog().IsConnectingForTests.Should().BeTrue(
            "clicking Connect on a valid saved connection starts a pending attempt");
        _dialogProvider.WaitForAssertion(() => _mockChart.Received(1).StartAsync());

        _dialogProvider.Find(".connection-dialog-content").KeyDown(key: "Escape");
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty("closing cancels the pending startup"));

        chartStarted.TrySetResult();
        await connect.WaitAsync(_signalBound);

        await _mockChart.Received(1).StopAsync();
        _client.StartedAttempts.Should().BeEmpty("no MQTT attempt starts after the canceled startup");
    }

    [Test]
    public async Task CanceledConnectDuringChartStartup_LeavesAlreadyListeningCollectionRunning()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        var chartStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockChart.IsListening.Returns(true);
        _mockChart.StartAsync().Returns(chartStarted.Task);
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        var connect = _dialogProvider.Find("button[title='Connect']").ClickAsync();
        await _dialogProvider.InvokeAsync(() => Task.CompletedTask);
        _dialogProvider.WaitForAssertion(() => _mockChart.Received(1).StartAsync());

        _dialogProvider.Find(".connection-dialog-content").KeyDown(key: "Escape");
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty("closing cancels the pending startup"));

        chartStarted.TrySetResult();
        await connect.WaitAsync(_signalBound);

        await _mockChart.DidNotReceive().StopAsync();
        _client.StartedAttempts.Should().BeEmpty("no MQTT attempt starts after the canceled startup");
    }

    [Test]
    public async Task FailedAttempt_ThenReconnectSucceeds_ClosesDialogAndRecordsSuccessOnce()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();
        await FailPendingAttemptAsync();

        _dialogProvider.FindAll(".mud-dialog").Should().NotBeEmpty(
            "a retrying attempt keeps the dialog open");
        Dialog().IsConnectingForTests.Should().BeTrue(
            "a retrying attempt keeps the connecting affordance and its Stop action");
        _mockUxMetrics.Received(1).RecordConnectFailure();
        _mockSessionState.DidNotReceive().LastSuccessfulConnectionId = Arg.Any<Guid?>();

        var raising = _client.RaiseConnectedAsync();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty(
                "a successful reconnect closes the dialog"));
        await raising;

        _mockSessionState.Received(1).LastSuccessfulConnectionId = conn.Id;
        _mockUxMetrics.Received(1).RecordConnectSuccess();
        _mockUxMetrics.Received(1).RecordConnectFailure();
        _client.ConnectedSubscriberCount.Should().Be(0,
            "the released operation stops listening for connect notifications");
    }

    [Test]
    public async Task FailedAttempt_ThenReconnectSucceeds_RaisedTwice_RecordsSuccessOnce()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();
        await FailPendingAttemptAsync();

        await _client.RaiseConnectedAsync();
        await FlushDispatcherAsync();
        await _client.RaiseConnectedAsync();
        await FlushDispatcherAsync();

        _mockUxMetrics.Received(1).RecordConnectSuccess();
        _mockSessionState.Received(1).LastSuccessfulConnectionId = conn.Id;
    }

    [Test]
    public async Task FailedAttempt_WithStagedCertificate_ConnectAnywayReconnectSuccess_DeletesStagedAsset()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        _mockCertStore.ImportAsync(conn.Id, Arg.Any<CertificateImportRequest>()).Returns("staged-asset-id");
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);
        var dialog = Dialog();
        await _dialogProvider.InvokeAsync(() => dialog.SetStagedCertificate([1, 2, 3], null, null, ""));

        _dialogProvider.Find("button[title='Connect']").Click();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.Markup.Should().Contain("Unsaved changes"));
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Connect anyway")).Click();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _client.StartedAttempts.Should().HaveCount(1, "Connect anyway starts the attempt"));

        await FailPendingAttemptAsync();
        await _client.RaiseConnectedAsync();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty("a successful reconnect closes the dialog"));

        await _mockCertStore.Received(1).DeleteAsync(conn.Id, "staged-asset-id");
        _mockSessionState.DidNotReceive().RetainedUnsavedAssetId = Arg.Is<string>(s => s != null);
    }

    [Test]
    public async Task Dispose_WhileAwaitingReconnect_DeletesUnsavedStagedAssetOnce()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        _mockCertStore.ImportAsync(conn.Id, Arg.Any<CertificateImportRequest>()).Returns("staged-asset-id");
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);
        var dialog = Dialog();
        await _dialogProvider.InvokeAsync(() => dialog.SetStagedCertificate([1, 2, 3], null, null, ""));

        _dialogProvider.Find("button[title='Connect']").Click();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.Markup.Should().Contain("Unsaved changes"));
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Connect anyway")).Click();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _client.StartedAttempts.Should().HaveCount(1, "Connect anyway starts the attempt"));
        await FailPendingAttemptAsync();

        await _dialogProvider.InvokeAsync(async () => await dialog.DisposeAsync());
        await FlushDispatcherAsync();

        await _mockCertStore.Received(1).DeleteAsync(conn.Id, "staged-asset-id");
        await _mockSessionLifecycle.Received(1).StopAttemptAsync(
            _client.LiveAttemptId, Arg.Any<CertificateSessionResource?>());
        _client.ConnectedSubscriberCount.Should().Be(0,
            "disposal releases the reconnect listener");
        _mockSessionState.DidNotReceive().LastSuccessfulConnectionId = Arg.Any<Guid?>();
    }

    [Test]
    public async Task FailedAttempt_ThenStop_IgnoresLateReconnectSuccess()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();
        await FailPendingAttemptAsync();
        _mockSessionLifecycle.ClearReceivedCalls();

        _dialogProvider.Find("[data-testid=cancel-connect]").Click();
        await FlushDispatcherAsync();

        Dialog().IsConnectingForTests.Should().BeFalse("Stop ends the retrying attempt");
        await _mockSessionLifecycle.Received(1).StopAttemptAsync(
            _client.LiveAttemptId, Arg.Any<CertificateSessionResource?>());

        await _client.RaiseConnectedAsync();
        await FlushDispatcherAsync();

        _dialogProvider.FindAll(".mud-dialog").Should().NotBeEmpty(
            "a connect that lands after Stop must not close the dialog");
        _mockSessionState.DidNotReceive().LastSuccessfulConnectionId = Arg.Any<Guid?>();
        _mockUxMetrics.DidNotReceive().RecordConnectSuccess();
        _client.ConnectedSubscriberCount.Should().Be(0, "Stop releases the reconnect listener");
    }

    [Test]
    public async Task FailedAttempt_ThenClose_IgnoresLateReconnectSuccess()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();
        await FailPendingAttemptAsync();

        _dialogProvider.Find(".connection-dialog-content").KeyDown(key: "Escape");
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty("closing while retrying closes the dialog"));

        await _client.RaiseConnectedAsync();
        await FlushDispatcherAsync();

        _mockSessionState.DidNotReceive().LastSuccessfulConnectionId = Arg.Any<Guid?>();
        _mockUxMetrics.DidNotReceive().RecordConnectSuccess();
        _client.ConnectedSubscriberCount.Should().Be(0, "closing releases the reconnect listener");
    }

    [Test]
    public async Task FailedAttempt_ThenAnotherGenerationConnects_IsIgnoredAndReleasesTheAttempt()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();
        await FailPendingAttemptAsync();

        _client.ReplaceLiveAttempt();

        await _client.RaiseConnectedAsync();
        await FlushDispatcherAsync();

        _dialogProvider.FindAll(".mud-dialog").Should().NotBeEmpty(
            "a connect owned by a different generation must not finalize this attempt");
        _mockSessionState.DidNotReceive().LastSuccessfulConnectionId = Arg.Any<Guid?>();
        _mockUxMetrics.DidNotReceive().RecordConnectSuccess();
        Dialog().IsConnectingForTests.Should().BeFalse(
            "an attempt whose generation is gone is released rather than left retrying");
        _client.ConnectedSubscriberCount.Should().Be(0,
            "the released attempt stops listening for the next generation");
    }

    [Test]
    public async Task QueuedConnectedCallback_AfterSupersededGeneration_DoesNotFinalize()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();
        await FailPendingAttemptAsync();

        var held = HoldRendererAsync();
        var scheduled = ScheduleConnectedEventAsync();
        _client.ReplaceLiveAttempt();
        // Connected, so only the attempt identity can reject the queued callback.
        _client.MarkConnected();
        _invalidated.Set();
        _releaseRenderer.Set();
        await held;
        await scheduled;
        await FlushDispatcherAsync();

        _mockUxMetrics.DidNotReceive().RecordConnectSuccess();
        _mockSessionState.DidNotReceive().LastSuccessfulConnectionId = Arg.Any<Guid?>();
        _dialogProvider.FindAll(".mud-dialog").Should().NotBeEmpty();
        _dialogProvider.FindAll("button[title='Connect']").Should().HaveCount(1,
            "an attempt whose generation is gone is released so the user can start another");
    }

    [Test]
    public async Task QueuedConnectedCallback_AfterStop_DoesNotFinalizeAndLeavesAUsableDialog()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();
        await FailPendingAttemptAsync();
        var dialog = Dialog();

        var held = HoldRendererAsync(() => dialog.CancelConnect());
        var scheduled = ScheduleConnectedEventAsync();
        WaitFor(_invalidated, "Stop to release the queued attempt");
        _releaseRenderer.Set();
        await held;
        await scheduled;
        await FlushDispatcherAsync();

        _mockUxMetrics.DidNotReceive().RecordConnectSuccess();
        _dialogProvider.FindAll(".mud-dialog").Should().NotBeEmpty();
        dialog.IsConnectingForTests.Should().BeFalse();

        ConnectAndWaitForPending();
        await _client.RaiseConnectedAsync();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty(
                "the stale callback left the dialog able to connect again"));

        _mockUxMetrics.Received(1).RecordConnectSuccess();
        _mockSessionState.Received(1).LastSuccessfulConnectionId = conn.Id;
    }

    [Test]
    public async Task QueuedConnectedCallback_TransientDropThenSameGenerationReconnect_ClosesOnce()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();
        await FailPendingAttemptAsync();
        var dialog = Dialog();

        var held = HoldRendererAsync();
        var scheduled = ScheduleConnectedEventAsync();
        _client.Disconnect();
        _invalidated.Set();
        _releaseRenderer.Set();
        await held;
        await scheduled;
        await FlushDispatcherAsync();

        _mockUxMetrics.DidNotReceive().RecordConnectSuccess();
        _dialogProvider.FindAll(".mud-dialog").Should().NotBeEmpty(
            "a link drop under a live generation must not abandon the attempt");
        dialog.IsConnectingForTests.Should().BeTrue(
            "the attempt keeps waiting on a generation that is still retrying");
        _client.ConnectedSubscriberCount.Should().Be(1, "the retrying attempt keeps its listener");

        await _client.RaiseConnectedAsync();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty(
                "the same generation reconnecting closes the dialog"));

        _mockUxMetrics.Received(1).RecordConnectSuccess();
        _mockSessionState.Received(1).LastSuccessfulConnectionId = conn.Id;
    }

    [Test]
    public async Task ReconnectWinsBeforeTheAttemptIsObserved_FailedOutcomeStillClosesAndRecordsOnce()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        _client.ConnectedBeforeNextAttemptReturns = true;
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        _dialogProvider.Find("button[title='Connect']").Click();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty(
                "a connect that already won closes the dialog without waiting for a notification"));
        _mockSessionState.Received(1).LastSuccessfulConnectionId = conn.Id;
        _mockUxMetrics.Received(1).RecordConnectSuccess();
    }

    [Test]
    public async Task FailedOutcome_AfterTheSameAttemptAlreadyConnected_ClosesInsteadOfWaiting()
    {
        var conn = new Connection { Name = "TestConn", Host = "localhost", Port = 1883 };
        await OpenDialog(new AppConfiguration { Connections = [conn] });
        await SelectConnection(conn);

        ConnectAndWaitForPending();
        _client.MarkConnected();
        await FailPendingAttemptAsync();

        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty(
                "a failed outcome for an attempt that already connected must not wait for a notification"));
        _mockSessionState.Received(1).LastSuccessfulConnectionId = conn.Id;
        _mockUxMetrics.Received(1).RecordConnectSuccess();
    }

    [Test]
    public async Task UnsavedStagedCertificate_FailThenReconnectSuccess_TransfersOwnershipAndKeepsAsset()
    {
        _mockCertStore.ImportAsync(Arg.Any<Guid>(), Arg.Any<CertificateImportRequest>())
            .Returns("unsaved-asset-id");
        await OpenDialog(new AppConfiguration());
        var dialog = Dialog();
        SetTextField("Name", "Unsaved Broker");
        SetTextField("Host", "localhost");
        await _dialogProvider.InvokeAsync(() => dialog.SetStagedCertificate([1, 2, 3], null, null, ""));
        var connectionId = dialog.SelectedConnectionForTests.Id;

        _dialogProvider.Find("button[title='Connect']").Click();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.Markup.Should().Contain("Unsaved changes"));
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Connect anyway")).Click();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _client.StartedAttempts.Should().HaveCount(1, "Connect anyway starts the attempt"));

        await FailPendingAttemptAsync();
        await _client.RaiseConnectedAsync();
        await _dialogProvider.WaitForAssertionAsync(() =>
            _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty("a successful reconnect closes the dialog"));

        await _mockConnections.DidNotReceive().AddConnectionAsync(Arg.Any<Connection>());
        _mockSessionState.Received(1).LastSuccessfulConnectionSnapshot =
            Arg.Is<Connection>(c => c.Id == connectionId && c.ClientCertificateAssetId == "unsaved-asset-id");
        _mockSessionState.Received(1).LastSuccessfulConnectionId = Arg.Is<Guid?>(id => id == null);
        _mockSessionState.Received(1).RetainedUnsavedAssetId = "unsaved-asset-id";
        _mockSessionState.Received(1).RetainedUnsavedAssetOwnerId = connectionId;
        _mockUxMetrics.Received(1).RecordConnectSuccess();
        await _mockCertStore.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>());

        _mockSessionLifecycle.ClearReceivedCalls();
        await _dialogProvider.InvokeAsync(async () => await dialog.DisposeAsync());
        await FlushDispatcherAsync();

        await _mockCertStore.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _mockSessionLifecycle.DidNotReceive()
            .StopAttemptAsync(Arg.Any<MqttConnectAttemptId>(), Arg.Any<CertificateSessionResource?>());
        await _mockSessionLifecycle.DidNotReceive().StopActiveConnectionAsync();
    }

    private sealed class FakeManagedClient : IMqttManagedClient
    {
        private Func<MqttClientConnectedEventArgs, Task>? _connected;
        private MqttConnectAttemptId _liveAttemptId;
        private long _attemptCounter;

        public List<MqttConnectAttemptId> StartedAttempts { get; } = [];

        public MqttConnectAttemptId LiveAttemptId => _liveAttemptId;

        public TaskCompletionSource<MqttConnectOutcome>? PendingCompletion { get; private set; }

        public bool IsConnected { get; private set; }

        public bool ConnectedBeforeNextAttemptReturns { get; set; }

        public int ConnectedSubscriberCount => _connected?.GetInvocationList().Length ?? 0;

        public bool IsStarted => StartedAttempts.Count > 0;

        public bool IsCurrentAttempt(MqttConnectAttemptId attemptId) =>
            !attemptId.IsNone && attemptId == _liveAttemptId;

        public MqttConnectAttempt StartAttempt(
            MqttManagedClientOptions options, CancellationToken cancellationToken = default)
        {
            _liveAttemptId = new MqttConnectAttemptId(++_attemptCounter);
            StartedAttempts.Add(_liveAttemptId);
            PendingCompletion = new TaskCompletionSource<MqttConnectOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            IsConnected = false;
            if (ConnectedBeforeNextAttemptReturns)
            {
                ConnectedBeforeNextAttemptReturns = false;
                IsConnected = true;
                PendingCompletion.TrySetResult(
                    new MqttConnectOutcome(_liveAttemptId, MqttConnectStatus.Failed,
                        new InvalidOperationException("initial connect refused")));
            }

            return new MqttConnectAttempt(_liveAttemptId, PendingCompletion.Task);
        }

        public void ReplaceLiveAttempt()
        {
            _liveAttemptId = new MqttConnectAttemptId(++_attemptCounter);
            IsConnected = false;
        }

        public void MarkConnected() => IsConnected = true;

        public void Disconnect() => IsConnected = false;

        public Task RaiseConnectedAsync()
        {
            IsConnected = true;
            var handler = _connected;
            return handler is null
                ? Task.CompletedTask
                : handler(new MqttClientConnectedEventArgs(new MqttClientConnectResult()));
        }

        public Task StartAsync(MqttManagedClientOptions options, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _liveAttemptId = MqttConnectAttemptId.None;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task StopAttemptAsync(MqttConnectAttemptId attemptId, CancellationToken cancellationToken = default)
        {
            if (attemptId == _liveAttemptId)
                _liveAttemptId = MqttConnectAttemptId.None;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(
            IEnumerable<MqttTopicFilter> topicFilters, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnqueueAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync
        {
            add => _connected += value;
            remove => _connected -= value;
        }

        public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync
        {
            add { }
            remove { }
        }

        public event Func<MqttConnectingFailedEventArgs, Task>? ConnectingFailedAsync
        {
            add { }
            remove { }
        }

        public event Func<EventArgs, Task>? ConnectionStateChangedAsync
        {
            add { }
            remove { }
        }

        public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync
        {
            add { }
            remove { }
        }

        public event Func<MqttManagedProcessFailedEventArgs, Task>? SynchronizingSubscriptionsFailedAsync
        {
            add { }
            remove { }
        }

        public event Func<MqttConnectOutcome, Task>? ConnectAttemptCompletedAsync
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
