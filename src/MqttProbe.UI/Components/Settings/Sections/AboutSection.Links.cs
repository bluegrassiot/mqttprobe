namespace MqttProbe.UI.Components.Settings;

public partial class AboutSection
{
    // App Store review requires a reachable privacy policy; the address is not configurable.
    public static readonly Uri PrivacyPolicyUri = new("https://github.com/bluegrassiot/mqttprobe/blob/main/PRIVACY.md");

    // The published policy exists for the App Store listing, so only iOS hosts link to it.
    protected virtual bool IsIosHost => OperatingSystem.IsIOS();
}
