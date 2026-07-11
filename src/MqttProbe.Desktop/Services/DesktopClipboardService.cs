using Microsoft.JSInterop;
using MqttProbe.Services.Platform;

namespace MqttProbe.Services;

public class DesktopClipboardService : IClipboardService
{
    private readonly IJSRuntime _js;

    internal Func<Task<string?>> ReadNativeFallback { get; set; }
    internal Func<string, Task> WriteNativeFallback { get; set; }

    public DesktopClipboardService(IJSRuntime js)
    {
        _js = js;
        ReadNativeFallback = ReadViaLinuxToolsAsync;
        WriteNativeFallback = WriteViaLinuxToolsAsync;
    }

    public async Task<string?> GetTextAsync()
    {
        try
        {
            var text = await _js.InvokeAsync<string?>("navigator.clipboard.readText");
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }
        catch
        {
            // Denied or unavailable in this WebView; try native tools below.
        }

        return await ReadNativeFallback();
    }

    public async Task WriteTextAsync(string text)
    {
        try
        {
            await _js.InvokeVoidAsync("navigator.clipboard.writeText", text);
        }
        catch
        {
            await WriteNativeFallback(text);
        }
    }

    private static bool IsWayland
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static async Task<string?> ReadViaLinuxToolsAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return IsWayland
            ? await RunProcessAsync("wl-paste", "--no-newline")
            : await RunProcessAsync("xclip", "-selection clipboard -o");
    }

    private static async Task WriteViaLinuxToolsAsync(string text)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (IsWayland)
        {
            await RunProcessAsync("wl-copy", string.Empty, text);
        }
        else
        {
            await RunProcessAsync("xclip", "-selection clipboard", text);
        }
    }

    private static async Task<string?> RunProcessAsync(string fileName, string arguments, string? stdin = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardInput = stdin != null,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return null;
            }
            if (stdin != null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? output.TrimEnd() : null;
        }
        catch
        {
            return null;
        }
    }
}
