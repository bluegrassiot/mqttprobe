using System.Text.Json;
using Microsoft.Playwright;

namespace MqttProbe.BrowserSmokeTests;

public sealed partial class ExclusionBrowserSmokeTests
{
    private const int ResumeDiagnosticTimeoutMs = 3_000;
    private static readonly JsonSerializerOptions _evidenceJsonOptions = new() { WriteIndented = true };

    private sealed record ResumeSnapshot(
        string Phase,
        string ActiveTab,
        bool Connected,
        string[] TreeRows,
        string[] SelectedTreeRows,
        int TargetTopicRowCount,
        int TargetTopicCellMatchCount,
        int ResumePayloadCellMatchCount,
        int ResumePairMatchCount,
        int ResumePairVisibleCount,
        string[] ResumePairDetails,
        bool ResumePayloadVisible,
        string[] VisibleTestPayloads,
        string? MessagesProcessed,
        string? MessagesExcluded);

    private sealed record ResumeFailureEvidence(
        string Stage,
        string ExceptionType,
        string TargetTopic,
        string ResumePayload,
        ResumeSnapshot[] Snapshots,
        ConnectionDialogSnapshot? ConnectionDialog);

    private sealed record ConnectionDialogSnapshot(
        int VisibleDialogCount,
        int ConnectionDialogCount,
        int ActiveConnectionDialogCount,
        int MobileOnConnectControlCount,
        bool MobileOnConnectControlVisible,
        int OnConnectTabCount,
        bool OnConnectTabVisible,
        bool OnConnectTabSelected,
        int ActiveExcludeEditorCount,
        bool ActiveExcludeEditorVisible,
        int ExpandedPanelCount,
        int TopicInputCount,
        bool TopicInputMatchesExpected,
        int AddButtonCount,
        bool AddButtonVisible,
        int? CountChipValue,
        int TableRowCount,
        int ExpectedFilterRowCount,
        int CheckboxCount,
        bool CheckboxVisible,
        int RemoveButtonCount,
        bool RemoveButtonVisible,
        int ConnectButtonCount,
        bool ConnectButtonVisible,
        bool Connected);

