using System.Security.Cryptography;
using Microsoft.Playwright;

namespace MqttProbe.TestInfrastructure.Fixtures;

public sealed partial class LocalBrowserSmokeFixture
{
    internal async Task ProvisionLocalAccountAsync(CancellationToken cancellationToken)
    {
        Username = $"smoke-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
        Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _setupTask = RunProvisioningAsync(cancellationToken);
        ObserveFault(_setupTask);
        try
        {
            await _setupTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var evidenceTask = StartFailureEvidenceCapture();
            StartBrowserCleanupWorker();
            var evidenceWritten = false;
            try
            {
                evidenceWritten = await evidenceTask.WaitAsync(TimeSpan.FromSeconds(3), _timeProvider);
            }
            catch (TimeoutException)
            {
            }
            throw new InvalidOperationException(
                $"Visible local account setup failed during {_setupStage} (OperationCanceledException). "
                + (evidenceWritten ? string.Empty : "Failure evidence unavailable."));
        }
        catch
        {
            StartBrowserCleanupWorker();
            throw;
        }

        _setupStage = "browser cleanup";
        StartBrowserCleanupWorker();
        await WaitForBrowserCleanupAsync();
    }

    private async Task RunProvisioningAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _verifySetupListener(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _setupStage = "Playwright initialization";
            _playwright = await _createPlaywright();
            cancellationToken.ThrowIfCancellationRequested();
            _setupStage = "browser launch";
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Timeout = 40_000,
            });
            cancellationToken.ThrowIfCancellationRequested();
            _setupStage = "browser context creation";
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            });
            cancellationToken.ThrowIfCancellationRequested();
            _setupStage = "page creation";
            _page = await context.NewPageAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _page.SetDefaultTimeout(ActionTimeoutMs);
            _page.SetDefaultNavigationTimeout(40_000);
            _setupStage = "Setup navigation";
            cancellationToken.ThrowIfCancellationRequested();
            await _page.GotoAsync($"{BaseUrl}/Setup", new PageGotoOptions
            {
                Timeout = 40_000,
                WaitUntil = WaitUntilState.DOMContentLoaded,
            });
            cancellationToken.ThrowIfCancellationRequested();
            _setupStage = "username input";
            cancellationToken.ThrowIfCancellationRequested();
            await _page.Locator("#username").FillAsync(Username);
            cancellationToken.ThrowIfCancellationRequested();
            _setupStage = "password input";
            cancellationToken.ThrowIfCancellationRequested();
            await _page.Locator("#password").FillAsync(Password);
            cancellationToken.ThrowIfCancellationRequested();
            _setupStage = "password confirmation input";
            cancellationToken.ThrowIfCancellationRequested();
            await _page.Locator("#confirmPassword").FillAsync(Password);
            cancellationToken.ThrowIfCancellationRequested();
            _setupStage = "Setup submit";
            cancellationToken.ThrowIfCancellationRequested();
            await _page.Locator("form button[type='submit']").ClickAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _setupStage = "connection dialog wait";
            cancellationToken.ThrowIfCancellationRequested();
            await _page.Locator(".connection-dialog-content").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 40_000,
            });
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            var evidence = await StartFailureEvidenceCapture();
            var evidenceStatus = evidence ? string.Empty : " Failure evidence unavailable.";
            throw new InvalidOperationException(
                $"Visible local account setup failed during {_setupStage} ({ex.GetType().Name}).{evidenceStatus}");
        }
    }

    private Task<bool> StartFailureEvidenceCapture()
    {
        if (_setupEvidenceTask is null)
        {
            var page = _page;
            var stage = _setupStage;
            _setupEvidenceTask = TryWriteSetupFailureSnapshotAsync(page, stage);
            ObserveFault(_setupEvidenceTask);
        }

        return _setupEvidenceTask;
    }

    private void StartBrowserCleanupWorker()
    {
        if (_browserCleanupTask is { IsCompleted: false })
            return;
        if (_browserCleanupTask is { IsCompleted: true } previousTask)
        {
            if (previousTask.IsFaulted || previousTask.IsCanceled)
                ObserveFault(previousTask);
            _browserCleanupTask = null;
        }
        _browserCleanupTask = CleanupBrowserAsync();
        ObserveFault(_browserCleanupTask);
    }

    private async Task CleanupBrowserAsync()
    {
        if (_setupTask is not null)
        {
            var setupTask = _setupTask;
            try
            {
                await setupTask;
            }
            catch
            {
                ObserveFault(setupTask);
            }
            if (ReferenceEquals(_setupTask, setupTask))
                _setupTask = null;
        }

        if (_browser is not null)
        {
            if (_browserCloseTask is { IsCompleted: true } previousTask
                && (previousTask.IsFaulted || previousTask.IsCanceled))
            {
                ObserveFault(previousTask);
                _browserCloseTask = null;
            }
            _browserCloseTask ??= _browser.CloseAsync();
            ObserveFault(_browserCloseTask);
            await _browserCloseTask;
            _browser = null;
            _browserCloseTask = null;
        }

        if (_playwright is not null)
        {
            _playwright.Dispose();
            _playwright = null;
        }
    }

    private async Task WaitForBrowserCleanupAsync()
    {
        StartBrowserCleanupWorker();
        var cleanupTask = _browserCleanupTask;
        if (cleanupTask is null)
            return;
        try
        {
            await cleanupTask.WaitAsync(_cleanupTimeout, _timeProvider);
            if (ReferenceEquals(_browserCleanupTask, cleanupTask))
                _browserCleanupTask = null;
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Local smoke fixture cleanup is still pending.");
        }
    }

    private bool AllOwnedResourcesReleased() => _webProcess is null
        && _browser is null
        && _browserCloseTask is null
        && _setupTask is null
        && _browserCleanupTask is null
        && _playwright is null
        && _broker is null
        && _brokerDisposalTask is null
        && (_setupEvidenceTask is null || _setupEvidenceTask.IsCompleted);

    private static void ObserveFault(Task task) => _ = task.ContinueWith(
        static faultedTask => _ = faultedTask.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private async Task<bool> TryWriteSetupFailureSnapshotAsync(IPage? page, string stage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var route = "other";
            if (page is not null && Uri.TryCreate(page.Url, UriKind.Absolute, out var uri))
            {
                route = uri.AbsolutePath switch
                {
                    "/Setup" => "Setup",
                    "/Login" => "Login",
                    _ => "other",
                };
            }

            var snapshot = page is null
                ? new { Stage = stage, Route = route, PageAvailable = false, Controls = (object?)null }
                : new
                {
                    Stage = stage,
                    Route = route,
                    PageAvailable = true,
                    Controls = (object?)new
                    {
                        Username = await GetSelectorCountsAsync(page, "#username", timeout.Token),
                        Password = await GetSelectorCountsAsync(page, "#password", timeout.Token),
                        ConfirmPassword = await GetSelectorCountsAsync(page, "#confirmPassword", timeout.Token),
                        Submit = await GetSelectorCountsAsync(page, "form button[type='submit']", timeout.Token),
                        ValidationSummary = await GetSelectorCountsAsync(page, ".validation-summary-errors", timeout.Token),
                        ValidationMessage = await GetSelectorCountsAsync(page, ".validation-message", timeout.Token),
                        ConnectionDialog = await GetSelectorCountsAsync(page, ".connection-dialog-content", timeout.Token),
                    },
                };

            var evidenceRoot = Path.GetDirectoryName(_temporaryRoot)
                ?? throw new InvalidOperationException("Fixture temporary directory has no parent.");
            var path = Path.Combine(evidenceRoot, $"mqttprobe-browser-smoke-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(snapshot), timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int Count, int VisibleCount)> GetSelectorCountsAsync(
        IPage page, string selector, CancellationToken cancellationToken)
    {
        var locator = page.Locator(selector);
        var count = await locator.CountAsync().WaitAsync(cancellationToken);
        var visibleCount = 0;
        for (var index = 0; index < count; index++)
        {
            if (await locator.Nth(index).IsVisibleAsync().WaitAsync(cancellationToken))
                visibleCount++;
        }

        return (count, visibleCount);
    }
}
