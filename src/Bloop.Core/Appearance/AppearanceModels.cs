using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bloop.Core.Appearance;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterMaterialPreset
{
    Classic,
    Matte,
    Glass,
    LiquidGlass,
    Metallic,
    Soft
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EyeStyle
{
    Classic,
    Starry,
    Warm,
    Cyber
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PanelThemePreset
{
    Dark,
    Light,
    Glass,
    LiquidGlass,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardDensity
{
    Compact,
    Comfortable
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessoryCategory
{
    Headwear,
    Face,
    Wearables,
    Body
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessoryAnchor
{
    Head,
    Face,
    Front,
    Back
}

public sealed record AccessoryMetadata(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("category")] AccessoryCategory Category,
    [property: JsonPropertyName("anchor")] AccessoryAnchor Anchor,
    [property: JsonPropertyName("supportsTint")] bool SupportsTint = true,
    [property: JsonPropertyName("defaultTintHex")] string DefaultTintHex = "#38BDF8"
);

public sealed record CharacterAppearance(
    [property: JsonPropertyName("preset")] CharacterMaterialPreset Preset,
    [property: JsonPropertyName("bodyColor")] string BodyColor,
    [property: JsonPropertyName("accentColor")] string AccentColor,
    [property: JsonPropertyName("eyeStyle")] EyeStyle EyeStyle,
    [property: JsonPropertyName("roughness")] double Roughness,
    [property: JsonPropertyName("metalness")] double Metalness,
    [property: JsonPropertyName("opacity")] double Opacity,
    [property: JsonPropertyName("enableGlow")] bool EnableGlow,
    [property: JsonPropertyName("glowIntensity")] double GlowIntensity,
    [property: JsonPropertyName("characterScale")] double CharacterScale,
    [property: JsonPropertyName("equippedAccessories")] IReadOnlyList<string> EquippedAccessories,
    [property: JsonPropertyName("accessoryTintColor")] string AccessoryTintColor = "#FACC15"
);

public sealed record PanelAppearance(
    [property: JsonPropertyName("theme")] PanelThemePreset Theme,
    [property: JsonPropertyName("backgroundColor")] string BackgroundColor,
    [property: JsonPropertyName("backgroundOpacity")] double BackgroundOpacity,
    [property: JsonPropertyName("accentColor")] string AccentColor,
    [property: JsonPropertyName("cardOpacity")] double CardOpacity,
    [property: JsonPropertyName("cornerRadius")] double CornerRadius,
    [property: JsonPropertyName("cardDensity")] CardDensity CardDensity,
    [property: JsonPropertyName("enableGlassBlur")] bool EnableGlassBlur,
    [property: JsonPropertyName("synchronizeAccent")] bool SynchronizeAccent
);

public sealed record AppearanceProfile(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("character")] CharacterAppearance Character,
    [property: JsonPropertyName("panel")] PanelAppearance Panel,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("isActive")] bool IsActive = false
);

public static class AccessoryCatalog
{
    public static readonly IReadOnlyList<AccessoryMetadata> All =
    [
        new("wizard_hat", "Wizard Hat", AccessoryCategory.Headwear, AccessoryAnchor.Head, true, "#6366F1"),
        new("cap", "Baseball Cap", AccessoryCategory.Headwear, AccessoryAnchor.Head, true, "#EF4444"),
        new("crown", "Royal Crown", AccessoryCategory.Headwear, AccessoryAnchor.Head, true, "#FACC15"),
        new("sunglasses", "Cool Shades", AccessoryCategory.Face, AccessoryAnchor.Face, true, "#1E293B"),
        new("glasses", "Reading Glasses", AccessoryCategory.Face, AccessoryAnchor.Face, true, "#E2E8F0"),
        new("headphones", "Studio Headphones", AccessoryCategory.Wearables, AccessoryAnchor.Head, true, "#38BDF8"),
        new("scarf", "Cozy Scarf", AccessoryCategory.Wearables, AccessoryAnchor.Front, true, "#F43F5E"),
        new("wings", "Angel Wings", AccessoryCategory.Body, AccessoryAnchor.Back, true, "#F8FAFC")
    ];

    private static readonly HashSet<string> ValidIds = new(
        All.Select(a => a.Id),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsValidAccessoryId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ValidIds.Contains(id.Trim());

    public static AccessoryMetadata? GetById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(a => string.Equals(a.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class AppearanceDefaults
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultProfileId = "classic-blue";
    public const string DefaultProfileName = "Classic Blue";

    public const double MinScale = 0.65;
    public const double MaxScale = 1.50;
    public const double DefaultScale = 1.00;
    public const double BaseWindowSizeDip = 260.0;

    public static CharacterAppearance DefaultCharacter { get; } = new(
        Preset: CharacterMaterialPreset.Classic,
        BodyColor: "#0284C7",
        AccentColor: "#38BDF8",
        EyeStyle: EyeStyle.Classic,
        Roughness: 0.14,
        Metalness: 0.04,
        Opacity: 1.0,
        EnableGlow: false,
        GlowIntensity: 0.35,
        CharacterScale: DefaultScale,
        EquippedAccessories: [],
        AccessoryTintColor: "#FACC15"
    );

    public static PanelAppearance DefaultPanel { get; } = new(
        Theme: PanelThemePreset.Dark,
        BackgroundColor: "#0F172A",
        BackgroundOpacity: 0.96,
        AccentColor: "#38BDF8",
        CardOpacity: 0.92,
        CornerRadius: 16.0,
        CardDensity: CardDensity.Comfortable,
        EnableGlassBlur: false,
        SynchronizeAccent: true
    );

    public static AppearanceProfile CreateDefaultProfile(DateTimeOffset? timestampUtc = null)
    {
        var now = timestampUtc ?? DateTimeOffset.UtcNow;
        return new AppearanceProfile(
            ProfileId: DefaultProfileId,
            Name: DefaultProfileName,
            SchemaVersion: CurrentSchemaVersion,
            Character: DefaultCharacter,
            Panel: DefaultPanel,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            IsActive: true
        );
    }

    public static double CalculateWindowSizeDip(double characterScale)
    {
        var clamped = Math.Clamp(characterScale, MinScale, MaxScale);
        return Math.Round(BaseWindowSizeDip * clamped);
    }
}

public static class AppearancePresets
{
    public static CharacterAppearance ApplyCharacterPreset(CharacterAppearance current, CharacterMaterialPreset preset)
    {
        return preset switch
        {
            CharacterMaterialPreset.Classic => current with
            {
                Preset = CharacterMaterialPreset.Classic,
                BodyColor = "#0284C7",
                AccentColor = "#38BDF8",
                Roughness = 0.14,
                Metalness = 0.04,
                Opacity = 1.0,
                EnableGlow = false,
                GlowIntensity = 0.35
            },
            CharacterMaterialPreset.Matte => current with
            {
                Preset = CharacterMaterialPreset.Matte,
                Roughness = 0.78,
                Metalness = 0.02,
                Opacity = 1.0,
                EnableGlow = false,
                GlowIntensity = 0.15
            },
            CharacterMaterialPreset.Metallic => current with
            {
                Preset = CharacterMaterialPreset.Metallic,
                Roughness = 0.18,
                Metalness = 0.86,
                Opacity = 1.0,
                EnableGlow = false,
                GlowIntensity = 0.30
            },
            CharacterMaterialPreset.Glass => current with
            {
                Preset = CharacterMaterialPreset.Glass,
                Roughness = 0.06,
                Metalness = 0.08,
                Opacity = 0.54,
                EnableGlow = true,
                GlowIntensity = 0.45
            },
            CharacterMaterialPreset.LiquidGlass => current with
            {
                Preset = CharacterMaterialPreset.LiquidGlass,
                Roughness = 0.04,
                Metalness = 0.16,
                Opacity = 0.64,
                EnableGlow = true,
                GlowIntensity = 0.75
            },
            CharacterMaterialPreset.Soft => current with
            {
                Preset = CharacterMaterialPreset.Soft,
                Roughness = 0.48,
                Metalness = 0.0,
                Opacity = 1.0,
                EnableGlow = true,
                GlowIntensity = 0.25
            },
            _ => current
        };
    }

    public static PanelAppearance ApplyPanelPreset(PanelAppearance current, PanelThemePreset theme)
    {
        return theme switch
        {
            PanelThemePreset.Dark => current with
            {
                Theme = PanelThemePreset.Dark,
                BackgroundColor = "#0F172A",
                BackgroundOpacity = 0.96,
                CardOpacity = 0.92,
                EnableGlassBlur = false
            },
            PanelThemePreset.Light => current with
            {
                Theme = PanelThemePreset.Light,
                BackgroundColor = "#F8FAFC",
                BackgroundOpacity = 0.97,
                CardOpacity = 0.95,
                EnableGlassBlur = false
            },
            PanelThemePreset.Glass => current with
            {
                Theme = PanelThemePreset.Glass,
                BackgroundColor = "#0F172A",
                BackgroundOpacity = 0.68,
                CardOpacity = 0.62,
                EnableGlassBlur = true
            },
            PanelThemePreset.LiquidGlass => current with
            {
                Theme = PanelThemePreset.LiquidGlass,
                BackgroundColor = "#082F49",
                BackgroundOpacity = 0.58,
                CardOpacity = 0.54,
                EnableGlassBlur = true
            },
            PanelThemePreset.Custom => current with
            {
                Theme = PanelThemePreset.Custom
            },
            _ => current
        };
    }
}

public static partial class AppearanceValidator
{
    [GeneratedRegex("^#?(?:[0-9a-fA-F]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex Hex6Regex();

    public static string NormalizeHexColor(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        var trimmed = input.Trim();
        if (!Hex6Regex().IsMatch(trimmed))
        {
            return fallback;
        }

        var raw = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
        return $"#{raw.ToUpperInvariant()}";
    }

    public static (byte R, byte G, byte B) ParseRgb(string hexColor, (byte R, byte G, byte B) fallback)
    {
        var norm = NormalizeHexColor(hexColor, string.Empty);
        if (norm.Length != 7)
        {
            return fallback;
        }

        if (byte.TryParse(norm.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(norm.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(norm.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return (r, g, b);
        }

        return fallback;
    }

    public static CharacterAppearance Normalize(CharacterAppearance? character)
    {
        if (character is null)
        {
            return AppearanceDefaults.DefaultCharacter;
        }

        var preset = Enum.IsDefined(character.Preset) ? character.Preset : CharacterMaterialPreset.Classic;
        var eyeStyle = Enum.IsDefined(character.EyeStyle) ? character.EyeStyle : EyeStyle.Classic;
        var bodyColor = NormalizeHexColor(character.BodyColor, AppearanceDefaults.DefaultCharacter.BodyColor);
        var accentColor = NormalizeHexColor(character.AccentColor, AppearanceDefaults.DefaultCharacter.AccentColor);
        var tintColor = NormalizeHexColor(character.AccessoryTintColor, AppearanceDefaults.DefaultCharacter.AccessoryTintColor);

        var roughness = double.IsFinite(character.Roughness) ? Math.Clamp(character.Roughness, 0.0, 1.0) : 0.14;
        var metalness = double.IsFinite(character.Metalness) ? Math.Clamp(character.Metalness, 0.0, 1.0) : 0.04;
        var opacity = double.IsFinite(character.Opacity) ? Math.Clamp(character.Opacity, 0.25, 1.0) : 1.0;
        var glowIntensity = double.IsFinite(character.GlowIntensity) ? Math.Clamp(character.GlowIntensity, 0.0, 2.0) : 0.35;
        var scale = double.IsFinite(character.CharacterScale)
            ? Math.Clamp(character.CharacterScale, AppearanceDefaults.MinScale, AppearanceDefaults.MaxScale)
            : AppearanceDefaults.DefaultScale;

        var validAccessories = (character.EquippedAccessories ?? [])
            .Where(AccessoryCatalog.IsValidAccessoryId)
            .Select(id => id.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CharacterAppearance(
            Preset: preset,
            BodyColor: bodyColor,
            AccentColor: accentColor,
            EyeStyle: eyeStyle,
            Roughness: roughness,
            Metalness: metalness,
            Opacity: opacity,
            EnableGlow: character.EnableGlow,
            GlowIntensity: glowIntensity,
            CharacterScale: scale,
            EquippedAccessories: validAccessories,
            AccessoryTintColor: tintColor
        );
    }

    public static PanelAppearance Normalize(PanelAppearance? panel, string? characterAccentColor = null)
    {
        if (panel is null)
        {
            return AppearanceDefaults.DefaultPanel;
        }

        var theme = Enum.IsDefined(panel.Theme) ? panel.Theme : PanelThemePreset.Dark;
        var density = Enum.IsDefined(panel.CardDensity) ? panel.CardDensity : CardDensity.Comfortable;
        var bgColor = NormalizeHexColor(panel.BackgroundColor, AppearanceDefaults.DefaultPanel.BackgroundColor);
        var accentFallback = NormalizeHexColor(characterAccentColor, AppearanceDefaults.DefaultPanel.AccentColor);
        var accentColor = panel.SynchronizeAccent && !string.IsNullOrWhiteSpace(characterAccentColor)
            ? accentFallback
            : NormalizeHexColor(panel.AccentColor, accentFallback);

        var bgOpacity = double.IsFinite(panel.BackgroundOpacity) ? Math.Clamp(panel.BackgroundOpacity, 0.35, 1.0) : 0.96;
        var cardOpacity = double.IsFinite(panel.CardOpacity) ? Math.Clamp(panel.CardOpacity, 0.35, 1.0) : 0.92;
        var cornerRadius = double.IsFinite(panel.CornerRadius) ? Math.Clamp(panel.CornerRadius, 6.0, 28.0) : 16.0;

        return new PanelAppearance(
            Theme: theme,
            BackgroundColor: bgColor,
            BackgroundOpacity: bgOpacity,
            AccentColor: accentColor,
            CardOpacity: cardOpacity,
            CornerRadius: cornerRadius,
            CardDensity: density,
            EnableGlassBlur: panel.EnableGlassBlur,
            SynchronizeAccent: panel.SynchronizeAccent
        );
    }

    public static AppearanceProfile Normalize(AppearanceProfile? profile)
    {
        if (profile is null)
        {
            return AppearanceDefaults.CreateDefaultProfile();
        }

        var id = string.IsNullOrWhiteSpace(profile.ProfileId)
            ? Guid.NewGuid().ToString("N")
            : profile.ProfileId.Trim();
        var name = string.IsNullOrWhiteSpace(profile.Name)
            ? "Custom Profile"
            : profile.Name.Trim();
        if (name.Length > 64)
        {
            name = name[..64];
        }

        var normChar = Normalize(profile.Character);
        var normPanel = Normalize(profile.Panel, normChar.AccentColor);

        return profile with
        {
            ProfileId = id,
            Name = name,
            SchemaVersion = profile.SchemaVersion <= 0 ? AppearanceDefaults.CurrentSchemaVersion : profile.SchemaVersion,
            Character = normChar,
            Panel = normPanel
        };
    }
}

public interface IAppearanceRepository
{
    IReadOnlyList<AppearanceProfile> GetProfiles();
    AppearanceProfile GetActiveProfile();
    AppearanceProfile? GetProfileById(string profileId);
    AppearanceProfile SaveProfile(AppearanceProfile profile, bool setActive = false);
    AppearanceProfile CreateProfile(string name, CharacterAppearance? character = null, PanelAppearance? panel = null, bool setActive = true);
    AppearanceProfile DuplicateProfile(string sourceProfileId, string newName, bool setActive = true);
    AppearanceProfile RenameProfile(string profileId, string newName);
    bool DeleteProfile(string profileId);
    AppearanceProfile SetActiveProfile(string profileId);
    AppearanceProfile ResetProfileToDefaults(string profileId);
}

