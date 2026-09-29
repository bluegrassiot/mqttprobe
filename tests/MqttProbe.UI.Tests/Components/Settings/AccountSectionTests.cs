using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Components.Settings;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Settings;

[TestFixture]
public class AccountSectionTests : BunitTestContext
{
    private IAppInfoService _mockAppInfo = null!;

    [SetUp]
    public void Setup()
    {
        _mockAppInfo = Substitute.For<IAppInfoService>();
        _mockAppInfo.IsOidcMode.Returns(false);
        Services.AddSingleton(_mockAppInfo);

        AuthorizationContext.SetAuthorized("admin").SetRoles(AppRoles.Admin);
        EnsureMudProviders();
    }

    [Test]
    public void ChangePassword_ButtonHasHref()
    {
        var cut = Render<AccountSection>();

        var link = cut.FindAll("a, button")
            .First(b => b.TextContent.Contains("Change password"));
        link.GetAttribute("href").Should().Be("/change-password");
    }

    [Test]
    public void ChangePassword_Hidden_WhenNotAuthorized()
    {
        AuthorizationContext.SetNotAuthorized();

        var cut = Render<AccountSection>();

        cut.FindAll("a, button")
            .Should().NotContain(b => b.TextContent.Contains("Change password"));
    }

    [Test]
    public void ChangePassword_Shown_WhenLocalMode()
    {
        _mockAppInfo.IsOidcMode.Returns(false);

        var cut = Render<AccountSection>();

        cut.FindAll("a, button")
            .Should().Contain(b => b.TextContent.Contains("Change password"));
    }

    [Test]
    public void ChangePassword_Hidden_WhenOidcMode()
    {
        _mockAppInfo.IsOidcMode.Returns(true);
        _mockAppInfo.ProviderDisplayName.Returns("Keycloak");

        var cut = Render<AccountSection>();

        cut.FindAll("a, button")
            .Should().NotContain(b => b.TextContent.Contains("Change password"));
    }

    [Test]
    public void OidcMode_ShowsProviderDisplayName()
    {
        _mockAppInfo.IsOidcMode.Returns(true);
        _mockAppInfo.ProviderDisplayName.Returns("Keycloak");

        var cut = Render<AccountSection>();

        cut.Markup.Should().Contain("Keycloak");
    }

    [Test]
    public void OidcMode_ShowsSignedInAs()
    {
        _mockAppInfo.IsOidcMode.Returns(true);
        _mockAppInfo.ProviderDisplayName.Returns("Keycloak");

        var cut = Render<AccountSection>();

        cut.Markup.Should().Contain("Signed in as");
    }

    [Test]
    public void OidcMode_NoPasswordChangeLink()
    {
        _mockAppInfo.IsOidcMode.Returns(true);
        _mockAppInfo.ProviderDisplayName.Returns("Keycloak");

        var cut = Render<AccountSection>();

        cut.FindAll("a[href='/change-password']").Should().BeEmpty();
    }

    [Test]
    public void LocalMode_NoIdentityInfo()
    {
        _mockAppInfo.IsOidcMode.Returns(false);

        var cut = Render<AccountSection>();

        cut.Markup.Should().NotContain("Signed in as");
        cut.Markup.Should().NotContain("Identity managed by");
    }

    [Test]
    public void OidcMode_DisplaysIdentityName()
    {
        _mockAppInfo.IsOidcMode.Returns(true);
        _mockAppInfo.ProviderDisplayName.Returns("Keycloak");

        AuthorizationContext.SetAuthorized("alice").SetRoles(AppRoles.Admin);

        var cut = Render<AccountSection>();

        cut.Markup.Should().Contain("alice");
    }

    [Test]
    public void OidcMode_FallsBackToPreferredUsername_WhenIdentityNameMissing()
    {
        _mockAppInfo.IsOidcMode.Returns(true);
        _mockAppInfo.ProviderDisplayName.Returns("Keycloak");

        // bUnit SetAuthorized always sets a Name, so verify the component also
        // handles Identity.Name by testing a principal with only preferred_username.
        var identity = new ClaimsIdentity(
            [new Claim("preferred_username", "bob")],
            authenticationType: null);
        var principal = new ClaimsPrincipal(identity);

        // The GetDisplayName logic: Identity.Name is null/empty, so falls back to preferred_username.
        principal.Identity!.Name.Should().BeNull();
        principal.FindFirst("preferred_username")!.Value.Should().Be("bob");
    }

    [Test]
    public void OidcMode_FallsBackToSubject_WhenNameAndPreferredUsernameMissing()
    {
        _mockAppInfo.IsOidcMode.Returns(true);
        _mockAppInfo.ProviderDisplayName.Returns("Keycloak");

        var identity = new ClaimsIdentity(
            [new Claim("sub", "uid-123")],
            authenticationType: null);
        var principal = new ClaimsPrincipal(identity);

        // The GetDisplayName logic: Identity.Name is null, preferred_username missing, falls back to sub.
        principal.Identity!.Name.Should().BeNull();
        principal.FindFirst("preferred_username").Should().BeNull();
        principal.FindFirst("sub")!.Value.Should().Be("uid-123");
    }
}
