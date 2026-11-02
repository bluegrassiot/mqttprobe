using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBulkToolbarMobileTests : BunitTestContext
{
    private const string Alpha = "sensors/alpha";
    private const string Beta = "sensors/beta";
    private const string Gamma = "sensors/gamma";

    private TopicBrowserTestFixture _fixture = null!;
    private IDialogService _dialogs = null!;
    private ISnackbar _snackbar = null!;
    private ITopicExcludeService _excludes = null!;
    private IMessageStoreManager _manager = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = new TopicBrowserTestFixture();
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
        _dialogs = Substitute.For<IDialogService>();
        _snackbar = Substitute.For<ISnackbar>();
        _excludes = Substitute.For<ITopicExcludeService>();
        _manager = Substitute.For<IMessageStoreManager>();
        Services.AddSingleton(_dialogs);
        Services.AddSingleton(_snackbar);
        Services.AddSingleton(_excludes);
        Services.AddSingleton(_manager);
        Services.AddSingleton(Substitute.For<ISubscriptionManager>());
        _excludes.TopicExcludes.Returns(Array.Empty<string>());
    }

    [Test]
    public void MobileRow_ShowsOneStatusWithTheActionsAndTopicsButtons()
    {
        _fixture.AddTopics((Alpha, 1), (Beta, 1));
        var selection = Select(Alpha, Beta);
        var cut = RenderToolbar(selection);

        var mobile = cut.Find(".topic-bulk-toolbar__mobile");
        mobile.QuerySelectorAll(".topic-bulk-toolbar__mobile-count").Should().HaveCount(1,
            "the phone row is the only place the selection status appears");
        mobile.QuerySelector(".topic-bulk-toolbar__mobile-count")!.TextContent.Trim()
            .Should().Be("2 selected");
        var actions = mobile.QuerySelector(".topic-bulk-toolbar__actions-menu")!;
        actions.TextContent.Trim().Should().Be("Actions");
        actions.ClassList.Should().Contain("app-mobile-bar-btn",
            "both row buttons share the 44px touch height");
        actions.GetAttribute("aria-expanded").Should().Be("false");
        actions.GetAttribute("aria-haspopup").Should().Be("menu");
        var topics = mobile.QuerySelector(".topic-bulk-toolbar__topics")!;
        topics.TextContent.Trim().Should().Be("Topics");
        topics.ClassList.Should().Contain("app-mobile-bar-btn");
    }

    [Test]
    public async Task MobileTopicsButton_OpensTheTopicPicker()
    {
        var opened = false;
        var cut = Render<TopicBulkToolbar>(parameters => parameters
            .AddCascadingValue(Select(Alpha))
            .Add(x => x.OnOpenTopicPicker, () => { opened = true; return Task.CompletedTask; }));

        await cut.InvokeAsync(() => cut.Find(".topic-bulk-toolbar__topics").Click());

        opened.Should().BeTrue();
    }

    [Test]
    public void MobileRow_LeavesWhenTheSelectionIsCleared()
    {
        _fixture.AddTopics((Alpha, 1));
        var selection = Select(Alpha);
        var cut = RenderToolbar(selection);
        cut.FindAll(".topic-bulk-toolbar__mobile").Should().HaveCount(1);

        selection.Clear();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-bulk-toolbar__mobile").Should().BeEmpty(
                "the row exists only while there is a selection to act on");
            cut.Markup.Should().NotContain("None selected");
        });
    }

    [Test]
    public void CompactSummary_UsesShortCounts()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);

        selection.CompactSummary.Should().Be("None selected");
        selection.SelectAll([Raw(Alpha)]);
        selection.CompactSummary.Should().Be("1 selected");
        selection.SelectAll([Raw(Alpha), Raw(Beta)]);
        selection.CompactSummary.Should().Be("2 selected");
        selection.CoversWholeTree = true;
        selection.CompactSummary.Should().Be("All selected");
    }

    [Test]
    public void MobileActionsMenu_OffersTheFullActionLabels()
    {
        AuthorizeAsOperator();
        var selection = Select(Alpha, Beta);
        var cut = RenderToolbar(selection);
        var providers = Render<MudPopoverProvider>();

        cut.Find(".topic-bulk-toolbar__actions-menu").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".topic-bulk-toolbar__actions-menu").GetAttribute("aria-expanded").Should().Be("true"));

        providers.WaitForAssertion(() =>
        {
            var labels = providers.FindAll(".mud-menu-item")
                .Select(item => item.TextContent.Trim())
                .ToList();
            labels.Should().Contain(label => label.Contains("Clear messages", StringComparison.Ordinal));
            labels.Should().Contain(label => label.Contains("Exclude topics", StringComparison.Ordinal));
        });
    }

    [Test]
    public void MobileActions_EscapeOnTheTrigger_ClosesTheMenu()
    {
        AuthorizeAsOperator();
        var cut = RenderToolbar(Select(Alpha, Beta));
        var providers = Render<MudPopoverProvider>();

        cut.Find(".topic-bulk-toolbar__actions-menu").Click();
        cut.WaitForAssertion(() =>
            cut.Find(".topic-bulk-toolbar__actions-menu").GetAttribute("aria-expanded").Should().Be("true"));
        providers.WaitForAssertion(() => providers.FindAll(".mud-menu-item").Should().HaveCount(2));

        cut.Find(".topic-bulk-toolbar__actions-menu").KeyDown("Escape");

        cut.WaitForAssertion(() =>
            cut.Find(".topic-bulk-toolbar__actions-menu").GetAttribute("aria-expanded")
                .Should().Be("false", "Escape on the trigger dismisses the menu too"));
    }

    [Test]
    public void MobileMenu_EscapeOnAnItem_ClosesTheMenu()
    {
        AuthorizeAsOperator();
        var cut = RenderToolbar(Select(Alpha, Beta));
        var providers = Render<MudPopoverProvider>();

        cut.Find(".topic-bulk-toolbar__actions-menu").Click();
        providers.WaitForAssertion(() => providers.FindAll(".mud-menu-item").Should().HaveCount(2));

        providers.FindAll(".mud-menu-item")[0].KeyDown("Escape");

        cut.WaitForAssertion(() =>
            cut.Find(".topic-bulk-toolbar__actions-menu").GetAttribute("aria-expanded").Should().Be("false"));
    }

    [Test]
    public void MobileMenu_TabOnAnItem_ClosesTheMenu()
    {
        AuthorizeAsOperator();
        var cut = RenderToolbar(Select(Alpha, Beta));
        var providers = Render<MudPopoverProvider>();

        cut.Find(".topic-bulk-toolbar__actions-menu").Click();
        providers.WaitForAssertion(() => providers.FindAll(".mud-menu-item").Should().HaveCount(2));

        providers.FindAll(".mud-menu-item")[0].KeyDown("Tab");

        cut.WaitForAssertion(() =>
            cut.Find(".topic-bulk-toolbar__actions-menu").GetAttribute("aria-expanded").Should().Be("false"));
    }

    [Test]
    public async Task MobileMenuClear_UsesTheConfirmedTargets_AndCancelLeavesTheMessages()
    {
        _fixture.AddTopics((Alpha, 2));
        var selection = Select(Alpha);
        var pinned = selection.Branches;
        DialogParameters? captured = null;
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _dialogs.ShowAsync<ClearSelectedMessagesDialog>(
                Arg.Any<string>(), Arg.Do<DialogParameters>(parameters => captured = parameters),
                Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        var cut = RenderToolbar(selection);
        var providers = Render<MudPopoverProvider>();
        cut.Find(".topic-bulk-toolbar__actions-menu").Click();
        providers.WaitForAssertion(() => providers.FindAll(".mud-menu-item").Should().HaveCount(2));

        providers.FindAll(".mud-menu-item")[0].Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _manager.DidNotReceive().ClearMessagesForBranchesAsync(
            Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>());
        captured.Should().NotBeNull();
        captured["Topics"].Should().BeEquivalentTo(pinned);
    }

    [Test]
    public async Task MobileMenuExclude_UsesTheConfirmedTargets()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1), (Beta, 1));
        var selection = Select(Alpha, Beta);
        var pinned = selection.Branches;
        ReturnDialog(DialogResult.Ok(pinned));
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(Raw(Alpha), TopicExcludeAddOutcome.Added),
                new TopicBranchExcludeResult(Raw(Beta), TopicExcludeAddOutcome.Added)
            ]));
        var cut = RenderToolbar(selection);
        var providers = Render<MudPopoverProvider>();
        cut.Find(".topic-bulk-toolbar__actions-menu").Click();
        providers.WaitForAssertion(() => providers.FindAll(".mud-menu-item").Should().HaveCount(2));

        providers.FindAll(".mud-menu-item")[1].Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _excludes.Received(1).AddBranches(
            Arg.Is<IReadOnlyList<TopicBranchTarget>>(targets => targets.SequenceEqual(pinned)),
            Arg.Any<TopicExcludeConnectionToken>());
    }

    [Test]
    public async Task MobileOutcome_ShowsDerivedCountsAndKeepsPurgeDetailBehindDetails()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1), (Beta, 1), (Gamma, 1));
        var selection = Select(Alpha, Beta, Gamma);
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(Raw(Alpha), TopicExcludeAddOutcome.Added),
                new TopicBranchExcludeResult(Raw(Beta), TopicExcludeAddOutcome.AppliedWithPurgeFailure,
                    new UserNotification(UserNotificationSeverity.Warning, "Stored history could not be cleared.")),
                new TopicBranchExcludeResult(Raw(Gamma), TopicExcludeAddOutcome.Failed,
                    new UserNotification(UserNotificationSeverity.Error, "Connection changed."))
            ]));
        ReturnDialog(DialogResult.Ok(selection.Branches));
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var compact = cut.Find(".topic-bulk-outcome-compact");
            compact.TextContent.Should().Contain("Excluded 2 of 3 topics.");
            compact.TextContent.Should().Contain("History cleanup incomplete for 1.");
            compact.TextContent.Should().Contain("1 not added.");
            compact.QuerySelector(".topic-bulk-outcome-compact__details")!.GetAttribute("aria-expanded")
                .Should().Be("false");
            compact.QuerySelectorAll(".topic-bulk-outcome__lines li").Should().BeEmpty(
                "the per-target report stays collapsed until Details is chosen");
        });

        cut.Find(".topic-bulk-outcome-compact__details").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".topic-bulk-outcome-compact__details").GetAttribute("aria-expanded").Should().Be("true");
            var details = cut.FindAll(".topic-bulk-outcome-compact .topic-bulk-outcome__lines li")
                .Select(item => item.TextContent)
                .ToList();
            details.Should().Contain(line => line.Contains("Stored history could not be cleared.", StringComparison.Ordinal));
            details.Should().Contain(line => line.Contains("Connection changed.", StringComparison.Ordinal));
        });

        cut.Find(".topic-bulk-outcome-compact__dismiss").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-bulk-outcome-compact").Should().BeEmpty();
            cut.FindAll(".topic-bulk-outcome").Should().BeEmpty();
        });
    }

    [Test]
    public async Task MobileOutcome_SavedButNotApplied_KeepsThePathCheckedAndReportsItBehindDetails()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1), (Beta, 1));
        var selection = Select(Alpha, Beta);
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(Raw(Alpha), TopicExcludeAddOutcome.Added),
                new TopicBranchExcludeResult(Raw(Beta), TopicExcludeAddOutcome.SavedButNotApplied,
                    new UserNotification(UserNotificationSeverity.Warning, "Saved on the previous connection."))
            ]));
        ReturnDialog(DialogResult.Ok(selection.Branches));
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var compact = cut.Find(".topic-bulk-outcome-compact");
            compact.TextContent.Should().Contain("Excluded 1 of 2 topics.");
            compact.TextContent.Should().Contain("1 saved but not applied here.");
            compact.TextContent.Should().NotContain("not added.",
                "a saved exclusion is not a failed one");
            compact.QuerySelector(".topic-bulk-outcome-compact__details")!.GetAttribute("aria-expanded")
                .Should().Be("false");
        });

        cut.Find(".topic-bulk-outcome-compact__details").Click();

        cut.WaitForAssertion(() =>
        {
            var details = cut.FindAll(".topic-bulk-outcome-compact .topic-bulk-outcome__lines li")
                .Select(item => item.TextContent)
                .ToList();
            details.Should().Contain(line => line.Contains(
                $"Saved, not applied to this connection (1): \"{Beta}\": Saved on the previous connection.",
                StringComparison.Ordinal));
            details.Should().NotContain(line => line.StartsWith("Not added", StringComparison.Ordinal));
        });
        selection.Branches.Select(branch => branch.Path).Should().Equal(new[] { Beta });
        _snackbar.Received(1).Add(
            Arg.Is<string>(message => message.Contains("saved but not applied to this connection")),
            Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task MobileOutcome_SavedButNotAppliedOnly_SaysNothingExcludedHere()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1));
        var selection = Select(Alpha);
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(Raw(Alpha), TopicExcludeAddOutcome.SavedButNotApplied)
            ]));
        ReturnDialog(DialogResult.Ok(selection.Branches));
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var compact = cut.Find(".topic-bulk-outcome-compact");
            compact.TextContent.Should().Contain("Nothing excluded here.");
            compact.TextContent.Should().Contain("1 saved but not applied here.");
            compact.QuerySelector(".topic-bulk-outcome-compact__details").Should().NotBeNull(
                "the saved path still gets a full line");
        });
        selection.Branches.Select(branch => branch.Path).Should().Equal(new[] { Alpha });
        _snackbar.Received(1).Add(
            Arg.Is<string>(message => message.Contains("1 topic saved but not applied to this connection")
                && !message.Contains("Excluded")),
            Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task MobileOutcome_ClearSuccess_IsOneLineWithoutDetails()
    {
        _fixture.AddTopics((Alpha, 2));
        var selection = Select(Alpha);
        ReturnDialog(DialogResult.Ok(selection.Branches));
        _manager.ClearMessagesForBranchesAsync(
                Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(call => Task.FromResult(new TopicBranchClearResult(true,
                _fixture.Adapter.Store.ClearMessagesForBranches(
                    call.Arg<IReadOnlyList<TopicBranchTarget>>()))));
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var compact = cut.Find(".topic-bulk-outcome-compact");
            compact.TextContent.Should().Contain("2 stored messages cleared from 1 topic.");
            compact.QuerySelector(".topic-bulk-outcome-compact__details").Should().BeNull();
            compact.QuerySelectorAll(".topic-bulk-outcome__lines li").Should().BeEmpty();
        });
    }

    [Test]
    public async Task MobileOutcome_ClearConnectionChange_ShowsShortLineAndFullDetailBehindDetails()
    {
        _fixture.AddTopics((Alpha, 1));
        var selection = Select(Alpha);
        ReturnDialog(DialogResult.Ok(selection.Branches));
        _manager.ClearMessagesForBranchesAsync(
                Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(Task.FromResult(new TopicBranchClearResult(Applied: false, ClearedMessages: 0)));
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var compact = cut.Find(".topic-bulk-outcome-compact");
            compact.TextContent.Should().Contain("Nothing cleared. Connection changed.");
            compact.QuerySelector(".topic-bulk-outcome-compact__details").Should().NotBeNull();
        });

        cut.Find(".topic-bulk-outcome-compact__details").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".topic-bulk-outcome-compact .topic-bulk-outcome__lines").TextContent.Should().Contain(
                "The connection changed before the clear was confirmed. Nothing was cleared."));
    }

    private TopicBulkSelection Select(params string[] paths)
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll(paths.Select(Raw));
        return selection;
    }

    private IRenderedComponent<TopicBulkToolbar> RenderToolbar(TopicBulkSelection selection) =>
        Render<TopicBulkToolbar>(parameters => parameters.AddCascadingValue(selection));

    private void ReturnDialog(DialogResult result)
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(result));
        _dialogs.ShowAsync<ExcludeTopicsDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        _dialogs.ShowAsync<ClearSelectedMessagesDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
    }

    private static TopicBranchTarget Raw(string path) => new(TopicBranchKind.Raw, Path: path);

    private void AuthorizeAsOperator() =>
        AuthorizationContext.SetAuthorized("testuser").SetRoles(AppRoles.Operator);
}