    private static async Task<ResumeSnapshot> CaptureResumeSnapshotAsync(
        IPage page,
        string phase,
        string root,
        string targetTopic,
        string resumePayload,
        string[] baselineTopics,
        string[] controlTopics,
        string[] expectedBlocked,
        CancellationToken token)
    {
        var activeTabs = await page.Locator("[role='tab'][aria-selected='true']")
            .EvaluateAllAsync<string[]>("tabs => tabs.map(tab => (tab.innerText || '').trim())")
            .WaitAsync(token);
        var activeTab = string.Join(",", activeTabs);
        var connected = await page.Locator(".status-chip--connected").First.IsVisibleAsync()
            .WaitAsync(token);

        var treeRows = new List<string>();
        var selectedTreeRows = new List<string>();
        var rows = page.Locator(".topic-tree-row");
        var rowCount = await rows.CountAsync().WaitAsync(token);
        for (var i = 0; i < rowCount; i++)
        {
            var row = rows.Nth(i);
            var text = (await row.InnerTextAsync().WaitAsync(token)).Trim();
            if (!text.StartsWith(root, StringComparison.Ordinal))
                continue;

            treeRows.Add(text);
            if ((await row.GetAttributeAsync("class").WaitAsync(token))?.Contains(
                    "topic-tree-row--selected", StringComparison.Ordinal) == true)
            {
                selectedTreeRows.Add(text);
            }
        }

        var targetRows = page.Locator(
            $".browser-split-wrap tr:has(td.pb-cell-topic:text-is('{targetTopic}'))");
        var targetTopicRowCount = await targetRows.CountAsync().WaitAsync(token);
        var targetTopicCellMatchCount = await page.Locator(
            $".browser-split-wrap td.pb-cell-topic:text-is('{targetTopic}')")
            .CountAsync().WaitAsync(token);
        var resumePayloadCellMatchCount = await page.Locator(
            $".browser-split-wrap td.pb-cell-payload:text-is('{resumePayload}')")
            .CountAsync().WaitAsync(token);
        var resumeRow = PayloadRow(page, targetTopic, resumePayload);
        var resumePairMatchCount = await resumeRow.CountAsync().WaitAsync(token);
        var resumePairVisibleCount = 0;
        var resumePairDetails = new List<string>();
        for (var i = 0; i < resumePairMatchCount; i++)
        {
            var match = resumeRow.Nth(i);
            var visible = await match.IsVisibleAsync().WaitAsync(token);
            if (visible)
                resumePairVisibleCount++;

            var topicText = (await match.Locator("td.pb-cell-topic").InnerTextAsync().WaitAsync(token)).Trim();
            var payloadText = (await match.Locator("td.pb-cell-payload").InnerTextAsync().WaitAsync(token)).Trim();
            resumePairDetails.Add($"{topicText} | {payloadText} | visible={visible}");
        }
        var resumePayloadVisible = resumePairVisibleCount > 0;

        var allowedTopics = baselineTopics.Concat(controlTopics).Concat(expectedBlocked)
            .ToHashSet(StringComparer.Ordinal);
        var allowedPayloads = baselineTopics.Select((_, index) => $"baseline-{root}-{index + 1}")
            .Concat(controlTopics.Select((_, index) => $"baseline-ctrl-{root}-{index + 1}"))
            .Concat([ $"blocked-{root}", $"sentinel-{root}-1", resumePayload ])
            .ToHashSet(StringComparer.Ordinal);
        var visibleTestPayloads = new List<string>();
        var payloadRows = page.Locator(".browser-split-wrap tr:has(td.pb-cell-topic)");
        var payloadRowCount = await payloadRows.CountAsync().WaitAsync(token);
        for (var i = 0; i < payloadRowCount; i++)
        {
            var row = payloadRows.Nth(i);
            var topic = (await row.Locator("td.pb-cell-topic").InnerTextAsync().WaitAsync(token)).Trim();
            var payload = (await row.Locator("td.pb-cell-payload").InnerTextAsync().WaitAsync(token)).Trim();
            if (allowedTopics.Contains(topic) && allowedPayloads.Contains(payload)
                && await row.IsVisibleAsync().WaitAsync(token))
            {
                visibleTestPayloads.Add($"{topic} | {payload}");
            }
        }

        var (processed, excluded) = await ReadProcessingCountersAsync(page, token);
        return new ResumeSnapshot(phase, activeTab, connected, treeRows.ToArray(),
            selectedTreeRows.ToArray(), targetTopicRowCount, targetTopicCellMatchCount,
            resumePayloadCellMatchCount, resumePairMatchCount, resumePairVisibleCount,
            resumePairDetails.ToArray(), resumePayloadVisible, visibleTestPayloads.ToArray(), processed, excluded);
    }

    private static async Task TryCaptureResumeSnapshotAsync(
        IPage page,
        string phase,
        string root,
        string targetTopic,
        string resumePayload,
        string[] baselineTopics,
        string[] controlTopics,
        string[] expectedBlocked,
        List<ResumeSnapshot> snapshots)
    {
        var previousTimeout = ActionTimeoutMs;
        try
        {
            page.SetDefaultTimeout(ResumeDiagnosticTimeoutMs);
            using var timeout = new CancellationTokenSource(ResumeDiagnosticTimeoutMs);
            var snapshot = await CaptureResumeSnapshotAsync(page, phase, root, targetTopic,
                resumePayload, baselineTopics, controlTopics, expectedBlocked, timeout.Token);
            snapshots.Add(snapshot);
        }
        catch
        {
            // Resume snapshots are diagnostic only; a transient DOM state must not fail the smoke.
        }
        finally
        {
            try
            {
                page.SetDefaultTimeout(previousTimeout);
            }
            catch
            {
                // The page may have closed while diagnostics were running.
            }
        }
    }

