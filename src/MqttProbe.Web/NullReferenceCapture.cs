using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace MqttProbe.Web;

internal static class NullReferenceCapture
{
    internal const int MaxRecords = 64;
    internal const int MaxBytes = 256 * 1024;

    private static readonly System.Threading.Lock _sync = new();
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);
    [ThreadStatic] private static bool _capturing;

    private static bool _installed;
    private static bool _enabled;
    private static string? _path;
    private static long _records;
    private static long _bytes;

    internal static bool IsEnabledForTests => _enabled;

    internal static long RecordCountForTests => Interlocked.Read(ref _records);

    internal static long ByteCountForTests => Interlocked.Read(ref _bytes);

    internal static bool InstallFromEnvironment() =>
        TryInstall(
            Environment.GetEnvironmentVariable("MQTTPROBE_NRE_PATH"),
            Environment.GetEnvironmentVariable("MQTTPROBE_NRE_ROOT"),
            out _);

    internal static bool TryInstall(string? path, string? approvedRoot, out string error)
    {
        error = "";
        lock (_sync)
        {
            if (_installed)
            {
                error = "already installed";
                return false;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "disabled";
                return false;
            }

            var root = string.IsNullOrWhiteSpace(approvedRoot)
                ? Path.Combine(Path.GetTempPath(), "opencode")
                : approvedRoot;
            if (!TryValidatePath(path, root, out var fullPath, out error))
                return false;

            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                using var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            }
            catch (Exception ex)
            {
                error = $"capture file is not writable: {ex.GetType().Name}";
                return false;
            }

            _path = fullPath;
            _enabled = true;
            _installed = true;
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            return true;
        }
    }

    internal static void ResetForTests()
    {
        lock (_sync)
        {
            if (_installed)
                AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            _installed = false;
            _enabled = false;
            _path = null;
            _records = 0;
            _bytes = 0;
        }
    }

    internal static void CaptureForTests(Exception exception, Action? whileCapturing = null)
    {
        if (!_enabled || _capturing)
            return;
        _capturing = true;
        try
        {
            whileCapturing?.Invoke();
            Write(exception);
        }
        finally
        {
            _capturing = false;
        }
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (!_enabled || _capturing || args.Exception is not NullReferenceException exception)
            return;
        if (Interlocked.Read(ref _records) >= MaxRecords || Interlocked.Read(ref _bytes) >= MaxBytes)
        {
            _enabled = false;
            return;
        }

        _capturing = true;
        try
        {
            Write(exception);
        }
        catch
        {
            // First-chance capture must never change application behavior.
        }
        finally
        {
            _capturing = false;
        }
    }

    private static void Write(Exception exception)
    {
        lock (_sync)
        {
            if (_path is null || !_enabled)
                return;

            var recordNumber = _records + 1;
            if (recordNumber > MaxRecords)
            {
                _enabled = false;
                return;
            }

            var text = Format(exception, recordNumber);
            var bytes = _utf8.GetByteCount(text);
            if (_bytes + bytes > MaxBytes)
            {
                _enabled = false;
                return;
            }

            try
            {
                File.AppendAllText(_path, text, _utf8);
                _bytes += bytes;
                _records = recordNumber;
            }
            catch
            {
                _enabled = false;
            }
        }
    }

    private static string Format(Exception exception, long recordNumber)
    {
        var builder = new StringBuilder();
        builder.Append("=== NRE ")
            .Append(DateTimeOffset.UtcNow.ToString("o"))
            .Append(" pid=").Append(Environment.ProcessId)
            .Append(" tid=").Append(Environment.CurrentManagedThreadId)
            .Append(" index=").Append(recordNumber)
            .Append(" ===\n");

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            builder.Append("Type: ").Append(current.GetType().FullName).Append('\n');
            var stack = current.StackTrace;
            if (string.IsNullOrWhiteSpace(stack))
                stack = new StackTrace(1, fNeedFileInfo: true).ToString();
            builder.Append("Stack:\n").Append(stack).Append('\n');
        }

        builder.Append("=== END NRE ===\n");
        return builder.ToString();
    }

    private static bool TryValidatePath(string path, string approvedRoot, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                error = "capture path must be absolute";
                return false;
            }

            fullPath = Path.GetFullPath(path);
            var root = Path.GetFullPath(approvedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                || !fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "capture path is outside the approved temporary root";
                return false;
            }

            if (Directory.Exists(fullPath))
            {
                error = "capture path points to a directory";
                return false;
            }

            var current = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current)
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != FileAttributes.None)
                {
                    error = "capture path has a reparse-point ancestor";
                    return false;
                }

                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                    break;
                current = Path.GetDirectoryName(current);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            return false;
        }
    }
}
