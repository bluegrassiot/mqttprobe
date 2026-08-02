using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class SubscriptionsTests : BunitTestContext
{
    private ISubscriptionManager _mockSubMgr = null!;
    private ISnackbar _mockSnackbar = null!;
    private IDialogService _mockDialog = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockSubMgr = Substitute.For<ISubscriptionManager>();
        _mockSubMgr.Subscriptions.Returns(Array.Empty<SubscribedTopic>());
        Services.AddSingleton(_mockSubMgr);

        _mockSnackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_mockSnackbar);

        _mockDialog = Substitute.For<IDialogService>();
        _mockDialog.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));
        Services.AddSingleton(_mockDialog);

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
        markup.Should().Contain("AtLeastOnce");
        markup.Should().Contain("AtMostOnce");
    }

    [Test]
    public async Task AddButton_CallsSubscribeAsync_WithEnteredTopic()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Add(Arg.Any<string>(), Arg.Any<MqttQualityOfServiceLevel>()).Returns(Task.CompletedTask);
        var cut = Render<Subscriptions>();

        var topicInput = cut.FindAll("input").First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox");
        topicInput.Change("sensors/#");
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
        topicInput.Change("sensors/#");
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
        topicInput.Change("bad/#");
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
    public async Task ClearAll_RemovesAllSubscriptions()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Subscriptions.Returns(new List<SubscribedTopic>
        {
            new() { Topic = "a" },
            new() { Topic = "b" }
        });
        _mockSubMgr.Remove(Arg.Any<List<string>>()).Returns(Task.CompletedTask);
        var cut = Render<Subscriptions>();

        cut.Find("button[title='Clear all']").Click();
        await cut.InvokeAsync(() => { });

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
}
