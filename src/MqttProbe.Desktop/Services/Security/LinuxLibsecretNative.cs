using System.Runtime.InteropServices;

namespace MqttProbe.Desktop.Services.Security;

public sealed class LinuxLibsecretNative : ILinuxLibsecretNative
{
    private static readonly IntPtr _libsecretHandle;
    private static readonly IntPtr _glibHandle;
    private static readonly bool _loaded;

    private static readonly IntPtr _schemaNamePtr;
    private static readonly IntPtr _attrKeyIdPtr;
    private static readonly SecretSchema _schema;

    private static readonly IntPtr _gStrHash;
    private static readonly IntPtr _gStrEqual;
    private static readonly IntPtr _gFree;

    static LinuxLibsecretNative()
    {
        NativeLibrary.TryLoad("libsecret-1.so.0", typeof(LinuxLibsecretNative).Assembly, null, out _libsecretHandle);
        NativeLibrary.TryLoad("libglib-2.0.so.0", typeof(LinuxLibsecretNative).Assembly, null, out _glibHandle);

        _loaded = _libsecretHandle != IntPtr.Zero && _glibHandle != IntPtr.Zero;

        if (!_loaded)
            return;

        _schemaNamePtr = Marshal.StringToHGlobalAnsi("com.bluegrassiot.mqttprobe.MasterKey");
        _attrKeyIdPtr = Marshal.StringToHGlobalAnsi("key-id");

        _schema = new SecretSchema
        {
            Name = _schemaNamePtr,
            Flags = 0,
            Attributes = new SecretSchemaAttribute[32],
            Reserved = 0,
        };
        _schema.Attributes[0] = new SecretSchemaAttribute { Name = _attrKeyIdPtr, Type = 0 };

        NativeLibrary.TryGetExport(_glibHandle, "g_str_hash", out _gStrHash);
        NativeLibrary.TryGetExport(_glibHandle, "g_str_equal", out _gStrEqual);
        NativeLibrary.TryGetExport(_glibHandle, "g_free", out _gFree);
    }

    public int Lookup(string schemaName, IReadOnlyDictionary<string, string> attributes,
        out string? password, out string? errorMessage)
    {
        if (!_loaded)
        {
            password = null;
            errorMessage = "libsecret or glib native library not available.";
            return 2;
        }

        var attrs = CreateHashTable(attributes);
        try
        {
            var schema = _schema;
            var result = secret_password_lookupv_sync(ref schema, attrs, IntPtr.Zero, out var errorPtr);
            var error = ReadError(errorPtr, out var errorCode);

            if (result == IntPtr.Zero)
            {
                password = null;
                if (error != null)
                {
                    errorMessage = error;
                    return errorCode;
                }
                errorMessage = null;
                return 1;
            }

            password = Marshal.PtrToStringAnsi(result);
            secret_password_free(result);
            errorMessage = null;
            return 0;
        }
        finally
        {
            g_hash_table_unref(attrs);
        }
    }

    public int Store(string schemaName, IReadOnlyDictionary<string, string> attributes,
        string label, string password, out string? errorMessage)
    {
        if (!_loaded)
        {
            errorMessage = "libsecret or glib native library not available.";
            return 2;
        }

        var attrs = CreateHashTable(attributes);
        var labelPtr = Marshal.StringToHGlobalAnsi(label);
        var passwordPtr = Marshal.StringToHGlobalAnsi(password);
        try
        {
            var schema = _schema;
            var ok = secret_password_storev_sync(
                ref schema, attrs, IntPtr.Zero, labelPtr, passwordPtr, IntPtr.Zero, out var errorPtr);
            var error = ReadError(errorPtr, out var errorCode);

            if (ok == 0)
            {
                errorMessage = error ?? "libsecret store failed.";
                return errorCode;
            }

            errorMessage = null;
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(labelPtr);
            Marshal.FreeHGlobal(passwordPtr);
            g_hash_table_unref(attrs);
        }
    }

