namespace MqttProbe.Services.Platform;

public static class AppVersionResolver
{
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

            return TrimAfterPlus(value);
        }

        return "unknown";
    }

    private static string TrimAfterPlus(string version)
    {
        var index = version.IndexOf('+');
        return index != -1 ? version[..index] : version;
    }
}
