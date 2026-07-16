namespace MqttProbe.Desktop.Services.Security;

public static class MasterKeyConstants
{
    public const int KeySize = 32;
    public const string RawKeyFileName = ".key";
    public const string WindowsDpapiBlobFileName = ".master-key-v1.dpapi";
    public const string KeyVersionId = "master-key-v1";
    public const string AppServiceId = "com.bluegrassiot.mqttprobe";
}
