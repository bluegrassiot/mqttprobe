#if IOS
using Foundation;
using MqttProbe.Services.Security;

namespace MqttProbe.Maui.Services;

public class iOSFileProtector : IFileProtector
{
    public bool ApplyProtections(string path)
    {
        bool backupOk = false;
        bool protectionOk = false;

        try
        {
            var url = NSUrl.FromFilename(path);
            backupOk = url.SetResource(NSUrl.IsExcludedFromBackupKey, new NSNumber(true), out _);
        }
        catch { }

        try
        {
            var attrs = new NSFileAttributes
            {
                ProtectionKey = NSFileProtection.Complete
            };
            protectionOk = NSFileManager.DefaultManager.SetAttributes(attrs, path, out _);
        }
        catch { }

        return backupOk && protectionOk;
    }

    public bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    public bool TryMoveToQuarantine(string sourcePath, string quarantinePath)
    {
        try { File.Move(sourcePath, quarantinePath); return true; }
        catch { return false; }
    }
}
#endif
