using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class TopicExcludeServiceTests
{
    private IConnectionSettings _settings = null!;
    private SessionState _session = null!;
    private TopicExcludeService _service = null!;

    [SetUp]
    public void Setup()
    {
        _settings = Substitute.For<IConnectionSettings>();
        _session = new SessionState
        {
            SelectedConnection = new Connection { Id = Guid.NewGuid(), Name = "Test" }
        };
        _settings.Connections.Returns([]);
        _service = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(), _settings, _session);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    [Test]
    public async Task Add_PersistsImmediatelyAndMatchesMqttWildcards()
    {
        await _service.Add("sensors/#");

        _service.TopicExcludes.Should().ContainSingle().Which.Should().Be("sensors/#");
        _service.IsExcluded("sensors/temperature").Should().BeTrue();
        await _settings.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.TopicExcludes.Contains("sensors/#")));
    }

    [Test]
    public async Task Add_HashAlone_IsAllowed()
    {
        await _service.Add("#");

        _service.IsExcluded("anything").Should().BeTrue();
        _service.IsExcluded("$SYS/broker/uptime").Should().BeTrue();
    }

    [Test]
    public async Task Add_Duplicate_IsRejected()
    {
        await _service.Add("duplicate");
        await _service.Add("duplicate");

        _service.TopicExcludes.Should().ContainSingle();
        var result = _service.ValidateAdd("duplicate");
        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("Already");
    }

    [Test]
    public async Task Add_InvalidFilter_DoesNotMutateState()
    {
        await _service.Add("sensors/#/invalid");

        _service.TopicExcludes.Should().BeEmpty();
        _service.ValidateAdd("sensors/#/invalid").IsValid.Should().BeFalse();
    }

    [Test]
    public async Task Add_PersistenceFailure_DoesNotMutateState()
    {
        _settings.AddConnectionAsync(Arg.Any<Connection>())
            .Returns(Task.FromException(new IOException("disk full")));

        var result = await _service.Add("failed/topic");

        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("Failed");
        _service.TopicExcludes.Should().BeEmpty();
        _session.SelectedConnection.TopicExcludes.Should().BeEmpty();
    }

    [Test]
    public async Task Add_AtLimit_IsRejected()
    {
        for (var i = 0; i < 500; i++)
            await _service.Add($"topic/{i}");

        await _service.Add("topic/501");

        _service.TopicExcludes.Should().HaveCount(500);
        var result = _service.ValidateAdd("topic/501");
        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("limit");
    }

    [Test]
    public void SelectedConnectionChanged_LoadsThatConnectionsExcludes()
    {
        var next = new Connection
        {
            Id = Guid.NewGuid(),
            TopicExcludes = ["new/#"]
        };

        _session.SelectedConnection = next;

        _service.TopicExcludes.Should().Equal("new/#");
        _service.IsExcluded("new/topic").Should().BeTrue();
        _service.IsExcluded("old/topic").Should().BeFalse();
    }

    [Test]
    public void SelectedConnectionChanged_CapsLoadedExcludesAt500()
    {
        _session.SelectedConnection = new Connection
        {
            Id = Guid.NewGuid(),
            TopicExcludes = Enumerable.Range(0, 501).Select(i => $"topic/{i}").ToList()
        };

        _service.TopicExcludes.Should().HaveCount(500);
    }

    [Test]
    public async Task Remove_PersistsRemainingExcludes()
    {
        await _service.Add("keep");
        await _service.Add("remove");
        _settings.ClearReceivedCalls();

        await _service.Remove(["remove"]);

        _service.TopicExcludes.Should().Equal("keep");
        await _settings.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.TopicExcludes.SequenceEqual(new[] { "keep" })));
    }

    [Test]
    public async Task Add_WhenActiveConnectionChangesDuringPersistence_DoesNotMutateNewConnection()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        var oldConnection = _session.SelectedConnection;
        var newConnection = new Connection
        {
            Id = Guid.NewGuid(),
            TopicExcludes = ["new/#"]
        };
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(_ =>
        {
            _session.SelectedConnection = newConnection;
            return Task.CompletedTask;
        });

        var result = await _service.Add("old/#");

        result.IsValid.Should().BeFalse();
        _session.SelectedConnection.Should().BeSameAs(newConnection);
        _service.TopicExcludes.Should().Equal("new/#");
        oldConnection.TopicExcludes.Should().Contain("old/#");
        published.Should().NotContain("old/#");
        published.Should().Contain("new/#");
        await _settings.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.Id == oldConnection.Id));
    }
}
