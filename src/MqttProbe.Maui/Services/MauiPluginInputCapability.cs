using MqttProbe.Services.Plugins.Packaging;

namespace MqttProbe.Maui.Services;

public class MauiPluginInputCapability : IPluginInputCapability
{
    public bool UsesInputFileComponent => false;
}
