namespace MqttProbe.UI.Components.Settings;

internal static class LegalDocuments
{
    internal const string LicenseResourceName = "MqttProbe.UI.Legal.LICENSE";
    internal const string NoticesResourceName = "MqttProbe.UI.Legal.NOTICES";

    internal static string ReadLicense() => Read(LicenseResourceName, "The licence text isn't included in this build.");

    internal static string ReadNotices() => Read(NoticesResourceName, "The third-party notices aren't included in this build.");

    private static string Read(string resourceName, string missing)
    {
        using var stream = typeof(LegalDocuments).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return missing;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
    }
}
