namespace MqttProbe.Benchmarks.V5Traffic;

internal static class V5TrafficRedaction
{
    public static string Redact(string value, V5TrafficOptions options)
    {
        foreach (var secret in Secrets(options).OrderByDescending(s => s.Length))
            value = value.Replace(secret, "***", StringComparison.Ordinal);

        return value;
    }

    public static string DescribeException(Exception exception, V5TrafficOptions options)
    {
        var message = exception.Message;
        var baseMessage = exception.GetBaseException().Message;
        if (!string.IsNullOrEmpty(baseMessage)
            && !baseMessage.Equals(message, StringComparison.Ordinal))
        {
            message = $"{message} ({baseMessage})";
        }

        return Redact(message, options);
    }

    private static IEnumerable<string> Secrets(V5TrafficOptions options)
    {
        if (!string.IsNullOrEmpty(options.Username))
            yield return options.Username;
        if (!string.IsNullOrEmpty(options.Password))
            yield return options.Password;
        if (!string.IsNullOrEmpty(options.CaCertificatePassword))
            yield return options.CaCertificatePassword;
        if (!string.IsNullOrEmpty(options.ClientCertificatePassword))
            yield return options.ClientCertificatePassword;
    }
}
