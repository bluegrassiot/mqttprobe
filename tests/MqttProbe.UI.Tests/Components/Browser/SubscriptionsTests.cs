using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using MqttProbe.Core;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Services;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class SubscriptionsTests : BunitTestContext
{
    private ISubscriptionManager _mockSubMgr = null!;
    private ISnackbar _mockSnackbar = null!;
    private ITopicExcludeService _mockExcludeService = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockSubMgr = Substitute.For<ISubscriptionManager>();
        _mockSubMgr.Subscriptions.Returns(Array.Empty<SubscribedTopic>());
        Services.AddSingleton(_mockSubMgr);

        _mockSnackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_mockSnackbar);

        _mockExcludeService = Substitute.For<ITopicExcludeService>();
        _mockExcludeService.TopicExcludes.Returns(Array.Empty<string>());
        _mockExcludeService.ValidateAdd(Arg.Any<string>()).Returns(new TopicExcludeValidationResult(true));
        Services.AddSingleton(_mockExcludeService);

        Services.AddSingleton(Substitute.For<IDialogService>());
        EnsureMudProviders();
    }

    private void AuthorizeAsOperator()
    {
        AuthorizationContext.SetAuthorized("testuser").SetRoles(AppRoles.Operator);
    }

    [Test]
    public void Renders_ActiveSubscriptions_FromManager()
    {
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "spBv1.0/+/DDATA/#", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce },
            new() { Topic = "my/topic", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce }
        });

        var cut = Render<Subscriptions>();

        var markup = cut.Markup;
        markup.Should().Contain("spBv1.0/+/DDATA/#");
        markup.Should().Contain("my/topic");
        markup.Should().Contain("1 · At least once");
        markup.Should().Contain("0 · At most once");
    }

    [Test]
    public async Task AddButton_CallsSubscribeAsync_WithEnteredTopic()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>()).Returns(Task.CompletedTask);
        var cut = Render<Subscriptions>();

        var topicInput = cut.FindAll("input").First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox");
        topicInput.Input(new ChangeEventArgs { Value = "sensors/#" });
        cut.Find("button[title='Add subscription']").Click();

        await _mockSubMgr.Received(1).Add("sensors/#", MqttQualityOfServiceLevel.AtLeastOnce);
    }

    [Test]
    public async Task DeleteSelected_CallsUnsubscribeAsync()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a/topic" }
        });
        _mockSubMgr.Remove(Arg.Any<List<string>>()).Returns(Task.CompletedTask);
        var cut = Render<Subscriptions>();

        cut.Find("input[type='checkbox']").Change(true);
        cut.Find("button[title='Remove']").Click();

        await _mockSubMgr.Received(1).Remove(Arg.Any<List<string>>());
    }

    [Test]
    public async Task AddButton_AwaitsDelayedAdd_DisablingButtonUntilComplete_ThenClearsTopic()
    {
        AuthorizeAsOperator();
        var pending = new TaskCompletionSource();
        _mockSubMgr.Add("sensors/#", Arg.Any<MqttQualityOfServiceLevel>()).Returns(pending.Task);
        var cut = Render<Subscriptions>();

        var topicInput = cut.FindAll("input").First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox");
        topicInput.Input(new ChangeEventArgs { Value = "sensors/#" });
        cut.Find("button[title='Add subscription']").Click();

        cut.Find("button[title='Add subscription']").HasAttribute("disabled")
            .Should().BeTrue("the add button stays disabled while the Add task is in flight");

        pending.SetResult();

        cut.WaitForAssertion(() =>
        {
            cut.Find("button[title='Add subscription']").HasAttribute("disabled").Should().BeFalse();
            cut.FindAll("input").First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox")
                .GetAttribute("value").Should().BeNullOrEmpty();
        });

        await _mockSubMgr.Received(1).Add("sensors/#", Arg.Any<MqttQualityOfServiceLevel>());
    }

    [Test]
    public void AddButton_WhenAddFails_SurfacesErrorSnackbar()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Add("bad/#", Arg.Any<MqttQualityOfServiceLevel>())
            .Returns(Task.FromException(new InvalidOperationException("broker rejected")));
        var cut = Render<Subscriptions>();

        var topicInput = cut.FindAll("input").First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox");
        topicInput.Input(new ChangeEventArgs { Value = "bad/#" });
        cut.Find("button[title='Add subscription']").Click();

        cut.WaitForAssertion(() =>
            _mockSnackbar.Received().Add(
                Arg.Is<string>(m => m!.Contains("Failed to subscribe")),
                Severity.Error,
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string>()));
    }

    [Test]
    public void RowTemplate_UsesMudTd_NotMudTh()
    {
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "some/topic" }
        });

        var cut = Render<Subscriptions>();

        cut.FindAll("td").Should().NotBeEmpty();
        cut.FindAll("th").Should().NotBeEmpty();
    }

    [Test]
    public async Task SelectAllAndRemove_RemovesAllSubscriptions()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a" },
            new() { Topic = "b" }
        });
        _mockSubMgr.Remove(Arg.Any<List<string>>()).Returns(Task.CompletedTask);
        var cut = Render<Subscriptions>();

        // Select all via the header checkbox, then click Remove
        var checkboxes = cut.FindAll("input[type='checkbox']");
        checkboxes[0].Change(true);
        cut.Find("button[title='Remove']").Click();

        await _mockSubMgr.Received(1).Remove(Arg.Is<List<string>>(l =>
            l != null && l.Count == 2 && l.Contains("a") && l.Contains("b")));
    }

    [Test]
    public void NotAuthorized_RendersDisabledEditor()
    {
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a/topic" }
        });

        var cut = Render<Subscriptions>();

        cut.Find("button[title='Add subscription']").HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public async Task DeleteSelected_WhenRemoveFails_SurfacesErrorSnackbar()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a/topic" }
        });
        _mockSubMgr.Remove(Arg.Any<List<string>>())
            .Returns(Task.FromException(new InvalidOperationException("broker rejected")));
        var cut = Render<Subscriptions>();

        cut.Find("input[type='checkbox']").Change(true);
        cut.Find("button[title='Remove']").Click();

        cut.WaitForAssertion(() =>
            _mockSnackbar.Received().Add(
                Arg.Is<string>(m => m!.Contains("Failed to unsubscribe")),
                Severity.Error,
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string>()));
    }

    [Test]
    public async Task ExcludeRemove_WhenRemoveFails_SurfacesErrorSnackbar()
    {
        AuthorizeAsOperator();
        _mockExcludeService.TopicExcludes.Returns(new List<string> { "$SYS/#" });
        _mockExcludeService.Remove(Arg.Any<IReadOnlyList<string>>())
            .Returns(Task.FromException<TopicExcludeOperationResult>(
                new InvalidOperationException("storage error")));
        var cut = Render<Subscriptions>();

        cut.Find("input[type='checkbox']").Change(true);
        cut.Find("button[title='Remove']").Click();

        cut.WaitForAssertion(() =>
            _mockSnackbar.Received().Add(
                Arg.Is<string>(m => m!.Contains("storage error")),
                Severity.Error,
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string>()));
    }

    [Test]
    public async Task ExcludeRemove_UnsuccessfulResult_ShowsFeedbackSnackbar()
    {
        AuthorizeAsOperator();
        _mockExcludeService.TopicExcludes.Returns(new List<string> { "$SYS/#" });
        _mockExcludeService.Remove(Arg.Any<IReadOnlyList<string>>())
            .Returns(Task.FromResult(new TopicExcludeOperationResult(false,
                new UserNotification(UserNotificationSeverity.Error, "Failed to save topic exclusions"))));
        var cut = Render<Subscriptions>();

        cut.Find("input[type='checkbox']").Change(true);
        cut.Find("button[title='Remove']").Click();

        cut.WaitForAssertion(() =>
            _mockSnackbar.Received().Add(
                Arg.Is<string>(m => m!.Contains("Failed to save topic exclusions")),
                Severity.Error,
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string>()));
    }

    [Test]
    public void SubscriptionCountChip_PresentInPanelHeader()
    {
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a/topic" },
            new() { Topic = "b/topic" }
        });

        var cut = Render<Subscriptions>();

        var chip = cut.Find(".count-chip");
        chip.TextContent.Should().Be("2");
    }

    [Test]
    public void SubscriptionCountChip_ShowsZeroWhenNoSubscriptions()
    {
        var cut = Render<Subscriptions>();

        var chip = cut.Find(".count-chip");
        chip.TextContent.Should().Be("0");
    }

    [Test]
    public void BothEditors_Present_WhenBothHaveItems()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a/topic" }
        });
        _mockExcludeService.TopicExcludes.Returns(new List<string> { "$SYS/#" });

        var cut = Render<Subscriptions>();

        cut.Find(".subscription-editor").Should().NotBeNull();
        cut.Find(".exclude-editor").Should().NotBeNull();
    }

    [Test]
    public void ExcludeEditor_CollapsedWhenExcludesEmpty()
    {
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a/topic" }
        });
        _mockExcludeService.TopicExcludes.Returns(Array.Empty<string>());

        var cut = Render<Subscriptions>();

        var excludePanel = cut.FindComponent<ExcludeEditor>();
        excludePanel.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeFalse();
    }

    [Test]
    public void BothEditors_HaveIndependentExpansionPanels()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a/topic" }
        });
        _mockExcludeService.TopicExcludes.Returns(new List<string> { "$SYS/#" });

        var cut = Render<Subscriptions>();

        // Both editors should have their own MudExpansionPanel
        var panels = cut.FindComponents<MudExpansionPanel>();
        panels.Should().HaveCount(2);
    }

    [Test]
    public void SubsBody_ContainsBothEditors_WithAccessibleRegions()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a/topic" }
        });
        _mockExcludeService.TopicExcludes.Returns(new List<string> { "$SYS/#" });

        var cut = Render<Subscriptions>();

        cut.Find(".subs-body").Should().NotBeNull();

        var subEditor = cut.Find(".subscription-editor");
        subEditor.GetAttribute("role").Should().Be("region");
        subEditor.GetAttribute("aria-label").Should().Be("Subscriptions list");
        subEditor.GetAttribute("tabindex").Should().Be("0");

        var excEditor = cut.Find(".exclude-editor");
        excEditor.GetAttribute("role").Should().Be("region");
        excEditor.GetAttribute("aria-label").Should().Be("Excluded topics list");
        excEditor.GetAttribute("tabindex").Should().Be("0");
    }
}
