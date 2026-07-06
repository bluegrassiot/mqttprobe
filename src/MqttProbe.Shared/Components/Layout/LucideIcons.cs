namespace MqttProbe.Components.Layout;

/// <summary>
/// Lucide icon SVG strings compatible with MudBlazor's icon system.
/// Icons are formatted as stroke-based SVG elements (fill="none", stroke="currentColor").
/// See https://lucide.dev for the icon set.
/// Note: Blazor.Lucide NuGet package provides &lt;LucideIcon&gt; components; these string
/// constants are used for MudBlazor icon parameters (Icon, StartIcon, AdornmentIcon, etc.)
/// which require raw SVG string data.
/// </summary>
public static class LucideIcons
{
    private const string S = "stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' fill='none'";

    // Navigation
    public const string ArrowLeft      = $"<path {S} d='M19 12H5M12 19l-7-7 7-7'/>";
    public const string ChevronLeft    = $"<path {S} d='M15 18l-6-6 6-6'/>";
    public const string ChevronRight   = $"<path {S} d='M9 18l6-6-6-6'/>";
    public const string ChevronUp      = $"<path {S} d='M18 15l-6-6-6 6'/>";
    public const string ChevronDown    = $"<path {S} d='M6 9l6 6 6-6'/>";
    public const string ChevronsUpDown = $"<path {S} d='M7 15l5 5 5-5M7 9l5-5 5 5'/>";
    public const string ChevronsDownUp = $"<path {S} d='M7 20l5-5 5 5M7 4l5 5 5-5'/>";
    public const string ArrowUp        = $"<path {S} d='M12 19V5M5 12l7-7 7 7'/>";
    public const string ArrowDown      = $"<path {S} d='M12 5v14M19 12l-7 7-7-7'/>";
    public const string MoreVertical   = $"<path {S} d='M12 5v.01M12 12v.01M12 19v.01'/>";

    // Actions / CRUD
    public const string Plus           = $"<path {S} d='M12 5v14M5 12h14'/>";
    public const string Trash2         = $"<path {S} d='M3 6h18M8 6V4h8v2M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M10 11v6M14 11v6'/>";
    public const string Save           = $"<path {S} d='M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2zM17 21v-8H7v8M7 3v5h8'/>";
    public const string Copy           = $"<rect {S} x='9' y='9' width='13' height='13' rx='2'/><path {S} d='M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1'/>";
    public const string ClipboardPaste = $"<path {S} d='M9 5H7a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-2M9 5a2 2 0 0 0 2 2h2a2 2 0 0 0 2-2M9 5a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2M8 15h8M8 11h4'/>";
    public const string Pencil         = $"<path {S} d='M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7M18.5 2.5a2.12 2.12 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z'/>";
    public const string Bookmark       = $"<path {S} d='M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z'/>";
    public const string Send           = $"<path {S} d='M22 2L11 13M22 2L15 22 11 13 2 9l20-7z'/>";

    // Connectivity / MQTT
    public const string Wifi           = $"<path {S} d='M5 12.55a11 11 0 0 1 14.08 0M1.42 9a16 16 0 0 1 21.16 0M8.53 16.11a6 6 0 0 1 6.95 0'/>";
    public const string WifiOff        = $"<path {S} d='M1.42 9a16 16 0 0 1 21.16 0M5 12.55a11 11 0 0 1 14.08 0M8.53 16.11a6 6 0 0 1 6.95 0M1 1l22 22'/>";
    public const string Radio          = $"<path {S} d='M18.89 8.11a7 7 0 0 0-9.9 0M15.46 10.54a4 4 0 0 0-5.66 0'/>";
    public const string Hash           = $"<path {S} d='M4 9h16M4 15h16M10 3L8 21M16 3l-2 18'/>";
    public const string GitHub         = $"<path {S} d='M15 22v-4a4.8 4.8 0 0 0-1-3.5c3 0 6-2 6-5.5.08-1.25-.27-2.48-1-3.5.28-1.15.28-2.35 0-3.5 0 0-1 0-3 1.5-2.64-.5-5.36-.5-8 0C6 2 5 2 5 2c-.3 1.15-.3 2.35 0 3.5A5.403 5.403 0 0 0 4 9c0 3.5 3 5.5 6 5.5-.39.49-.68 1.05-.85 1.65-.17.6-.22 1.23-.15 1.85v4M9 18c-4.51 2-5-2-7-2'/>";

    // Connectivity icons (connection/power)
    public const string Power          = $"<path {S} d='M18.36 6.64a9 9 0 1 1-12.73 0M12 2v10'/>";
    public const string LogOut         = $"<path {S} d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9'/>";
    public const string KeyRound       = $"<path {S} d='M2 18v3c0 .6.4 1 1 1h4v-3h3v-3h2l1.4-1.4a6.5 6.5 0 1 0-4 4zM17.5 6a.5.5 0 1 0 0-1 .5.5 0 0 0 0 1'/>";
    public const string Shield         = $"<path {S} d='M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z'/>";
    public const string Lock           = $"<rect {S} x='3' y='11' width='18' height='11' rx='2'/><path {S} d='M7 11V7a5 5 0 0 1 10 0v4'/>";

