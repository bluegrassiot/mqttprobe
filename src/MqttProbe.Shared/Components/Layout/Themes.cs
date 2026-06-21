using MudBlazor;

namespace MqttProbe.Components.Layout;

public interface IThemes
{
    public MudTheme CurrentTheme { get; }
    public bool IsDarkMode { get; }
    public bool IsFontAccessible { get; }
    public string DarkLightModeButtonIcon { get; }
    public void SetTheme(bool isDark);
    public void SetFontAccessible(bool accessible);
    public void ToggleMode();
    public event Action? ModeChanged;
    public event Action? FontModeChanged;
}

public class Themes : IThemes
{
    private const string FontInter = "Inter";
    private const string FontDisplay = "Chakra Petch";
    private const string FontOpenDyslexic = "OpenDyslexic";
    private const string FontSansSerif = "sans-serif";

    private static class BrandTokens
    {
        public static class Brand
        {
            public const string SignalOrange = "#F97316";
            public const string SlateGray = "#94A3B8";
        }

        public static class Status
        {
            public const string SignalGreen = "#22C55E";
            public const string SignalAmber = "#F59E0B";
            public const string SignalRed = "#EF4444";
            public const string SignalBlue = "#38BDF8";
        }

        public static class Surfaces
        {
            public const string BgLight = "#F8FAFC";
            public const string SurfaceLight = "#F1F5F9";
            public const string BorderLight = "#CBD5E1";
            public const string DrawerBgLight = "#F8FAFC";

            public const string SlateBlack = "#0F172A";
            public const string SlateDark = "#1E293B";
            public const string SlateMid = "#293548";
            public const string SlateBorder = "#334155";
        }

        public static class Text
        {
            public const string PrimaryLight = "#0F172A";
            public const string SecondaryLight = "#475569";

            public const string OffWhite = "#F1F5F9";
            public const string SlateGray = "#94A3B8";
            public const string SlateMuted = "#64748B";
        }
    }

    private static readonly PaletteLight _lightPalette = new()
    {
        Primary = BrandTokens.Brand.SignalOrange,
        Secondary = BrandTokens.Brand.SlateGray,
        AppbarBackground = BrandTokens.Surfaces.SlateDark,
        AppbarText = BrandTokens.Text.OffWhite,
        DrawerBackground = BrandTokens.Surfaces.DrawerBgLight,
        Background = BrandTokens.Surfaces.BgLight,
        BackgroundGray = BrandTokens.Surfaces.SurfaceLight,
        Surface = BrandTokens.Surfaces.SurfaceLight,
        TextPrimary = BrandTokens.Text.PrimaryLight,
        TextSecondary = BrandTokens.Text.SecondaryLight,
        Info = BrandTokens.Status.SignalBlue,
        Success = BrandTokens.Status.SignalGreen,
        Warning = BrandTokens.Status.SignalAmber,
        Error = BrandTokens.Status.SignalRed,
        GrayLight = BrandTokens.Surfaces.BorderLight,
        GrayLighter = BrandTokens.Surfaces.BgLight,
        LinesDefault = BrandTokens.Surfaces.BorderLight,
        TableLines = BrandTokens.Surfaces.BorderLight,
        Divider = BrandTokens.Surfaces.BorderLight,
        Black = BrandTokens.Surfaces.SlateBlack,
        White = BrandTokens.Text.OffWhite
    };

    private static readonly PaletteDark _darkPalette = new()
    {
        Primary = BrandTokens.Brand.SignalOrange,
        Secondary = BrandTokens.Brand.SlateGray,
        Surface = BrandTokens.Surfaces.SlateDark,
        Background = BrandTokens.Surfaces.SlateBlack,
        BackgroundGray = BrandTokens.Surfaces.SlateMid,
        AppbarText = BrandTokens.Text.OffWhite,
        AppbarBackground = BrandTokens.Surfaces.SlateDark,
        DrawerBackground = BrandTokens.Surfaces.SlateBlack,
        DrawerIcon = BrandTokens.Text.SlateGray,
        DrawerText = BrandTokens.Text.SlateGray,
        ActionDefault = BrandTokens.Brand.SignalOrange,
        ActionDisabled = "#94A3B84D",
        ActionDisabledBackground = BrandTokens.Surfaces.SlateBorder,
        TextPrimary = BrandTokens.Text.OffWhite,
        TextSecondary = BrandTokens.Text.SlateGray,
        TextDisabled = BrandTokens.Text.SlateMuted,
        Info = BrandTokens.Status.SignalBlue,
        Success = BrandTokens.Status.SignalGreen,
        Warning = BrandTokens.Status.SignalAmber,
        Error = BrandTokens.Status.SignalRed,
        LinesDefault = BrandTokens.Surfaces.SlateBorder,
        TableLines = BrandTokens.Surfaces.SlateBorder,
        Divider = BrandTokens.Surfaces.SlateBorder,
        OverlayLight = "#0F172A80",
        GrayLight = BrandTokens.Surfaces.SlateBorder,
        GrayLighter = BrandTokens.Surfaces.SlateDark,
        Black = BrandTokens.Surfaces.SlateBlack,
        White = BrandTokens.Text.OffWhite
    };

    public MudTheme CurrentTheme { get; }

    public Themes()
    {
        CurrentTheme = new MudTheme
        {
            PaletteLight = _lightPalette,
            PaletteDark = _darkPalette,
            LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px" },
            Typography = BuildTypography(FontOpenDyslexic, FontOpenDyslexic)
        };
    }

    private static Typography BuildTypography(string fontUi, string fontDisplay) =>
        new()
        {
            Default = new DefaultTypography
            {
                FontFamily = [fontUi, FontSansSerif],
                LineHeight = "1.6"
            },
            H1 = new H1Typography { FontFamily = [fontDisplay, FontSansSerif], LineHeight = "1.2" },
            H2 = new H2Typography { FontFamily = [fontDisplay, FontSansSerif], LineHeight = "1.2" },
            H3 = new H3Typography { FontFamily = [fontDisplay, FontSansSerif], LineHeight = "1.2" },
            H4 = new H4Typography { FontFamily = [fontDisplay, FontSansSerif], LineHeight = "1.2" },
            H5 = new H5Typography { FontFamily = [fontDisplay, FontSansSerif], LineHeight = "1.2" },
            H6 = new H6Typography { FontFamily = [fontDisplay, FontSansSerif], LineHeight = "1.2" },
            Button = new ButtonTypography
            {
                FontFamily = [fontUi, FontSansSerif],
                FontSize = "0.875rem",
                FontWeight = "600"
            }
        };

    private bool _isFontAccessible;

    public bool IsFontAccessible
    {
        get => _isFontAccessible;
        set
        {
            if (_isFontAccessible == value) return;
            _isFontAccessible = value;
            CurrentTheme.Typography = value
                ? BuildTypography(FontOpenDyslexic, FontOpenDyslexic)
                : BuildTypography(FontInter, FontDisplay);
            FontModeChanged?.Invoke();
        }
    }

    public static string FontToggleIcon => LucideIcons.Eye;

    public event Action? FontModeChanged;

    public string DarkLightModeButtonIcon => IsDarkMode
        ? LucideIcons.Sun
        : LucideIcons.Moon;

    public bool IsDarkMode { get; private set; } = true;

    public event Action? ModeChanged;

    public void SetTheme(bool isDark)
    {
        if (IsDarkMode == isDark) return;
        IsDarkMode = isDark;
        ModeChanged?.Invoke();
    }

    public void ToggleMode() => SetTheme(!IsDarkMode);

    public void SetFontAccessible(bool accessible) => IsFontAccessible = accessible;
}
