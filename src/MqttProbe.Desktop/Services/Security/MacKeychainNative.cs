using System.Runtime.InteropServices;

namespace MqttProbe.Desktop.Services.Security;

public sealed class MacKeychainNative : IMacKeychainNative
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int CfStringEncodingUtf8 = 0x08000100;

    private static readonly IntPtr _cfAllocatorDefault = IntPtr.Zero;

    private static readonly IntPtr _secClass;
    private static readonly IntPtr _secClassGenericPassword;
    private static readonly IntPtr _secAttrService;
    private static readonly IntPtr _secAttrAccount;
    private static readonly IntPtr _secValueData;
    private static readonly IntPtr _secReturnData;
    private static readonly IntPtr _secMatchLimit;
    private static readonly IntPtr _secMatchLimitOne;

    private static readonly IntPtr _cfTypeDictionaryKeyCallBacks;
    private static readonly IntPtr _cfTypeDictionaryValueCallBacks;

    private static readonly IntPtr _cfBooleanTrue;

    static MacKeychainNative()
    {
        var cfHandle = NativeLibrary.Load(
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
        var secHandle = NativeLibrary.Load(
            "/System/Library/Frameworks/Security.framework/Security");

        _cfBooleanTrue = ResolveCFBoolean(cfHandle, "kCFBooleanTrue");

        _secClass = ResolveCFString(secHandle, "kSecClass");
        _secClassGenericPassword = ResolveCFString(secHandle, "kSecClassGenericPassword");
        _secAttrService = ResolveCFString(secHandle, "kSecAttrService");
        _secAttrAccount = ResolveCFString(secHandle, "kSecAttrAccount");
        _secValueData = ResolveCFString(secHandle, "kSecValueData");
        _secReturnData = ResolveCFString(secHandle, "kSecReturnData");
        _secMatchLimit = ResolveCFString(secHandle, "kSecMatchLimit");
        _secMatchLimitOne = ResolveCFString(secHandle, "kSecMatchLimitOne");

        _cfTypeDictionaryKeyCallBacks = ResolveExport(cfHandle, "kCFTypeDictionaryKeyCallBacks");
        _cfTypeDictionaryValueCallBacks = ResolveExport(cfHandle, "kCFTypeDictionaryValueCallBacks");
    }

    public int CopyMatching(string service, string account, out byte[]? data)
    {
        var query = BuildQueryDict(service, account, returnData: true);
        try
        {
            var status = SecItemCopyMatching(query, out var result);
            if (status == ErrSecSuccess)
            {
                try
                {
                    var length = CFDataGetLength(result);
                    data = new byte[length];
                    Marshal.Copy(CFDataGetBytePtr(result), data, 0, (int)length);
                }
                finally
                {
                    CFRelease(result);
                }
            }
            else
            {
                data = null;
            }
            return status;
        }
        finally
        {
            CFRelease(query);
        }
    }

    public int Add(string service, string account, ReadOnlySpan<byte> data)
    {
        var attrs = BuildAddDict(service, account, data);
        try
        {
            return SecItemAdd(attrs, out _);
        }
        finally
        {
            CFRelease(attrs);
        }
    }

    public int Update(string service, string account, ReadOnlySpan<byte> data)
    {
        var query = BuildQueryDict(service, account, returnData: false);
        var updateAttrs = BuildUpdateDict(data);
        try
        {
            return SecItemUpdate(query, updateAttrs);
        }
        finally
        {
            CFRelease(query);
            CFRelease(updateAttrs);
        }
    }

    public int Delete(string service, string account)
    {
        var query = BuildQueryDict(service, account, returnData: false);
        try
        {
            return SecItemDelete(query);
        }
        finally
        {
            CFRelease(query);
        }
    }

    private static IntPtr BuildQueryDict(string service, string account, bool returnData)
    {
        var svc = CreateCFString(service);
        var acct = CreateCFString(account);
        try
        {
            var (keys, values) = MacKeychainQueryEntries.Create(
                _secClass,
                _secClassGenericPassword,
                _secAttrService,
                svc,
                _secAttrAccount,
                acct,
                _secReturnData,
                _cfBooleanTrue,
                _secMatchLimit,
                _secMatchLimitOne,
                returnData);
            return CFDictionaryCreate(_cfAllocatorDefault, keys, values, keys.Length, _cfTypeDictionaryKeyCallBacks, _cfTypeDictionaryValueCallBacks);
        }
        finally
        {
            CFRelease(svc);
            CFRelease(acct);
        }
    }

    private static IntPtr BuildAddDict(string service, string account, ReadOnlySpan<byte> data)
    {
        var svc = CreateCFString(service);
        var acct = CreateCFString(account);
        var cfData = CreateCFData(data);
        try
        {
            var keys = new IntPtr[] { _secClass, _secAttrService, _secAttrAccount, _secValueData };
            var values = new IntPtr[] { _secClassGenericPassword, svc, acct, cfData };
            return CFDictionaryCreate(_cfAllocatorDefault, keys, values, keys.Length, _cfTypeDictionaryKeyCallBacks, _cfTypeDictionaryValueCallBacks);
        }
        finally
        {
            CFRelease(svc);
            CFRelease(acct);
            CFRelease(cfData);
        }
    }

    private static IntPtr BuildUpdateDict(ReadOnlySpan<byte> data)
    {
        var cfData = CreateCFData(data);
        try
        {
            var keys = new IntPtr[] { _secValueData };
            var values = new IntPtr[] { cfData };
            return CFDictionaryCreate(_cfAllocatorDefault, keys, values, 1, _cfTypeDictionaryKeyCallBacks, _cfTypeDictionaryValueCallBacks);
        }
        finally
        {
            CFRelease(cfData);
        }
    }

    private static IntPtr CreateCFString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, CfStringEncodingUtf8, 0);
    }

    private static IntPtr CreateCFData(ReadOnlySpan<byte> data)
    {
        var arr = data.ToArray();
        return CFDataCreate(IntPtr.Zero, arr, arr.Length);
    }

    private static IntPtr ResolveCFBoolean(IntPtr handle, string name)
    {
        if (NativeLibrary.TryGetExport(handle, name, out var addr))
            return Marshal.ReadIntPtr(addr);
        return IntPtr.Zero;
    }

    private static IntPtr ResolveCFString(IntPtr handle, string name)
    {
        if (NativeLibrary.TryGetExport(handle, name, out var addr))
            return Marshal.ReadIntPtr(addr);
        return IntPtr.Zero;
    }

    private static IntPtr ResolveExport(IntPtr handle, string name)
    {
        if (NativeLibrary.TryGetExport(handle, name, out var addr))
            return addr;
        return IntPtr.Zero;
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemAdd(IntPtr attributes, out IntPtr result);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues,
        IntPtr keyCallbacks, IntPtr valueCallbacks);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFDataGetLength(IntPtr theData);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataGetBytePtr(IntPtr theData);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithBytes(
        IntPtr allocator, byte[] bytes, nint numBytes, int encoding, byte isExternalRepresentation);
}

internal static class MacKeychainQueryEntries
{
    public static (IntPtr[] Keys, IntPtr[] Values) Create(
        IntPtr secClass,
        IntPtr secClassGenericPassword,
        IntPtr secAttrService,
        IntPtr service,
        IntPtr secAttrAccount,
        IntPtr account,
        IntPtr secReturnData,
        IntPtr cfBooleanTrue,
        IntPtr secMatchLimit,
        IntPtr secMatchLimitOne,
        bool returnData)
    {
        if (!returnData)
        {
            return (
                [secClass, secAttrService, secAttrAccount],
                [secClassGenericPassword, service, account]);
        }

        return (
            [secClass, secAttrService, secAttrAccount, secReturnData, secMatchLimit],
            [secClassGenericPassword, service, account, cfBooleanTrue, secMatchLimitOne]);
    }
}