    // Media / playback
    public const string Play           = $"<path {S} d='M5 3l14 9-14 9V3z'/>";
    public const string Pause          = $"<rect {S} x='6' y='4' width='4' height='16'/><rect {S} x='14' y='4' width='4' height='16'/>";
    public const string PlayCircle     = $"<circle {S} cx='12' cy='12' r='10'/><path {S} d='M10 8l6 4-6 4V8z'/>";
    public const string PauseCircle    = $"<circle {S} cx='12' cy='12' r='10'/><path {S} d='M10 15V9M14 15V9'/>";
    public const string Square         = $"<rect {S} x='3' y='3' width='18' height='18' rx='2'/>";
    public const string CircleStop     = $"<circle {S} cx='12' cy='12' r='10'/><rect {S} x='9' y='9' width='6' height='6'/>";

    // Data / charts
    public const string Activity       = $"<path {S} d='M22 12h-4l-3 9L9 3l-3 9H2'/>";
    public const string ChartLine      = $"<path {S} d='M3 3v18h18M18.7 8l-5.1 5.2-2.8-2.7L7 14.3'/>";
    public const string Layers         = $"<path {S} d='M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5'/>";

    // Alerts / status
    public const string AlertTriangle  = $"<path {S} d='M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z'/><path {S} d='M12 9v4M12 17h.01'/>";
    public const string Info           = $"<circle {S} cx='12' cy='12' r='10'/><path {S} d='M12 16v-4M12 8h.01'/>";

    // Search / filter
    public const string Search         = $"<circle {S} cx='11' cy='11' r='8'/><path {S} d='M21 21l-4.35-4.35'/>";

    // System / UI
    public const string Sun            = $"<circle {S} cx='12' cy='12' r='5'/><path {S} d='M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42'/>";
    public const string Moon           = $"<path {S} d='M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z'/>";
    public const string RefreshCw      = $"<path {S} d='M23 4v6h-6M1 20v-6h6M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15'/>";
    public const string Eye            = $"<path {S} d='M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z'/><circle {S} cx='12' cy='12' r='3'/>";
    public const string EyeOff         = $"<path {S} d='M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24M1 1l22 22'/>";
    public const string Inbox          = $"<path {S} d='M22 12h-6l-2 3h-4l-2-3H2M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z'/>";
    public const string Folder         = $"<path {S} d='M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z'/>";
    public const string Circle         = $"<circle {S} cx='12' cy='12' r='10'/>";
    public const string Database       = $"<ellipse {S} cx='12' cy='5' rx='9' ry='3'/><path {S} d='M3 5v14c0 1.66 4 3 9 3s9-1.34 9-3V5'/><path {S} d='M3 12c0 1.66 4 3 9 3s9-1.34 9-3'/>";
    public const string CheckCircle    = $"<path {S} d='M22 11.08V12a10 10 0 1 1-5.93-9.14M22 4L12 14.01l-3-3'/>";
    public const string Sparkles       = $"<path {S} d='M12 3l1.09 2.36L15.75 6l-2.66 1.09L12 9.75l-1.09-2.66L8.25 6l2.66-1.09L12 3zM5 14l.54 1.09L6.75 15.5 5.54 16.09 5 17.5l-.54-1.41L3.25 15.5l1.21-.41L5 14zM19 2l.54 1.09L20.75 3.5l-1.21.5L19 5.25l-.54-1.25L17.25 3.5l1.21-.41L19 2z'/>";
    public const string Cpu            = $"<rect {S} x='9' y='9' width='6' height='6'/><path {S} d='M9 3H5a2 2 0 0 0-2 2v4m6-6h10a2 2 0 0 1 2 2v4M9 3v18m0 0h10a2 2 0 0 0 2-2V9M9 21H5a2 2 0 0 1-2-2V9m0 0h18M3 9h18M3 15h18M15 3v6M15 15v6'/>";
    public const string Network        = $"<rect {S} x='9' y='2' width='6' height='6'/><rect {S} x='2' y='16' width='6' height='6'/><rect {S} x='16' y='16' width='6' height='6'/><path {S} d='M12 8v4M8 19H5a3 3 0 0 1 0-6h14a3 3 0 0 1 0 6h-3'/>";
    public const string Clock          = $"<circle {S} cx='12' cy='12' r='10'/><path {S} d='M12 6v6l4 2'/>";
    public const string Zap            = $"<path {S} d='M13 2L3 14h9l-1 8 10-12h-9l1-8z'/>";
    public const string Maximize2      = $"<polyline {S} points='15 3 21 3 21 9'/><polyline {S} points='9 21 3 21 3 15'/><line {S} x1='21' y1='3' x2='14' y2='10'/><line {S} x1='3' y1='21' x2='10' y2='14'/>";
    public const string Download       = $"<path {S} d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><polyline {S} points='7 10 12 15 17 10'/><line {S} x1='12' y1='15' x2='12' y2='3'/>";
    public const string Minimize2      = $"<polyline {S} points='4 14 10 14 10 20'/><polyline {S} points='20 10 14 10 14 4'/><line {S} x1='10' y1='14' x2='21' y2='3'/><line {S} x1='3' y1='21' x2='14' y2='10'/>";
    public const string Settings       = $"<path {S} d='M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z'/><circle {S} cx='12' cy='12' r='3'/>";

    // Close / dismiss
    public const string X              = $"<path {S} d='M18 6 6 18M6 6l12 12'/>";

    public const string FactCheck      = CheckCircle; // alias
    public const string AutoFixHigh    = Sparkles;    // alias
}
