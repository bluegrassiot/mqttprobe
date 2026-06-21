using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using MqttProbe.Components.Shared;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Shared;

[TestFixture]
public class SectionErrorTests : BunitTestContext
{
    [Test]
    public void Renders_FriendlyMessage_AndReloadAction()
    {
        EnsureMudProviders();

        var cut = Render<SectionError>();

        cut.Markup.Should().Contain("Something went wrong");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Reload"));
    }

    [Test]
    public void ErrorBoundary_WhenChildThrows_RendersSectionErrorContent()
    {
        EnsureMudProviders();

        var cut = Render<ErrorBoundary>(p => p
            .Add(e => e.ChildContent, (RenderFragment)(b =>
            {
                b.OpenComponent<ThrowingChild>(0);
                b.CloseComponent();
            }))
            .Add(e => e.ErrorContent, (RenderFragment<Exception>)(_ => b =>
            {
                b.OpenComponent<SectionError>(0);
                b.CloseComponent();
            })));

        cut.Markup.Should().Contain("Something went wrong");
        cut.Markup.Should().NotContain("boom", "internal exception detail must not leak to the UI");
    }

    private sealed class ThrowingChild : ComponentBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("boom");

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
        }
    }
}
