using MqttProbe.Benchmarks.Models;

namespace MqttProbe.Benchmarks;

public static class InteractivePrompt
{
    public static bool IsInteractive => !Console.IsInputRedirected;

    public static string PromptHost(string current)
    {
        Console.Write($"Host [{current}]: ");
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? current : input;
    }

    public static int PromptPort(int current)
    {
        while (true)
        {
            Console.Write($"Port [{current}]: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                return current;
            if (int.TryParse(input, out var p) && p > 0)
                return p;
            Console.WriteLine("  Must be a positive integer.");
        }
    }

    public static int PromptCount(int current)
    {
        while (true)
        {
            Console.Write($"Messages per format [{current}]: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                return current;
            if (int.TryParse(input, out var c) && c > 0)
                return c;
            Console.WriteLine("  Must be a positive integer.");
        }
    }

    public static int PromptRate(int current)
    {
        while (true)
        {
            Console.Write($"Rate (msg/s, 0 = unlimited) [{current}]: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                return current;
            if (int.TryParse(input, out var r) && r >= 0)
                return r;
            Console.WriteLine("  Must be >= 0.");
        }
    }

    public static int PromptConcurrency(int current)
    {
        while (true)
        {
            Console.Write($"Concurrency [{current}]: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                return current;
            if (int.TryParse(input, out var c) && c >= 1)
                return c;
            Console.WriteLine("  Must be >= 1.");
        }
    }

    public static HashSet<string>? PromptFormats(HashSet<string>? current)
    {
        var all = Enum.GetValues<PayloadFormat>();
        Console.WriteLine("Formats:");
        Console.WriteLine("  0  all");
        for (var i = 0; i < all.Length; i++)
            Console.WriteLine($"  {i + 1}  {all[i]}");

        var defaultLabel = current is { Count: > 0 } ? string.Join(",", current) : "all";
        while (true)
        {
            Console.Write($"Select formats [{defaultLabel}]: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                return current;

            if (input.Equals("all", StringComparison.OrdinalIgnoreCase)
                || input.Equals("0", StringComparison.Ordinal))
                return null;

            // Try numeric selection
            var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = true;

            foreach (var part in parts)
            {
                if (int.TryParse(part, out var num) && num >= 1 && num <= all.Length)
                {
                    results.Add(all[num - 1].ToString());
                }
                else
                {
                    // Try name match (use Enum.TryParse to handle Empty correctly)
                    if (Enum.TryParse<PayloadFormat>(part, ignoreCase: true, out var match))
                    {
                        results.Add(match.ToString());
                    }
                    else
                    {
                        Console.WriteLine($"  Unknown format: '{part}'");
                        found = false;
                        break;
                    }
                }
            }

            if (found && results.Count > 0)
                return results;

            if (found && results.Count == 0)
                Console.WriteLine("  Select at least one format or 'all'.");
        }
    }

    public static void PromptReady(string host, int port, int count, int rate, int concurrency, HashSet<string>? formats)
    {
        var formatList = formats is { Count: > 0 } ? string.Join(", ", formats) : "all";

        Console.WriteLine();
        Console.WriteLine("─".PadRight(50, '─'));
        Console.WriteLine($"  Broker     : {host}:{port}");
        Console.WriteLine($"  Count      : {count} per format");
        Console.WriteLine($"  Rate       : {(rate > 0 ? $"{rate} msg/s" : "unlimited")}");
        Console.WriteLine($"  Concurrency: {concurrency}");
        Console.WriteLine($"  Formats    : {formatList}");
        Console.WriteLine("─".PadRight(50, '─'));
        Console.WriteLine();
        Console.Write("Press Enter to start (or Ctrl+C to cancel)...");
        Console.ReadLine();
    }
}
