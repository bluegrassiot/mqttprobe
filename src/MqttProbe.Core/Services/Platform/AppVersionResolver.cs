using System.Text.RegularExpressions;

namespace MqttProbe.Core.Services.Platform;

public static partial class AppVersionResolver
{
#pragma warning disable MA0009 // GeneratedRegex with constant pattern has no backtracking risk
    [GeneratedRegex(@"^\d+\.\d+\.\d+\.0$")]
    private static partial Regex TrailingZeroRevision();
#pragma warning restore MA0009

    public static string Resolve(params Func<string?>[] providers)
    {
        foreach (var provider in providers)
        {
            string? value;
            try
            {
                value = provider();
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
                continue;

            return Normalize(TrimAfterPlus(value.Trim()));
        }

        return "unknown";
    }

    private static string TrimAfterPlus(string version)
    {
        var index = version.IndexOf('+');
        return index != -1 ? version[..index] : version;
    }

    private static string Normalize(string version) =>
        TrailingZeroRevision().IsMatch(version)
            ? version[..version.LastIndexOf('.')]
            : version;
}
