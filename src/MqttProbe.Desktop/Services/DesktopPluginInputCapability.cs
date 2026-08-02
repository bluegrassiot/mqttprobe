using MqttProbe.Services.Plugins.Packaging;

namespace MqttProbe.Desktop.Services;

public class DesktopPluginInputCapability : IPluginInputCapability
{
    public bool UsesInputFileComponent => false;
}
