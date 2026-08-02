using MqttProbe.Services.Platform;

namespace MqttProbe.Shared.Tests.Services.Platform;

[TestFixture]
public class AppVersionResolverTests
{
    [Test]
    public void Resolve_ReturnsFirstNonEmptyProviderResult()
    {
        var result = AppVersionResolver.Resolve(
            () => "1.2.3",
            () => "9.9.9");

        result.Should().Be("1.2.3");
    }

    [Test]
    public void Resolve_SkipsNullEmptyAndWhitespaceProviders()
    {
        var result = AppVersionResolver.Resolve(
            () => null,
            () => "",
            () => "   ",
            () => "2.0.0");

        result.Should().Be("2.0.0");
    }

    [Test]
    public void Resolve_SkipsProvidersThatThrow()
    {
        var result = AppVersionResolver.Resolve(
            () => throw new InvalidOperationException("unavailable"),
            () => "3.1.4");

        result.Should().Be("3.1.4");
    }

    [Test]
    public void Resolve_TrimsSourceLinkSuffixAfterPlus()
    {
        var result = AppVersionResolver.Resolve(() => "1.2.3+abc123");

        result.Should().Be("1.2.3");
    }

    [Test]
    public void Resolve_WhenAllProvidersFail_ReturnsUnknown()
    {
        var result = AppVersionResolver.Resolve(
            () => null,
            () => throw new InvalidOperationException("fail"),
            () => "  ");

        result.Should().Be("unknown");
    }

    [Test]
    public void Resolve_WhenNoProviders_ReturnsUnknown()
    {
        AppVersionResolver.Resolve().Should().Be("unknown");
    }

    [Test]
    public void Resolve_StripsTrailingZeroFourthPart()
    {
        AppVersionResolver.Resolve(() => "1.2.3.0").Should().Be("1.2.3");
    }

    [Test]
    public void Resolve_KeepsThreePartVersion()
    {
        AppVersionResolver.Resolve(() => "1.2.3").Should().Be("1.2.3");
    }

    [Test]
    public void Resolve_KeepsThreePartVersionEndingInZero()
    {
        AppVersionResolver.Resolve(() => "1.0.0").Should().Be("1.0.0");
    }

    [Test]
    public void Resolve_KeepsNonZeroFourthPart()
    {
        AppVersionResolver.Resolve(() => "1.2.3.1").Should().Be("1.2.3.1");
    }

    [Test]
    public void Resolve_StripsTrailingZeroAfterPlusTrim()
    {
        AppVersionResolver.Resolve(() => "1.2.3.0+abc").Should().Be("1.2.3");
    }
}
