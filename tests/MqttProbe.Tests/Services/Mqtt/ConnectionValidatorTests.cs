using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Shared.Tests.Services.Mqtt;

[TestFixture]
public class ConnectionValidatorTests
{
    private ConnectionValidator _validator = null!;

    [SetUp]
    public void Setup() => _validator = new ConnectionValidator();

    private static Connection ValidConnection() =>
        new() { Name = "Test", Host = "localhost", Port = 1883 };

    [Test]
    public async Task Validate_FailsWhenNameIsEmpty()
    {
        var conn = ValidConnection();
        conn.Name = string.Empty;

        var result = await _validator.ValidateAsync(conn);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(Connection.Name));
    }

    [Test]
    public async Task Validate_FailsWhenHostIsEmpty()
    {
        var conn = ValidConnection();
        conn.Host = string.Empty;

        var result = await _validator.ValidateAsync(conn);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(Connection.Host));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(65536)]
    public async Task Validate_FailsWhenPortIsOutsideValidRange(int port)
    {
        var conn = ValidConnection();
        conn.Port = port;

        var result = await _validator.ValidateAsync(conn);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(Connection.Port) &&
            e.ErrorMessage.Contains("1 and 65535", StringComparison.Ordinal));
    }

    [TestCase("bad host name")]
    [TestCase("mqtt://broker.local")]
    [TestCase("host:1883")]
    public async Task Validate_FailsWhenHostIsMalformed(string host)
    {
        var conn = ValidConnection();
        conn.Host = host;

        var result = await _validator.ValidateAsync(conn);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(Connection.Host) &&
            e.ErrorMessage.Contains("valid hostname or IP address", StringComparison.Ordinal));
    }

    [TestCase("broker.local")]
    [TestCase("localhost")]
    [TestCase("192.168.1.10")]
    [TestCase("2001:db8::1")]
    public async Task Validate_PassesForValidHostFormats(string host)
    {
        var conn = ValidConnection();
        conn.Host = host;

        var result = await _validator.ValidateAsync(conn);

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task Validate_PassesForValidConnection()
    {
        var result = await _validator.ValidateAsync(ValidConnection());

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task ValidateValue_ForName_FailsOnEmptyString()
    {
        var conn = ValidConnection();
        conn.Name = string.Empty;

        var errors = await _validator.ValidateValue(conn, nameof(Connection.Name));

        errors.Should().NotBeEmpty();
    }

    [Test]
    public async Task ValidateValue_ForHost_FailsOnEmptyString()
    {
        var conn = ValidConnection();
        conn.Host = string.Empty;

        var errors = await _validator.ValidateValue(conn, nameof(Connection.Host));

        errors.Should().NotBeEmpty();
    }

    [Test]
    public void ValidateValue_IsCompatibleWithMudBlazorFormDelegate()
    {
        // MudBlazor Validation expects Func<object, string, Task<IEnumerable<string>>>
        Func<object, string, Task<IEnumerable<string>>> delegate_ = _validator.ValidateValue;

        delegate_.Should().NotBeNull();
    }

    [Test]
    public async Task Validate_NoDuplicateErrors_OneErrorPerInvalidField()
    {
        var conn = ValidConnection();
        conn.Name = string.Empty;
        conn.Host = string.Empty;

        var result = await _validator.ValidateAsync(conn);

        var nameErrors = result.Errors.Where(e => e.PropertyName == nameof(Connection.Name));
        var hostErrors = result.Errors.Where(e => e.PropertyName == nameof(Connection.Host));

        nameErrors.Should().HaveCount(1, "only one rule per field");
        hostErrors.Should().HaveCount(1, "only one rule per field");
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(121)]
    [TestCase(300)]
    public async Task Validate_FailsWhenConnectTimeoutIsOutsideValidRange(int timeout)
    {
        var conn = ValidConnection();
        conn.ConnectTimeout = timeout;

        var result = await _validator.ValidateAsync(conn);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(Connection.ConnectTimeout) &&
            e.ErrorMessage.Contains("1 and 120", StringComparison.Ordinal));
    }

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(15)]
    [TestCase(60)]
    [TestCase(120)]
    public async Task Validate_PassesForValidConnectTimeout(int timeout)
    {
        var conn = ValidConnection();
        conn.ConnectTimeout = timeout;

        var result = await _validator.ValidateAsync(conn);

        result.IsValid.Should().BeTrue();
    }
}
