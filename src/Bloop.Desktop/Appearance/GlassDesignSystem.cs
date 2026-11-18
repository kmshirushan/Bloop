using System.Windows;
using System.Windows.Media;
using Bloop.Core.Appearance;

namespace Bloop.Desktop.Appearance;

/// <summary>
/// Computed 5-level Liquid Glass surface tokens shared between NotificationWindow and SettingsWindow.
/// Level 0: Desktop Backdrop (DWM Acrylic / Mica / Translucent Fallback)
/// Level 1: Main Glass Window Surface & Directional Specular Rim
/// Level 2: Secondary Glass Containers (App Groups & Settings Sections)
/// Level 3: Notification Cards & Input Wells
/// Level 4: Interactive Controls, Pills, Tabs & Buttons
/// </summary>
public sealed record GlassDesignTokens(
    bool IsLightTheme,
    bool IsGlassTheme,
    bool IsDwmBackdropActive,
    CornerRadius WindowCornerRadius,
    CornerRadius SectionCornerRadius,
    CornerRadius CardCornerRadius,
    System.Windows.Media.Brush Level1WindowFillBrush,
    System.Windows.Media.Brush Level1WindowRimBrush,
    System.Windows.Media.Brush Level1TopSpecularBrush,
    System.Windows.Media.Brush Level1AmbientGlowBrush,
    System.Windows.Media.Brush Level2ContainerFillBrush,
    System.Windows.Media.Brush Level2ContainerBorderBrush,
    System.Windows.Media.Brush Level3CardFillBrush,
    System.Windows.Media.Brush Level3CardUnreadFillBrush,
    System.Windows.Media.Brush Level3CardBorderBrush,
    System.Windows.Media.Brush Level3CardUnreadBorderBrush,
    System.Windows.Media.Brush Level3InputFillBrush,
    System.Windows.Media.Brush Level4ControlFillBrush,
    System.Windows.Media.Brush Level4ControlHoverBrush,
    System.Windows.Media.Brush Level4ControlPressedBrush,
    System.Windows.Media.Brush Level4ControlBorderBrush,
    System.Windows.Media.Brush PrimaryAccentFillBrush,
    System.Windows.Media.Brush PrimaryAccentHoverBrush,
    System.Windows.Media.Brush PrimaryAccentBorderBrush,
    System.Windows.Media.Brush DangerFillBrush,
    System.Windows.Media.Brush DangerBorderBrush,
    System.Windows.Media.Brush PrimaryTextBrush,
    System.Windows.Media.Brush SecondaryTextBrush,
    System.Windows.Media.Brush MutedTextBrush,
    System.Windows.Media.Brush AccentTextBrush
);

