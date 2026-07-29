using System.Globalization;

namespace MqttProbe.Services.Plugins.Packaging;

public static class PluginVersion
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        char[] separators = ['-', '+'];
        var core = value.Split(separators)[0];
        var parts = core.Split('.');

        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var numbers = new int[4];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }
}