    private static async Task<(string? Processed, string? Excluded)> ReadProcessingCountersAsync(
        IPage page, CancellationToken token)
    {
        var flyout = page.Locator(".metrics-flyout:visible");
        if (await flyout.CountAsync().WaitAsync(token) == 0)
            return (null, null);

        var rows = flyout.Locator(".metrics-row");

        string? processed = null;
        string? excluded = null;
        var count = await rows.CountAsync().WaitAsync(token);
        for (var i = 0; i < count; i++)
        {
            var row = rows.Nth(i);
            var spans = row.Locator("span");
            var label = (await spans.Nth(0).InnerTextAsync().WaitAsync(token)).Trim();
            if (label is not ("Messages processed" or "Messages excluded"))
                continue;

            var value = (await spans.Nth(1).InnerTextAsync().WaitAsync(token)).Trim();
            if (label == "Messages processed")
                processed = value;
            else
                excluded = value;
        }

        return (processed, excluded);
    }

    private static async Task<string?> TryWriteResumeEvidenceAsync(
        IPage? page,
        string stage,
        string exceptionType,
        string root,
        string targetTopic,
        string resumePayload,
        string[] baselineTopics,
        string[] controlTopics,
        string[] expectedBlocked,
        List<ResumeSnapshot> snapshots)
    {
        try
        {
            return await WriteResumeEvidenceAsync(page, stage, exceptionType, root, targetTopic,
                resumePayload, baselineTopics, controlTopics, expectedBlocked, snapshots);
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name} during evidence capture)";
        }
    }

    private static async Task<string?> WriteResumeEvidenceAsync(
        IPage? page,
        string stage,
        string exceptionType,
        string root,
        string targetTopic,
        string resumePayload,
        string[] baselineTopics,
        string[] controlTopics,
        string[] expectedBlocked,
        List<ResumeSnapshot> snapshots)
    {
        if (page is null)
            return "unavailable (page unavailable)";

        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "opencode");
        string path;
        try
        {
            Directory.CreateDirectory(tempRoot);
            path = Path.Combine(tempRoot, $"mqttprobe-exclusion-{Guid.NewGuid():N}.json");
            await WriteEvidenceAsync(path, stage, exceptionType, targetTopic, resumePayload, snapshots);
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name} during initial evidence write)";
        }

        var priorTimeout = 15_000;
        page.SetDefaultTimeout(2_000);
        ConnectionDialogSnapshot? connectionDialog = null;
        try
        {
            try
            {
                snapshots.Add(await CaptureResumeSnapshotAsync(page, "failure-dom", root,
                    targetTopic, resumePayload, baselineTopics, controlTopics, expectedBlocked,
                    CancellationToken.None));
            }
            catch
            {
                // Earlier phase snapshots still provide the state immediately before the failed assertion.
            }
            if (stage.StartsWith("connection-dialog:", StringComparison.Ordinal))
            {
                try
                {
                    connectionDialog = await CaptureConnectionDialogSnapshotAsync(page, root);
                }
                catch
                {
                    // Keep the resume evidence if the dialog is no longer responsive.
                }
            }
        }
        finally
        {
            page.SetDefaultTimeout(priorTimeout);
        }

        try
        {
            await WriteEvidenceAsync(path, stage, exceptionType, targetTopic, resumePayload,
                snapshots, connectionDialog);
        }
        catch
        {
            // Retain the initial file if the final rewrite fails.
        }

        return path;
    }

    private static Task WriteEvidenceAsync(
        string path,
        string stage,
        string exceptionType,
        string targetTopic,
        string resumePayload,
        List<ResumeSnapshot> snapshots,
        ConnectionDialogSnapshot? connectionDialog = null)
    {
        var evidence = new ResumeFailureEvidence(stage, exceptionType, targetTopic, resumePayload,
            snapshots.ToArray(), connectionDialog);
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, _evidenceJsonOptions));
    }

    private static async Task<ConnectionDialogSnapshot> CaptureConnectionDialogSnapshotAsync(
        IPage page, string root)
    {
        var dialog = page.Locator(".mud-dialog-container:visible .connection-dialog-content");
        var mobileOnConnectControl = dialog.Locator(".step-selector .step-btn")
            .Filter(new() { HasText = "On Connect" });
        var onConnectTab = dialog.GetByRole(AriaRole.Tab, new() { Name = "On Connect", Exact = true });
        var editor = dialog.Locator(".mud-tab-panel-active .exclude-editor");
        var expandedPanel = editor.Locator(".mud-collapse-container.mud-collapse-entered");
        var topicInput = editor.Locator(TopicInput);
        var addButton = editor.Locator(AddExcludeButton);
        var countChip = editor.Locator(".count-chip");
        var rows = editor.Locator(".exclude-editor-table-wrap .mud-table-body .mud-table-row");
        var expectedRows = editor.Locator(
            $".exclude-editor-table-wrap .mud-table-body .mud-table-row:has-text('{root}/dialog-excluded/#')");
        var checkboxes = editor.Locator(".exclude-editor-table-wrap .mud-table-body label.mud-checkbox");
        var removeButton = editor.Locator(".exclude-editor-actions button[title='Remove']");
        var connectButton = dialog.GetByRole(AriaRole.Button, new() { Name = "Connect", Exact = true });
        var mobileOnConnectCount = await mobileOnConnectControl.CountAsync();
        var onConnectTabCount = await onConnectTab.CountAsync();
        var topicInputCount = await topicInput.CountAsync();
        var countChipCount = await countChip.CountAsync();
        var checkboxCount = await checkboxes.CountAsync();
        var connectButtonCount = await connectButton.CountAsync();
        var onConnectTabSelected = onConnectTabCount > 0
            && string.Equals(await onConnectTab.Nth(0).GetAttributeAsync("aria-selected"), "true",
                StringComparison.OrdinalIgnoreCase);
        var inputValue = topicInputCount > 0 ? await topicInput.Nth(0).GetAttributeAsync("value") : null;
        var chipText = countChipCount > 0 ? (await countChip.Nth(0).InnerTextAsync()).Trim() : string.Empty;
        int? countChipValue = int.TryParse(chipText, out var parsedCount) ? parsedCount : null;

        return new ConnectionDialogSnapshot(
            await page.Locator(".mud-dialog-container:visible").CountAsync(),
            await page.Locator(".mud-dialog-container:visible .connection-dialog-content").CountAsync(),
            await dialog.CountAsync(),
            mobileOnConnectCount,
            mobileOnConnectCount > 0 && await mobileOnConnectControl.Nth(0).IsVisibleAsync(),
            onConnectTabCount,
            onConnectTabCount > 0 && await onConnectTab.Nth(0).IsVisibleAsync(),
            onConnectTabSelected,
            await editor.CountAsync(),
            await editor.IsVisibleAsync(),
            await expandedPanel.CountAsync(),
            topicInputCount,
            string.Equals(inputValue, $"{root}/dialog-excluded/#", StringComparison.Ordinal),
            await addButton.CountAsync(),
            await addButton.IsVisibleAsync(),
            countChipValue,
            await rows.CountAsync(),
            await expectedRows.CountAsync(),
            checkboxCount,
            checkboxCount > 0 && await checkboxes.Nth(0).IsVisibleAsync(),
            await removeButton.CountAsync(),
            await removeButton.IsVisibleAsync(),
            connectButtonCount,
            connectButtonCount > 0 && await connectButton.Nth(0).IsVisibleAsync(),
            await page.Locator(".status-chip--connected").IsVisibleAsync());
    }

    private static string FormatEvidencePath(string? path) =>
        path is null ? string.Empty : $"; sanitized evidence: {path}";

    private async Task VerifyConnectionDialogExcludeEditorAsync(
        IPage page, string root, CancellationToken token)
    {
        SetStage("connection-dialog: click Disconnect button");
        await page.GetByRole(AriaRole.Button, new() { Name = "Disconnect", Exact = true })
            .ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(token);
        SetStage("connection-dialog: click Connect button");
        await page.GetByRole(AriaRole.Button, new() { Name = "Connect", Exact = true })
            .ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var activeDialog = page.Locator(".mud-dialog-container:visible .connection-dialog-content");
        SetStage("connection-dialog: wait for visible dialog");
        await activeDialog.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = ActionTimeoutMs,
        }).WaitAsync(token);
        var onConnectTab = activeDialog.GetByRole(AriaRole.Tab, new() { Name = "On Connect", Exact = true });
        SetStage("connection-dialog: wait for visible On Connect tab");
        await onConnectTab.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = ActionTimeoutMs,
        }).WaitAsync(token);
        SetStage("connection-dialog: click visible On Connect tab");
        await onConnectTab.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var filter = $"{root}/dialog-excluded/#";
        var editor = activeDialog.Locator(".mud-tab-panel-active .exclude-editor");
        SetStage("connection-dialog: wait for active On Connect exclusion editor");
        await editor.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = ActionTimeoutMs,
        }).WaitAsync(token);
        await AddAndAssertExcludeAsync(editor, filter, token, SetStage, "connection-dialog");
        await RemoveAndAssertExcludeAsync(editor, filter, token, SetStage, "connection-dialog");
        SetStage("connection-dialog: click dialog Connect button");
        await activeDialog.GetByRole(AriaRole.Button, new() { Name = "Connect", Exact = true }).ClickAsync(
            new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(token);
        SetStage("connection-dialog: wait for dialog to close");
        await activeDialog.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = ActionTimeoutMs,
        }).WaitAsync(token);
        SetStage("connection-dialog: wait for connected status");
        await Assertions.Expect(page.Locator(".status-chip--connected").First)
            .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task AddAndAssertExcludeAsync(
        ILocator editor, string filter, CancellationToken token,
        Action<string>? setStage = null, string stagePrefix = "exclude editor")
    {
        var expandedPanel = editor.Locator(".mud-collapse-container.mud-collapse-entered");
        setStage?.Invoke($"{stagePrefix}: inspect expanded panel");
        if (await expandedPanel.CountAsync().WaitAsync(token) == 0)
        {
            setStage?.Invoke($"{stagePrefix}: click expansion header");
            await editor.Locator(".mud-expand-panel .mud-expand-panel-header").ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(token);
            setStage?.Invoke($"{stagePrefix}: wait for expanded panel");
            await expandedPanel.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(token);
        }

        var topicInput = editor.Locator(TopicInput);
        setStage?.Invoke($"{stagePrefix}: fill generated exclusion filter");
        await topicInput.FillAsync(filter,
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var addButton = editor.Locator(AddExcludeButton);
        setStage?.Invoke($"{stagePrefix}: click Add exclude topic");
        await addButton.ClickAsync(
            new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(token);
        setStage?.Invoke($"{stagePrefix}: assert exclusion count");
        await Assertions.Expect(editor.Locator(".count-chip"))
            .ToHaveTextAsync("1", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        setStage?.Invoke($"{stagePrefix}: assert one exclusion row");
        await Assertions.Expect(editor.Locator(
                ".exclude-editor-table-wrap .mud-table-body .mud-table-row"))
            .ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        setStage?.Invoke($"{stagePrefix}: assert generated exclusion row visible");
        await Assertions.Expect(editor.Locator(
                $".exclude-editor-table-wrap .mud-table-body .mud-table-row:has-text('{filter}')"))
            .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task RemoveAndAssertExcludeAsync(
        ILocator editor, string filter, CancellationToken token,
        Action<string>? setStage = null, string stagePrefix = "exclude editor")
    {
        setStage?.Invoke($"{stagePrefix}: click exclusion checkbox");
        await editor.Locator(".exclude-editor-table-wrap .mud-table-body label.mud-checkbox")
            .First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(token);
        setStage?.Invoke($"{stagePrefix}: click Remove");
        await editor.Locator(".exclude-editor-actions button[title='Remove']").ClickAsync(
            new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(token);
        setStage?.Invoke($"{stagePrefix}: assert zero exclusion count");
        await Assertions.Expect(editor.Locator(".count-chip"))
            .ToHaveTextAsync("0", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        setStage?.Invoke($"{stagePrefix}: assert generated exclusion row absent");
        await Assertions.Expect(editor.Locator(
                $".exclude-editor-table-wrap .mud-table-body .mud-table-row:has-text('{filter}')"))
            .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }
}