    public int Clear(string schemaName, IReadOnlyDictionary<string, string> attributes,
        out string? errorMessage)
    {
        if (!_loaded)
        {
            errorMessage = "libsecret or glib native library not available.";
            return 2;
        }

        var attrs = CreateHashTable(attributes);
        try
        {
            var schema = _schema;
            var ok = secret_password_clearv_sync(ref schema, attrs, IntPtr.Zero, out var errorPtr);
            var error = ReadError(errorPtr, out var errorCode);

            if (ok == 0)
            {
                errorMessage = error ?? "libsecret clear failed.";
                return errorCode;
            }

            errorMessage = null;
            return 0;
        }
        finally
        {
            g_hash_table_unref(attrs);
        }
    }

    private static IntPtr CreateHashTable(IReadOnlyDictionary<string, string> attributes)
    {
        var table = g_hash_table_new_full(_gStrHash, _gStrEqual, _gFree, _gFree);
        foreach (var (key, value) in attributes)
        {
            var keyPtr = Marshal.StringToHGlobalAnsi(key);
            var valuePtr = Marshal.StringToHGlobalAnsi(value);
            var dupKey = g_strdup(keyPtr);
            var dupValue = g_strdup(valuePtr);
            Marshal.FreeHGlobal(keyPtr);
            Marshal.FreeHGlobal(valuePtr);
            g_hash_table_insert(table, dupKey, dupValue);
        }
        return table;
    }

    private static string? ReadError(IntPtr errorPtr, out int classifiedCode)
    {
        classifiedCode = 3;
        if (errorPtr == IntPtr.Zero)
            return null;
        var error = Marshal.PtrToStructure<GError>(errorPtr);
        var message = error.Message != IntPtr.Zero
            ? Marshal.PtrToStringAnsi(error.Message)
            : "Unknown error";
        classifiedCode = ClassifyLibsecretError(message, hasError: true);
        g_error_free(errorPtr);
        return message;
    }

    internal static int ClassifyLibsecretError(string? message, bool hasError)
    {
        if (!hasError || message == null)
            return 3;

        var msg = message.ToLowerInvariant();

        if (msg.Contains("cannot autolaunch") ||
            msg.Contains("service unknown") ||
            msg.Contains("no such service") ||
            msg.Contains("no d-bus") ||
            msg.Contains("not available") ||
            msg.Contains("no session") ||
            msg.Contains("org.freedesktop.secrets"))
            return 2;

        if (msg.Contains("spawn") && msg.Contains("failed"))
            return 2;

        return 3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchemaAttribute
    {
        public IntPtr Name;
        public int Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchema
    {
        public IntPtr Name;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public SecretSchemaAttribute[] Attributes;
        public int Reserved;
        public IntPtr Reserved1;
        public IntPtr Reserved2;
        public IntPtr Reserved3;
        public IntPtr Reserved4;
        public IntPtr Reserved5;
        public IntPtr Reserved6;
        public IntPtr Reserved7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public IntPtr Message;
    }

    [DllImport("libsecret-1.so.0")]
    private static extern IntPtr secret_password_lookupv_sync(
        ref SecretSchema schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport("libsecret-1.so.0")]
    private static extern int secret_password_storev_sync(
        ref SecretSchema schema, IntPtr attributes, IntPtr collection,
        IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);

    [DllImport("libsecret-1.so.0")]
    private static extern int secret_password_clearv_sync(
        ref SecretSchema schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport("libsecret-1.so.0")]
    private static extern void secret_password_free(IntPtr password);

    [DllImport("libglib-2.0.so.0")]
    private static extern void g_error_free(IntPtr error);

    [DllImport("libglib-2.0.so.0")]
    private static extern IntPtr g_hash_table_new_full(
        IntPtr hashFunc, IntPtr keyEqualFunc, IntPtr keyDestroyFunc, IntPtr valueDestroyFunc);

    [DllImport("libglib-2.0.so.0")]
    private static extern void g_hash_table_insert(IntPtr hashTable, IntPtr key, IntPtr value);

    [DllImport("libglib-2.0.so.0")]
    private static extern void g_hash_table_unref(IntPtr hashTable);

    [DllImport("libglib-2.0.so.0")]
    private static extern IntPtr g_strdup(IntPtr str);
}
