extern alias benchmarks;

using System.Net;
using System.Net.Sockets;
using benchmarks::MqttProbe.Benchmarks.V5Traffic;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
public class V5TrafficRunnerValidationTests
{
    [Test]
    public async Task RunAsync_RejectsInvalidOptionsBeforeConnecting()
    {
        var cases = new (string Label, V5TrafficOptions Options)[]
        {
            ("blank host", new V5TrafficOptions
            {
                Host = " ", Port = 1, Scenarios = [V5TrafficScenarioName.AbsentProperties],
                Interval = TimeSpan.Zero,
            }),
            ("port zero", new V5TrafficOptions
            {
                Port = 0, Scenarios = [V5TrafficScenarioName.AbsentProperties],
                Interval = TimeSpan.Zero,
            }),
            ("wildcard prefix", new V5TrafficOptions
            {
                Host = "127.0.0.1", Port = 1, Prefix = "benchmarks/+",
                Scenarios = [V5TrafficScenarioName.AbsentProperties], Interval = TimeSpan.Zero,
            }),
            ("no scenarios", new V5TrafficOptions
            {
                Host = "127.0.0.1", Port = 1, Scenarios = [], Interval = TimeSpan.Zero,
            }),
            ("zero repeat", new V5TrafficOptions
            {
                Host = "127.0.0.1", Port = 1, Scenarios = [V5TrafficScenarioName.AbsentProperties],
                Repeat = 0, Interval = TimeSpan.Zero,
            }),
            ("negative interval", new V5TrafficOptions
            {
                Host = "127.0.0.1", Port = 1, Scenarios = [V5TrafficScenarioName.AbsentProperties],
                Interval = TimeSpan.FromMilliseconds(-1),
            }),
            ("password without username", new V5TrafficOptions
            {
                Host = "127.0.0.1", Port = 1, Scenarios = [V5TrafficScenarioName.AbsentProperties],
                Password = "secret", Interval = TimeSpan.Zero,
            }),
            ("CA without TLS", new V5TrafficOptions
            {
                Host = "127.0.0.1", Port = 1, Scenarios = [V5TrafficScenarioName.AbsentProperties],
                CaCertificatePath = "ca.pem", Interval = TimeSpan.Zero,
            }),
        };

        foreach (var (label, options) in cases)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var exitCode = await V5TrafficRunner.RunAsync(options, cts.Token);
            exitCode.Should().Be(1, $"{label} should fail validation before connecting");
        }
    }

    [Test]
    public async Task RunAsync_AlreadyCancelled_Returns130()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exitCode = await V5TrafficRunner.RunAsync(new V5TrafficOptions
        {
            Host = "127.0.0.1",
            Port = 1,
            Scenarios = [V5TrafficScenarioName.AbsentProperties],
            Interval = TimeSpan.Zero,
        }, cts.Token);

        exitCode.Should().Be(130);
    }

    [Test]
    public async Task RunAsync_CancelledWhileAwaitingConnAck_Returns130()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var socket = await listener.AcceptSocketAsync();
                accepted.TrySetResult();
                await release.Task;
            }
            catch (SocketException)
            {
            }
        });

        try
        {
            using var cts = new CancellationTokenSource();
            var runTask = V5TrafficRunner.RunAsync(new V5TrafficOptions
            {
                Host = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                Scenarios = [V5TrafficScenarioName.AbsentProperties],
                Interval = TimeSpan.Zero,
            }, cts.Token);

            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.CancelAfter(TimeSpan.FromMilliseconds(250));

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            exitCode.Should().Be(130);
        }
        finally
        {
            release.TrySetResult();
            listener.Stop();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
