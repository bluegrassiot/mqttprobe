using MqttProbe.Core.Services.Plugins.Packaging;

namespace MqttProbe.Web.Services;

public class WebPluginInputCapability : IPluginInputCapability
{
    public bool UsesInputFileComponent => true;
}
