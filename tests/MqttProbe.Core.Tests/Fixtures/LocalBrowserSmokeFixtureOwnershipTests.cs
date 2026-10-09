using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.Core.Tests.Fixtures;

[TestFixture]
public sealed class LocalBrowserSmokeFixtureOwnershipTests
{
    private static readonly TimeSpan _cleanupTimeout = TimeSpan.FromMilliseconds(100);

    [TestCase(SetupStage.PlaywrightCreation)]
    [TestCase(SetupStage.BrowserLaunch)]
    [TestCase(SetupStage.ContextCreation)]
    [TestCase(SetupStage.PageCreation)]
    public async Task ProvisionLocalAccountAsync_CancellationDuringPendingStage_ReleasesLateResource(SetupStage stage)
    {
        var harness = new FixtureHarness(stage);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var setup = harness.Fixture.ProvisionLocalAccountAsync(cancellation.Token);
            await harness.StageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            Func<Task> setupAction = () => setup;
            await setupAction.Should().ThrowAsync<InvalidOperationException>()
                .WaitAsync(TimeSpan.FromSeconds(2));

            harness.LaunchCount.Should().Be((int)stage >= (int)SetupStage.BrowserLaunch ? 1 : 0);
            harness.ContextCount.Should().Be((int)stage >= (int)SetupStage.ContextCreation ? 1 : 0);
            harness.PageCount.Should().Be((int)stage >= (int)SetupStage.PageCreation ? 1 : 0);

            harness.CompletePendingStage();
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            harness.BrowserCloseCount.Should().Be(stage == SetupStage.PlaywrightCreation ? 0 : 1);
            harness.PlaywrightDisposeCount.Should().Be(1);
        }
        finally
        {
            harness.CompletePendingStage();
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            harness.DeleteOwnedRoot();
        }
    }

    [Test]
    public async Task DisposeAsync_PendingCloseAndBrokerDisposal_RetriesWithoutDuplicateRelease()
    {
        var harness = new FixtureHarness(SetupStage.Navigation);
        var close = NewCompletionSource();
        var brokerDisposal = NewCompletionSource();
        harness.SetPendingClose(close.Task);
        harness.SetPendingBrokerDisposal(brokerDisposal.Task);
        using var cancellation = new CancellationTokenSource();

        try
        {
            await harness.BeginCanceledNavigationCleanupAsync(cancellation);

            Func<Task> firstDispose = harness.DisposeAndExpireBrokerDeadlineAsync;
            await firstDispose.Should().ThrowAsync<InvalidOperationException>();
            Func<Task> secondDispose = harness.DisposeAndExpireBrokerDeadlineAsync;
            await secondDispose.Should().ThrowAsync<InvalidOperationException>();

            harness.BrowserCloseCount.Should().Be(1);
            harness.BrokerDisposeCount.Should().Be(1);

            close.TrySetResult();
            brokerDisposal.TrySetResult();
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            harness.BrowserCloseCount.Should().Be(1);
            harness.BrokerDisposeCount.Should().Be(1);
            Directory.Exists(harness.Root).Should().BeFalse();
        }
        finally
        {
            close.TrySetResult();
            brokerDisposal.TrySetResult();
            harness.CompletePendingStage();
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Dispose();
            harness.DeleteOwnedRoot();
        }
    }

    [Test]
    public async Task DisposeAsync_ReleaseFailureAndCanceledRetry_DoesNotOverlapReleaseAttempts()
    {
        var harness = new FixtureHarness(SetupStage.Navigation);
        var close = NewCompletionSource();
        var closeCalls = 0;
        harness.Browser.CloseAsync().Returns(_ => ++closeCalls == 1 ? close.Task : Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();

        try
        {
            await harness.BeginCanceledNavigationCleanupAsync(cancellation);
            Func<Task> disposeWithDeadline = harness.DisposeAndExpireBrokerDeadlineAsync;
            await disposeWithDeadline.Should().ThrowAsync<InvalidOperationException>();
            harness.BrowserCloseCount.Should().Be(1);

            close.TrySetException(new IOException("release failed"));
            try
            {
                await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (InvalidOperationException)
            {
                // The completed close failure can surface before the next retry starts.
            }

            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            harness.BrowserCloseCount.Should().Be(2);
        }
        finally
        {
            close.TrySetResult();
            harness.CompletePendingStage();
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Dispose();
            harness.DeleteOwnedRoot();
        }
    }

    [Test]
    public async Task DisposeAsync_PendingBrowserCleanup_StillCleansWebAndBrokerAndRetainsConfiguration()
    {
        var harness = new FixtureHarness(SetupStage.Navigation);
        var close = NewCompletionSource();
        harness.SetPendingClose(close.Task);
        using var cancellation = new CancellationTokenSource();

        try
        {
            await harness.BeginCanceledNavigationCleanupAsync(cancellation);
            Func<Task> disposeWithDeadline = harness.DisposeAndExpireBrokerDeadlineAsync;
            await disposeWithDeadline.Should().ThrowAsync<InvalidOperationException>();

            harness.WebCleanupCount.Should().Be(1);
            harness.BrokerDisposeCount.Should().Be(1);
            Directory.Exists(harness.Root).Should().BeTrue();
            File.Exists(Path.Combine(harness.Root, "fixture-config.json")).Should().BeTrue();

            close.TrySetResult();
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Directory.Exists(harness.Root).Should().BeFalse();
            harness.WebCleanupCount.Should().Be(2);
            harness.BrokerDisposeCount.Should().Be(1);
        }
        finally
        {
            close.TrySetResult();
            harness.CompletePendingStage();
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Dispose();
            harness.DeleteOwnedRoot();
        }
    }

    [Test]
    public async Task ProvisionLocalAccountAsync_CancellationDuringNavigation_DoesNotFillOrSubmitAfterNavigationCompletes()
    {
        var harness = new FixtureHarness(SetupStage.Navigation);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var setup = harness.Fixture.ProvisionLocalAccountAsync(cancellation.Token);
            await harness.StageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Func<Task> setupAction = () => setup;
            await setupAction.Should().ThrowAsync<InvalidOperationException>()
                .WaitAsync(TimeSpan.FromSeconds(2));

            harness.CompletePendingStage();
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            harness.Locator.ReceivedCalls()
                .Count(call => call.GetMethodInfo().Name == nameof(ILocator.FillAsync)).Should().Be(0);
            harness.Locator.ReceivedCalls()
                .Count(call => call.GetMethodInfo().Name == nameof(ILocator.ClickAsync)).Should().Be(0);
        }
        finally
        {
            harness.CompletePendingStage();
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            harness.DeleteOwnedRoot();
        }
    }

    [Test]
    public async Task ProvisionLocalAccountAsync_FailureWithSensitiveDetails_ReportsOnlySanitizedFailure()
    {
        const string marker = "password-marker-not-for-diagnostics";
        var harness = new FixtureHarness();
        harness.CreatePlaywright().Returns(_ => Task.FromException<IPlaywright>(new Exception(marker)));

        try
        {
            Func<Task> setupAction = () => harness.Fixture.ProvisionLocalAccountAsync(CancellationToken.None);
            var error = await setupAction.Should().ThrowAsync<InvalidOperationException>();

            error.Which.ToString().Should().NotContain(marker);
        }
        finally
        {
            await harness.Fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            harness.DeleteOwnedRoot();
        }
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public enum SetupStage
    {
        PlaywrightCreation,
        BrowserLaunch,
        ContextCreation,
        PageCreation,
        Navigation,
    }

    private sealed class FixtureHarness
    {
        private readonly TaskCompletionSource<IPlaywright> _playwrightResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IBrowser> _browserResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IBrowserContext> _contextResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IPage> _pageResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IResponse?> _navigationResult =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SetupStage? _pendingStage;

        public FixtureHarness(SetupStage? pendingStage = null)
        {
            _pendingStage = pendingStage;
            Root = Path.Combine(
                Path.GetTempPath(),
                $"mqttprobe-browser-fixture-tests-{Guid.NewGuid():N}",
                "fixture");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "fixture-config.json"), "owned fixture configuration");

            Playwright = Substitute.For<IPlaywright>();
            BrowserType = Substitute.For<IBrowserType>();
            Browser = Substitute.For<IBrowser>();
            Context = Substitute.For<IBrowserContext>();
            Page = Substitute.For<IPage>();
            Locator = Substitute.For<ILocator>();
            Broker = Substitute.For<IContainer>();

            Playwright.Chromium.Returns(BrowserType);
            BrowserType.LaunchAsync(Arg.Any<BrowserTypeLaunchOptions>()).Returns(_ =>
                pendingStage == SetupStage.BrowserLaunch ? SignalAndReturn(_browserResult.Task) : Task.FromResult(Browser));
            Browser.NewContextAsync(Arg.Any<BrowserNewContextOptions>()).Returns(_ =>
                pendingStage == SetupStage.ContextCreation ? SignalAndReturn(_contextResult.Task) : Task.FromResult(Context));
            Context.NewPageAsync().Returns(_ =>
                pendingStage == SetupStage.PageCreation ? SignalAndReturn(_pageResult.Task) : Task.FromResult(Page));
            Page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(_ =>
                pendingStage == SetupStage.Navigation ? SignalAndReturn(_navigationResult.Task) : Task.FromResult<IResponse?>(null));
            Page.Locator(Arg.Any<string>()).Returns(Locator);
            Locator.FillAsync(Arg.Any<string>(), Arg.Any<LocatorFillOptions>()).Returns(Task.CompletedTask);
            Locator.ClickAsync(Arg.Any<LocatorClickOptions>()).Returns(Task.CompletedTask);
            Locator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);
            Browser.CloseAsync().Returns(_ =>
            {
                BrowserCloseStarted.TrySetResult();
                return Task.CompletedTask;
            });
            CreatePlaywright = Substitute.For<Func<Task<IPlaywright>>>();
            CreatePlaywright().Returns(_ => pendingStage == SetupStage.PlaywrightCreation
                ? SignalAndReturn(_playwrightResult.Task)
                : Task.FromResult(Playwright));
            VerifyListener = Substitute.For<Func<CancellationToken, Task>>();
            VerifyListener(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            WebCleanup = Substitute.For<Func<Task<string?>>>();
            WebCleanup().Returns(Task.FromResult<string?>(null));
            Broker.DisposeAsync().Returns(_ =>
            {
                BrokerDisposalStarted.TrySetResult();
                return ValueTask.CompletedTask;
            });

            Clock = new FakeTimeProvider();

            Fixture = new LocalBrowserSmokeFixture(
                Root,
                Broker,
                CreatePlaywright,
                VerifyListener,
                _cleanupTimeout,
                Clock,
                WebCleanup);
        }

        public string Root { get; }
        public LocalBrowserSmokeFixture Fixture { get; }
        public IPlaywright Playwright { get; }
        public IBrowserType BrowserType { get; }
        public IBrowser Browser { get; }
        public IBrowserContext Context { get; }
        public IPage Page { get; }
        public ILocator Locator { get; }
        public IContainer Broker { get; }
        public Func<Task<IPlaywright>> CreatePlaywright { get; }
        public Func<CancellationToken, Task> VerifyListener { get; }
        public Func<Task<string?>> WebCleanup { get; }
        public FakeTimeProvider Clock { get; }
        public TaskCompletionSource StageStarted { get; } = NewCompletionSource();
        public TaskCompletionSource BrowserCloseStarted { get; } = NewCompletionSource();
        public TaskCompletionSource BrokerDisposalStarted { get; } = NewCompletionSource();
        public int LaunchCount => BrowserType.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IBrowserType.LaunchAsync));
        public int ContextCount => Browser.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IBrowser.NewContextAsync));
        public int PageCount => Context.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IBrowserContext.NewPageAsync));
        public int BrowserCloseCount => Browser.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IBrowser.CloseAsync));
        public int PlaywrightDisposeCount => Playwright.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IPlaywright.Dispose));
        public int BrokerDisposeCount => Broker.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));
        public int WebCleanupCount => WebCleanup.ReceivedCalls().Count();

        public async Task BeginCanceledNavigationCleanupAsync(CancellationTokenSource cancellation)
        {
            var setup = Fixture.ProvisionLocalAccountAsync(cancellation.Token);
            await StageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Func<Task> setupAction = () => setup;
            await setupAction.Should().ThrowAsync<InvalidOperationException>().WaitAsync(TimeSpan.FromSeconds(2));
            CompletePendingStage();
            await BrowserCloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public void SetPendingClose(Task closeTask)
        {
            Browser.CloseAsync().Returns(_ =>
            {
                BrowserCloseStarted.TrySetResult();
                return closeTask;
            });
        }

        public void SetPendingBrokerDisposal(Task disposalTask)
        {
            Broker.DisposeAsync().Returns(_ =>
            {
                BrokerDisposalStarted.TrySetResult();
                return new ValueTask(disposalTask);
            });
        }

        public async Task DisposeAndExpireBrokerDeadlineAsync()
        {
            var dispose = Fixture.DisposeAsync().AsTask();
            Clock.Advance(_cleanupTimeout);
            await BrokerDisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Clock.Advance(_cleanupTimeout);
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public void CompletePendingStage()
        {
            switch (_pendingStage)
            {
                case SetupStage.PlaywrightCreation:
                    _playwrightResult.TrySetResult(Playwright);
                    break;
                case SetupStage.BrowserLaunch:
                    _browserResult.TrySetResult(Browser);
                    break;
                case SetupStage.ContextCreation:
                    _contextResult.TrySetResult(Context);
                    break;
                case SetupStage.PageCreation:
                    _pageResult.TrySetResult(Page);
                    break;
                case SetupStage.Navigation:
                    _navigationResult.TrySetResult(null);
                    break;
            }
        }

        public void DeleteOwnedRoot()
        {
            var ownedParent = Path.GetDirectoryName(Root);
            if (ownedParent is not null && Directory.Exists(ownedParent))
                Directory.Delete(ownedParent, recursive: true);
        }

        private async Task<T> SignalAndReturn<T>(Task<T> pending)
        {
            StageStarted.TrySetResult();
            return await pending;
        }

        private async Task SignalAndReturn(Task pending)
        {
            StageStarted.TrySetResult();
            await pending;
        }
    }
}
