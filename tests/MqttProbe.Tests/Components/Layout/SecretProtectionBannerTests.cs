using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Layout;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Layout;

[TestFixture]
public class SecretProtectionBannerTests : BunitTestContext
{
    [Test]
    public void WhenNoStatusRegistered_ShowsNoAlert()
    {
        var cut = Render<SecretProtectionBanner>();

        cut.Markup.Should().NotContain("key storage is unavailable");
        cut.FindAll(".secret-protection-banner").Should().BeEmpty();
    }

    [Test]
    public void WhenFileFallback_ShowsWarning()
    {
        Services.AddSingleton<ISecretProtectionStatus>(
            new FakeStatus { Mode = SecretProtectionMode.FileFallback });

        var cut = Render<SecretProtectionBanner>();

        cut.Markup.Should().Contain("key storage is unavailable");
        var alert = cut.FindComponent<MudAlert>();
        alert.Instance.Severity.Should().Be(Severity.Warning);
        alert.Instance.Variant.Should().Be(Variant.Outlined);
        cut.FindAll(".secret-protection-banner").Should().ContainSingle();
    }

    [Test]
    public void WhenOsKeyring_ShowsNoAlert()
    {
        Services.AddSingleton<ISecretProtectionStatus>(
            new FakeStatus { Mode = SecretProtectionMode.OsKeyring });

        var cut = Render<SecretProtectionBanner>();

        cut.Markup.Should().NotContain("key storage is unavailable");
        cut.FindAll(".secret-protection-banner").Should().BeEmpty();
    }

    [Test]
    public void WhenStatusThrowsInvalidOperationException_ShowsNoAlert()
    {
        Services.AddSingleton<ISecretProtectionStatus>(new ThrowingStatus());

        var cut = Render<SecretProtectionBanner>();

        cut.Markup.Should().NotContain("key storage is unavailable");
        cut.FindAll(".secret-protection-banner").Should().BeEmpty();
    }

    private sealed class FakeStatus : ISecretProtectionStatus
    {
        public SecretProtectionMode Mode { get; init; }
    }

    private sealed class ThrowingStatus : ISecretProtectionStatus
    {
        public SecretProtectionMode Mode => throw new InvalidOperationException("not initialized");
    }
}
