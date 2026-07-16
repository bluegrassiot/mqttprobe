using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MqttProbe.Components.Shared;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Shared;

[TestFixture]
public class AppButtonTests : BunitTestContext
{
    [Test]
    public void Primary_RendersFilledPrimaryCssClasses()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.ClassList.Should().Contain("mud-button-filled");
        btn.ClassList.Should().Contain("mud-button-filled-primary");
    }

    [Test]
    public void Primary_IncludesAppBtnMin_WhenMinWidthTrue()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .Add(x => x.MinWidth, true)
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.ClassList.Should().Contain("app-btn-min");
    }

    [Test]
    public void Primary_CombinesClassAndAppBtnMin_WhenBothProvided()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .Add(x => x.MinWidth, true)
            .Add(x => x.Class, "my-custom")
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.ClassList.Should().Contain("app-btn-min");
        btn.ClassList.Should().Contain("my-custom");
    }

    [Test]
    public void Secondary_RendersOutlinedPrimaryCssClasses()
    {
        EnsureMudProviders();

        var cut = Render<AppSecondaryButton>(p => p
            .AddChildContent("Cancel"));

        var btn = cut.Find("button");
        btn.ClassList.Should().Contain("mud-button-outlined");
        btn.ClassList.Should().Contain("mud-button-outlined-primary");
    }

    [Test]
    public void Secondary_NoAppBtnMin_ByDefault()
    {
        EnsureMudProviders();

        var cut = Render<AppSecondaryButton>(p => p
            .AddChildContent("Cancel"));

        var btn = cut.Find("button");
        btn.ClassList.Should().NotContain("app-btn-min");
    }

    [Test]
    public void Tertiary_RendersTextDefaultCssClasses()
    {
        EnsureMudProviders();

        var cut = Render<AppTertiaryButton>(p => p
            .AddChildContent("Back"));

        var btn = cut.Find("button");
        btn.ClassList.Should().Contain("mud-button-text");
        btn.ClassList.Should().Contain("mud-button-text-default");
    }

    [Test]
    public void Tertiary_DoesNotRenderTextPrimary()
    {
        EnsureMudProviders();

        var cut = Render<AppTertiaryButton>(p => p
            .AddChildContent("Back"));

        var btn = cut.Find("button");
        btn.ClassList.Should().NotContain("mud-button-text-primary");
    }

    [Test]
    public void Destructive_RendersOutlinedErrorCssClasses()
    {
        EnsureMudProviders();

        var cut = Render<AppDestructiveButton>(p => p
            .AddChildContent("Delete"));

        var btn = cut.Find("button");
        btn.ClassList.Should().Contain("mud-button-outlined");
        btn.ClassList.Should().Contain("mud-button-outlined-error");
    }

    [Test]
    public void Primary_OnClick_InvokesCallback()
    {
        EnsureMudProviders();

        var clicked = false;
        var cut = Render<AppPrimaryButton>(p => p
            .Add(x => x.OnClick, EventCallback.Factory.Create<MouseEventArgs>(this, () => clicked = true))
            .AddChildContent("Save"));

        cut.Find("button").Click();
        clicked.Should().BeTrue();
    }

    [Test]
    public void Primary_ForwardsUnmatchedAttributes()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .AddUnmatched("data-testid", "btn-save")
            .AddUnmatched("title", "Save changes")
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.GetAttribute("data-testid").Should().Be("btn-save");
        btn.GetAttribute("title").Should().Be("Save changes");
    }

    [Test]
    public void Primary_RendersChildContent()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .AddChildContent("Publish"));

        cut.Find("button").TextContent.Should().Contain("Publish");
    }

    [Test]
    public void Primary_DefaultsToSizeMedium()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.ClassList.Should().Contain("mud-button-filled-size-medium");
    }

    [Test]
    public void Primary_RespectsExplicitSize()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .Add(x => x.Size, MudBlazor.Size.Small)
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.ClassList.Should().Contain("mud-button-filled-size-small");
    }

    [Test]
    public void Primary_RespectsDisabled()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .Add(x => x.Disabled, true)
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void Primary_RespectsFullWidth()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .Add(x => x.FullWidth, true)
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.ClassList.Should().Contain("mud-width-full");
    }

    [Test]
    public void Primary_RespectsButtonType()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .Add(x => x.ButtonType, MudBlazor.ButtonType.Submit)
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.GetAttribute("type").Should().Be("submit");
    }

    [Test]
    public void Primary_DefaultsToButtonTypeButton()
    {
        EnsureMudProviders();

        var cut = Render<AppPrimaryButton>(p => p
            .AddChildContent("Save"));

        var btn = cut.Find("button");
        btn.GetAttribute("type").Should().Be("button");
    }
}
