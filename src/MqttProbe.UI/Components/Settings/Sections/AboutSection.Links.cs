namespace MqttProbe.UI.Components.Settings;

public partial class AboutSection
{
    // App Store review requires a reachable privacy policy; the address is not configurable.
    public static readonly Uri PrivacyPolicyUri = new("https://github.com/bluegrassiot/mqttprobe/blob/main/PRIVACY.md");

    public const string ContactEmailAddress = "info@bluegrassiot.com";

    public const string ContactEmailUri = "mailto:" + ContactEmailAddress;

    public static readonly Uri SupportUri = new("https://mqttprobe.com/support");

    // The published policy exists for the App Store listing, so only iOS hosts link to it.
    protected virtual bool IsIosHost => OperatingSystem.IsIOS();
}
