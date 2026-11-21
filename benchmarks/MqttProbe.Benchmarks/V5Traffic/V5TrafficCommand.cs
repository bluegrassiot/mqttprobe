namespace MqttProbe.Benchmarks.V5Traffic;

internal static class V5TrafficCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parseResult = V5TrafficOptionsParser.Parse(args);
        if (parseResult.Options is null)
            return parseResult.ExitCode;

        var options = parseResult.Options;
        var output = new V5TrafficOutput(options);
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            return await V5TrafficRunner.RunAsync(options, cts.Token);
        }
        catch (OperationCanceledException)
        {
            output.Line("Cancelled.");
            return 130;
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            output.Line("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            output.Error($"ERROR: {output.Exception(ex)}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