public static class GlassDesignSystem
{
    public static GlassDesignTokens ComputeTokens(
        PanelAppearance? rawPanel,
        CharacterAppearance? rawCharacter,
        bool isDwmBackdropActive = true)
    {
        var character = AppearanceValidator.Normalize(rawCharacter);
        var panel = AppearanceValidator.Normalize(rawPanel, character.AccentColor);

        var (bgR, bgG, bgB) = AppearanceValidator.ParseRgb(panel.BackgroundColor, (10, 22, 44));
        var (accR, accG, accB) = AppearanceValidator.ParseRgb(panel.AccentColor, (56, 189, 248));

        double bgLuma = 0.299 * bgR + 0.587 * bgG + 0.114 * bgB;
        bool isLight = panel.Theme == PanelThemePreset.Light ||
                       (panel.Theme == PanelThemePreset.Custom && bgLuma > 165);
        bool isLiquid = panel.Theme == PanelThemePreset.LiquidGlass;
        bool isGlass = isLiquid || panel.Theme == PanelThemePreset.Glass || panel.EnableGlassBlur;

        // ------------------------------------------------------------------
        // Level 1: Main Glass Window Surface & Directional Specular Rim
        // ------------------------------------------------------------------
        double baseOpacity = panel.BackgroundOpacity;
        if (isGlass)
        {
            // When DWM Acrylic blur is active, allow more background diffusion through;
            // when in fallback or unfocused mode, keep enough sapphire body for crisp readability.
            baseOpacity = isDwmBackdropActive
                ? Math.Clamp(baseOpacity, 0.54, 0.84)
                : Math.Clamp(Math.Max(baseOpacity, 0.78), 0.72, 0.92);
        }

        byte l1AlphaTop = (byte)Math.Clamp(Math.Round(baseOpacity * 242.0), 115, 248);
        byte l1AlphaBottom = (byte)Math.Clamp(Math.Round(Math.Min(1.0, baseOpacity + 0.06) * 250.0), 135, 252);

        System.Windows.Media.Color l1TopColor;
        System.Windows.Media.Color l1MidColor;
        System.Windows.Media.Color l1BottomColor;

        if (isLight)
        {
            l1TopColor = System.Windows.Media.Color.FromArgb(l1AlphaTop, 248, 250, 252);
            l1MidColor = System.Windows.Media.Color.FromArgb(l1AlphaBottom, 241, 245, 249);
            l1BottomColor = System.Windows.Media.Color.FromArgb(l1AlphaBottom, 226, 232, 240);
        }
        else if (isLiquid)
        {
            // Deep sapphire-blue Liquid Glass gradient infused with subtle accent tint
            byte topR = (byte)Math.Clamp((bgR * 0.70) + (accR * 0.18) + 8, 8, 255);
            byte topG = (byte)Math.Clamp((bgG * 0.72) + (accG * 0.18) + 14, 14, 255);
            byte topB = (byte)Math.Clamp((bgB * 0.75) + (accB * 0.22) + 26, 28, 255);

            l1TopColor = System.Windows.Media.Color.FromArgb(l1AlphaTop, topR, topG, topB);
            l1MidColor = System.Windows.Media.Color.FromArgb(l1AlphaBottom,
                (byte)Math.Clamp(bgR * 0.85 + 4, 6, 255),
                (byte)Math.Clamp(bgG * 0.85 + 10, 12, 255),
                (byte)Math.Clamp(bgB * 0.90 + 20, 24, 255));
            l1BottomColor = System.Windows.Media.Color.FromArgb(l1AlphaBottom, 6, 15, 32);
        }
        else
        {
            l1TopColor = System.Windows.Media.Color.FromArgb(l1AlphaTop,
                (byte)Math.Clamp(bgR + 10, 0, 255),
                (byte)Math.Clamp(bgG + 14, 0, 255),
                (byte)Math.Clamp(bgB + 22, 0, 255));
            l1MidColor = System.Windows.Media.Color.FromArgb(l1AlphaBottom, bgR, bgG, bgB);
            l1BottomColor = System.Windows.Media.Color.FromArgb(l1AlphaBottom,
                (byte)Math.Max(4, bgR - 6),
                (byte)Math.Max(8, bgG - 8),
                (byte)Math.Max(16, bgB - 10));
        }

        var level1Fill = CreateDiagonalGradient(l1TopColor, l1MidColor, l1BottomColor);

        // Directional specular rim instead of a bright neon outline:
        // Catch-light at top-left (soft white + accent), subtle neutral glass edge in middle, soft accent glow at bottom-right.
        var rimTopLeft = isLight
            ? System.Windows.Media.Color.FromArgb(210, 255, 255, 255)
            : System.Windows.Media.Color.FromArgb(isGlass ? (byte)115 : (byte)85,
                (byte)Math.Clamp((255 + accR) / 2, 160, 255),
                (byte)Math.Clamp((255 + accG) / 2, 200, 255),
                (byte)Math.Clamp((255 + accB) / 2, 220, 255));
        var rimMid = isLight
            ? System.Windows.Media.Color.FromArgb(140, 148, 163, 184)
            : System.Windows.Media.Color.FromArgb(42, accR, accG, accB);
        var rimBottomRight = isLight
            ? System.Windows.Media.Color.FromArgb(165, accR, accG, accB)
            : System.Windows.Media.Color.FromArgb(isGlass ? (byte)78 : (byte)54, accR, accG, accB);

        var level1Rim = CreateDiagonalGradient(rimTopLeft, rimMid, rimBottomRight);

        var topSpecular = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0)
        };
        topSpecular.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 0.0));
        topSpecular.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(isLight ? (byte)220 : (byte)120, 255, 255, 255), 0.22));
        topSpecular.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(isLight ? (byte)160 : (byte)95, accR, accG, accB), 0.55));
        topSpecular.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 1.0));
        topSpecular.Freeze();

        var ambientGlow = new RadialGradientBrush
        {
            Center = new System.Windows.Point(0.18, 0.08),
            GradientOrigin = new System.Windows.Point(0.18, 0.08),
            RadiusX = 0.75,
            RadiusY = 0.55
        };
        ambientGlow.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(isLight ? (byte)28 : (byte)44, accR, accG, accB), 0.0));
        ambientGlow.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, accR, accG, accB), 1.0));
        ambientGlow.Freeze();

        // ------------------------------------------------------------------
        // Level 2: Secondary Glass Containers (App Groups & Settings Sections)
        // Lighter and more luminous than Level 1 so containers float above the backdrop
        // ------------------------------------------------------------------
        byte l2Alpha = (byte)Math.Clamp(Math.Round(panel.CardOpacity * (isGlass ? 115.0 : 165.0)), 64, 210);
        var level2Fill = isLight
            ? CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(215, 255, 255, 255),
                System.Windows.Media.Color.FromArgb(195, 241, 245, 249))
            : CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(l2Alpha, 26, 49, 82),
                System.Windows.Media.Color.FromArgb((byte)Math.Max(48, l2Alpha - 18), 15, 31, 56));

        var level2Border = isLight
            ? CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(190, 203, 213, 225),
                System.Windows.Media.Color.FromArgb(130, 148, 163, 184))
            : CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(68, 186, 230, 253),
                System.Windows.Media.Color.FromArgb(26, 148, 163, 184));

        // ------------------------------------------------------------------
        // Level 3: Notification Cards & Input Wells (Elevated Frosted Layer)
        // ------------------------------------------------------------------
        byte l3Alpha = (byte)Math.Clamp(Math.Round(panel.CardOpacity * (isGlass ? 148.0 : 195.0)), 82, 235);
        var level3CardFill = isLight
            ? CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(242, 255, 255, 255),
                System.Windows.Media.Color.FromArgb(225, 248, 250, 252))
            : CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(l3Alpha, 35, 62, 102),
                System.Windows.Media.Color.FromArgb((byte)Math.Max(64, l3Alpha - 22), 21, 39, 68));

        var level3UnreadFill = isLight
            ? CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(248, 255, 255, 255),
                System.Windows.Media.Color.FromArgb(235, 240, 249, 255))
            : CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb((byte)Math.Min(240, l3Alpha + 18),
                    (byte)Math.Clamp(32 + accR * 0.16, 28, 255),
                    (byte)Math.Clamp(58 + accG * 0.18, 48, 255),
                    (byte)Math.Clamp(98 + accB * 0.22, 82, 255)),
                System.Windows.Media.Color.FromArgb(l3Alpha, 23, 43, 77));

        var level3Border = isLight
            ? CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(180, 203, 213, 225),
                System.Windows.Media.Color.FromArgb(130, 226, 232, 240))
            : CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(76, 224, 242, 254),
                System.Windows.Media.Color.FromArgb(28, 148, 163, 184));

        var level3UnreadBorder = CreateDiagonalGradient(
            System.Windows.Media.Color.FromArgb(145, accR, accG, accB),
            System.Windows.Media.Color.FromArgb(72, 224, 242, 254),
            System.Windows.Media.Color.FromArgb(52, accR, accG, accB));

        var level3InputFill = isLight
            ? FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 255, 255, 255)))
            : FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromArgb(155, 15, 28, 52)));

        // ------------------------------------------------------------------
        // Level 4: Interactive Controls, Pills & Buttons
        // ------------------------------------------------------------------
        var level4Fill = isLight
            ? CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(215, 241, 245, 249),
                System.Windows.Media.Color.FromArgb(195, 226, 232, 240))
            : CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(135, 38, 66, 108),
                System.Windows.Media.Color.FromArgb(110, 22, 42, 74));

        var level4Hover = isLight
            ? CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(235, 226, 232, 240),
                System.Windows.Media.Color.FromArgb(215, 203, 213, 225))
            : CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(175, 52, 88, 138),
                System.Windows.Media.Color.FromArgb(145, 32, 58, 98));

        var level4Pressed = isLight
            ? FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 203, 213, 225)))
            : FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 18, 35, 64)));

        var level4Border = isLight
            ? FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromArgb(175, 148, 163, 184)))
            : CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(92, 186, 230, 253),
                System.Windows.Media.Color.FromArgb(38, 148, 163, 184));

        // Primary Accent Glass Button
        var primaryAccentFill = CreateVerticalGradient(
            System.Windows.Media.Color.FromArgb(215,
                (byte)Math.Clamp(accR * 0.85 + 24, 0, 255),
                (byte)Math.Clamp(accG * 0.85 + 24, 0, 255),
                (byte)Math.Clamp(accB * 0.90 + 24, 0, 255)),
            System.Windows.Media.Color.FromArgb(195,
                (byte)Math.Clamp(accR * 0.62, 0, 255),
                (byte)Math.Clamp(accG * 0.62, 0, 255),
                (byte)Math.Clamp(accB * 0.72, 0, 255)));

        var primaryAccentHover = CreateVerticalGradient(
            System.Windows.Media.Color.FromArgb(238,
                (byte)Math.Clamp(accR * 0.92 + 32, 0, 255),
                (byte)Math.Clamp(accG * 0.92 + 32, 0, 255),
                (byte)Math.Clamp(accB * 0.95 + 32, 0, 255)),
            System.Windows.Media.Color.FromArgb(215,
                (byte)Math.Clamp(accR * 0.72, 0, 255),
                (byte)Math.Clamp(accG * 0.72, 0, 255),
                (byte)Math.Clamp(accB * 0.82, 0, 255)));

        var primaryAccentBorder = CreateVerticalGradient(
            System.Windows.Media.Color.FromArgb(185, 224, 242, 254),
            System.Windows.Media.Color.FromArgb(110, accR, accG, accB));

        // Danger / Warning Glass Button (Clear Local / Delete)
        var dangerFill = isLight
            ? CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(45, 244, 63, 94),
                System.Windows.Media.Color.FromArgb(30, 225, 29, 72))
            : CreateVerticalGradient(
                System.Windows.Media.Color.FromArgb(95, 136, 19, 55),
                System.Windows.Media.Color.FromArgb(72, 76, 5, 25));

        var dangerBorder = FreezeBrush(new SolidColorBrush(
            isLight
                ? System.Windows.Media.Color.FromArgb(140, 225, 29, 72)
                : System.Windows.Media.Color.FromArgb(110, 251, 113, 133)));

        // Typography Hierarchy
        var primaryText = FreezeBrush(new SolidColorBrush(
            isLight
                ? System.Windows.Media.Color.FromRgb(15, 23, 42)
                : System.Windows.Media.Color.FromRgb(248, 250, 252)));
        var secondaryText = FreezeBrush(new SolidColorBrush(
            isLight
                ? System.Windows.Media.Color.FromRgb(51, 65, 85)
                : System.Windows.Media.Color.FromRgb(203, 213, 225)));
        var mutedText = FreezeBrush(new SolidColorBrush(
            isLight
                ? System.Windows.Media.Color.FromRgb(100, 116, 139)
                : System.Windows.Media.Color.FromRgb(148, 163, 184)));
        var accentText = FreezeBrush(new SolidColorBrush(
            isLight
                ? System.Windows.Media.Color.FromRgb(
                    (byte)Math.Clamp(accR * 0.65, 0, 200),
                    (byte)Math.Clamp(accG * 0.65, 0, 200),
                    (byte)Math.Clamp(accB * 0.75, 0, 215))
                : System.Windows.Media.Color.FromRgb(
                    (byte)Math.Clamp(accR + 28, 90, 255),
                    (byte)Math.Clamp(accG + 28, 150, 255),
                    (byte)Math.Clamp(accB + 28, 195, 255))));

        double winRadius = Math.Clamp(panel.CornerRadius, 8.0, 28.0);
        double secRadius = Math.Max(8.0, winRadius - 4.0);
        double cardRadius = Math.Max(6.0, winRadius - 6.0);

        return new GlassDesignTokens(
            IsLightTheme: isLight,
            IsGlassTheme: isGlass,
            IsDwmBackdropActive: isDwmBackdropActive,
            WindowCornerRadius: new CornerRadius(winRadius),
            SectionCornerRadius: new CornerRadius(secRadius),
            CardCornerRadius: new CornerRadius(cardRadius),
            Level1WindowFillBrush: level1Fill,
            Level1WindowRimBrush: level1Rim,
            Level1TopSpecularBrush: topSpecular,
            Level1AmbientGlowBrush: ambientGlow,
            Level2ContainerFillBrush: level2Fill,
            Level2ContainerBorderBrush: level2Border,
            Level3CardFillBrush: level3CardFill,
            Level3CardUnreadFillBrush: level3UnreadFill,
            Level3CardBorderBrush: level3Border,
            Level3CardUnreadBorderBrush: level3UnreadBorder,
            Level3InputFillBrush: level3InputFill,
            Level4ControlFillBrush: level4Fill,
            Level4ControlHoverBrush: level4Hover,
            Level4ControlPressedBrush: level4Pressed,
            Level4ControlBorderBrush: level4Border,
            PrimaryAccentFillBrush: primaryAccentFill,
            PrimaryAccentHoverBrush: primaryAccentHover,
            PrimaryAccentBorderBrush: primaryAccentBorder,
            DangerFillBrush: dangerFill,
            DangerBorderBrush: dangerBorder,
            PrimaryTextBrush: primaryText,
            SecondaryTextBrush: secondaryText,
            MutedTextBrush: mutedText,
            AccentTextBrush: accentText
        );
    }

    public static GlassDesignTokens ApplyToApplicationResources(
        PanelAppearance? panel,
        CharacterAppearance? character,
        bool isDwmBackdropActive = true)
    {
        var tokens = ComputeTokens(panel, character, isDwmBackdropActive);
        var res = System.Windows.Application.Current?.Resources;
        if (res is null)
        {
            return tokens;
        }

        res["GlassWindowCornerRadius"] = tokens.WindowCornerRadius;
        res["GlassSectionCornerRadius"] = tokens.SectionCornerRadius;
        res["GlassCardCornerRadius"] = tokens.CardCornerRadius;

        res["GlassLevel1FillBrush"] = tokens.Level1WindowFillBrush;
        res["GlassLevel1RimBrush"] = tokens.Level1WindowRimBrush;
        res["GlassLevel1TopSpecularBrush"] = tokens.Level1TopSpecularBrush;
        res["GlassLevel1AmbientGlowBrush"] = tokens.Level1AmbientGlowBrush;

        res["GlassLevel2FillBrush"] = tokens.Level2ContainerFillBrush;
        res["GlassLevel2BorderBrush"] = tokens.Level2ContainerBorderBrush;

        res["GlassLevel3CardFillBrush"] = tokens.Level3CardFillBrush;
        res["GlassLevel3CardUnreadFillBrush"] = tokens.Level3CardUnreadFillBrush;
        res["GlassLevel3CardBorderBrush"] = tokens.Level3CardBorderBrush;
        res["GlassLevel3CardUnreadBorderBrush"] = tokens.Level3CardUnreadBorderBrush;
        res["GlassLevel3InputFillBrush"] = tokens.Level3InputFillBrush;

        res["GlassLevel4FillBrush"] = tokens.Level4ControlFillBrush;
        res["GlassLevel4HoverBrush"] = tokens.Level4ControlHoverBrush;
        res["GlassLevel4PressedBrush"] = tokens.Level4ControlPressedBrush;
        res["GlassLevel4BorderBrush"] = tokens.Level4ControlBorderBrush;

        res["GlassPrimaryAccentFillBrush"] = tokens.PrimaryAccentFillBrush;
        res["GlassPrimaryAccentHoverBrush"] = tokens.PrimaryAccentHoverBrush;
        res["GlassPrimaryAccentBorderBrush"] = tokens.PrimaryAccentBorderBrush;

        res["GlassDangerFillBrush"] = tokens.DangerFillBrush;
        res["GlassDangerBorderBrush"] = tokens.DangerBorderBrush;

        res["GlassPrimaryTextBrush"] = tokens.PrimaryTextBrush;
        res["GlassSecondaryTextBrush"] = tokens.SecondaryTextBrush;
        res["GlassMutedTextBrush"] = tokens.MutedTextBrush;
        res["GlassAccentTextBrush"] = tokens.AccentTextBrush;

        return tokens;
    }

    private static LinearGradientBrush CreateDiagonalGradient(
        System.Windows.Media.Color topLeft,
        System.Windows.Media.Color middle,
        System.Windows.Media.Color bottomRight)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(topLeft, 0.0));
        brush.GradientStops.Add(new GradientStop(middle, 0.52));
        brush.GradientStops.Add(new GradientStop(bottomRight, 1.0));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateVerticalGradient(
        System.Windows.Media.Color top,
        System.Windows.Media.Color bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop(top, 0.0));
        brush.GradientStops.Add(new GradientStop(bottom, 1.0));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Brush FreezeBrush(System.Windows.Media.Brush brush)
    {
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        return brush;
    }
}
