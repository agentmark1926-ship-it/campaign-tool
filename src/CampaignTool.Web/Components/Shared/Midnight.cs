using MudBlazor;

namespace CampaignTool.Web.Components.Shared;

/// <summary>The "Midnight" dark theme: near-black ground, mint primary, violet and blue accents (see wwwroot/app.css for the rest).</summary>
public static class Midnight
{
    public const string Mint = "#34D399", Violet = "#8B7CF6", Blue = "#60A5FA", Amber = "#FBBF24", Orange = "#FB923C", Red = "#F87171";

    private static readonly string[] Body = ["Manrope", "system-ui", "sans-serif"];
    private static readonly string[] Display = ["Space Grotesk", "Manrope", "system-ui", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = Mint,
            PrimaryContrastText = "#0B0F17",
            Secondary = Violet,
            SecondaryContrastText = "#0B0F17",
            Info = Blue,
            InfoContrastText = "#0B0F17",
            Success = Mint,
            SuccessContrastText = "#0B0F17",
            Warning = Amber,
            WarningContrastText = "#0B0F17",
            Error = Red,
            ErrorContrastText = "#0B0F17",
            Black = "#0B0F17",
            Background = "#0B0F17",
            BackgroundGray = "#111827",
            Surface = "#131A26",
            DrawerBackground = "#0B0F17",
            DrawerText = "#9AA6B8",
            DrawerIcon = "#9AA6B8",
            AppbarBackground = "#0B0F17",
            AppbarText = "#E8EDF5",
            TextPrimary = "#E8EDF5",
            TextSecondary = "#9AA6B8",
            TextDisabled = "#5B6678",
            ActionDefault = "#9AA6B8",
            ActionDisabled = "#4A5568",
            ActionDisabledBackground = "#1E2738",
            LinesDefault = "#1E2738",
            LinesInputs = "#2E3A52",
            TableLines = "#1E2738",
            TableStriped = "#151D2B",
            TableHover = "#172033",
            Divider = "#1E2738",
            DividerLight = "#1A2233",
            OverlayDark = "rgba(5,8,14,0.7)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Body },
            H1 = new H1Typography { FontFamily = Display, FontWeight = "700" },
            H2 = new H2Typography { FontFamily = Display, FontWeight = "700" },
            H3 = new H3Typography { FontFamily = Display, FontWeight = "700" },
            H4 = new H4Typography { FontFamily = Display, FontWeight = "700", FontSize = "2rem" },
            H5 = new H5Typography { FontFamily = Display, FontWeight = "600" },
            H6 = new H6Typography { FontFamily = Display, FontWeight = "600", FontSize = "1.125rem" },
            Button = new ButtonTypography { FontFamily = Body, FontWeight = "700", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "12px", DrawerWidthLeft = "232px" },
    };
}
