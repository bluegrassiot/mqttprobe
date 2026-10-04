using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Pages;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Pages;

[TestFixture]
public class AccessDeniedModelTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private IUiSettings _mockUiSettings = null!;

    [SetUp]
    public void Setup()
    {
        _mockUiSettings = Substitute.For<IUiSettings>();
        _mockUiSettings.Ui.Returns(new UiPreferences());
    }

    private static (AccessDeniedModel Model, DenialStateStore Store) CreateModel(
        AuthenticationOptions? options = null,
        IUiSettings? uiSettings = null)
    {
        var tp = new FakeTimeProvider(_epoch);
        var store = new DenialStateStore(tp);
        var ui = uiSettings ?? Substitute.For<IUiSettings>();
        if (uiSettings is null)
        {
            ui.Ui.Returns(new UiPreferences());
        }
        var model = new AccessDeniedModel(store, Options.Create(options ?? new AuthenticationOptions()), ui);
        return (model, store);
    }

    [Test]
    public void OnGet_NullState_RedirectsToLogin()
    {
        var (model, _) = CreateModel();

        var result = model.OnGet(null);

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
    }

    [Test]
    public void OnGet_EmptyState_RedirectsToLogin()
    {
        var (model, _) = CreateModel();

        var result = model.OnGet("");

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
    }

    [Test]
    public void OnGet_InvalidHandle_RedirectsToLogin()
    {
        var (model, _) = CreateModel();

        var result = model.OnGet("invalid-handle");

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
    }

    [Test]
    public void OnGet_ValidHandle_SetsDisplayName()
    {
        var (model, store) = CreateModel();
        var handle = store.Store("John Doe", "admission_denied");

        model.OnGet(handle);

        model.DisplayName.Should().Be("John Doe");
    }

    [Test]
    public void OnGet_ValidHandle_SetsCategory()
    {
        var (model, store) = CreateModel();
        var handle = store.Store("John Doe", "admission_denied");

        model.OnGet(handle);

        model.Category.Should().Be("admission_denied");
    }

    [Test]
    public void OnGet_ValidHandle_SetsProviderDisplayName()
    {
        var options = new AuthenticationOptions
        {
            Oidc = new OidcOptions { ProviderDisplayName = "Keycloak" }
        };
        var (model, store) = CreateModel(options);
        var handle = store.Store("John Doe", "admission_denied");

        model.OnGet(handle);

        model.ProviderDisplayName.Should().Be("Keycloak");
    }

    [Test]
    public void OnGet_ConsumedHandle_RedirectsToLogin()
    {
        var (model, store) = CreateModel();
        var handle = store.Store("John Doe", "admission_denied");

        model.OnGet(handle);
        var result = model.OnGet(handle);

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
    }

    [Test]
    public void IsAccessibleFontProfile_WhenStandard_ReturnsFalse()
    {
        _mockUiSettings.Ui.Returns(new UiPreferences { FontProfile = FontProfiles.Standard });

        var (model, _) = CreateModel(uiSettings: _mockUiSettings);

        model.IsAccessibleFontProfile.Should().BeFalse();
    }

    [Test]
    public void IsAccessibleFontProfile_WhenAccessible_ReturnsTrue()
    {
        _mockUiSettings.Ui.Returns(new UiPreferences { FontProfile = FontProfiles.Accessible });

        var (model, _) = CreateModel(uiSettings: _mockUiSettings);

        model.IsAccessibleFontProfile.Should().BeTrue();
    }
}
