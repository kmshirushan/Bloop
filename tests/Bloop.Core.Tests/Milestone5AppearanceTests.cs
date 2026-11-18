using Bloop.Core.Appearance;
using Bloop.Core.Movement;
using Bloop.Storage;
using Xunit;

namespace Bloop.Core.Tests;

public sealed class Milestone5AppearanceTests
{
    [Fact]
    public void DatabaseService_AppliesMigrationV3_AndSeedsDefaultClassicBlueProfile()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m5_mig_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            Assert.Equal(3, db.SchemaVersion);

            var repo = new AppearanceRepository(db);
            var profiles = repo.GetProfiles();
            Assert.Single(profiles);

            var active = repo.GetActiveProfile();
            Assert.Equal(AppearanceDefaults.DefaultProfileId, active.ProfileId);
            Assert.Equal(AppearanceDefaults.DefaultProfileName, active.Name);
            Assert.True(active.IsActive);
            Assert.Equal(CharacterMaterialPreset.Classic, active.Character.Preset);
            Assert.Equal("#0284C7", active.Character.BodyColor);
            Assert.Equal("#38BDF8", active.Character.AccentColor);
            Assert.Equal(1.0, active.Character.CharacterScale);
            Assert.Empty(active.Character.EquippedAccessories);
            Assert.Equal(PanelThemePreset.Dark, active.Panel.Theme);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AppearanceValidator_ClampsValues_NormalizesHex_AndFiltersUnknownAccessories()
    {
        var dirtyCharacter = new CharacterAppearance(
            Preset: (CharacterMaterialPreset)999,
            BodyColor: "not-a-color",
            AccentColor: "38bdf8",
            EyeStyle: (EyeStyle)999,
            Roughness: -1.5,
            Metalness: 4.2,
            Opacity: 0.05,
            EnableGlow: true,
            GlowIntensity: 99.0,
            CharacterScale: 9.0,
            EquippedAccessories: ["WIZARD_HAT", "unknown_hat", "sunglasses", "wizard_hat"],
            AccessoryTintColor: "invalid"
        );

        var normChar = AppearanceValidator.Normalize(dirtyCharacter);
        Assert.Equal(CharacterMaterialPreset.Classic, normChar.Preset);
        Assert.Equal("#0284C7", normChar.BodyColor);
        Assert.Equal("#38BDF8", normChar.AccentColor);
        Assert.Equal(EyeStyle.Classic, normChar.EyeStyle);
        Assert.Equal(0.0, normChar.Roughness);
        Assert.Equal(1.0, normChar.Metalness);
        Assert.Equal(0.25, normChar.Opacity);
        Assert.Equal(2.0, normChar.GlowIntensity);
        Assert.Equal(AppearanceDefaults.MaxScale, normChar.CharacterScale);
        Assert.Equal(2, normChar.EquippedAccessories.Count);
        Assert.Contains("wizard_hat", normChar.EquippedAccessories);
        Assert.Contains("sunglasses", normChar.EquippedAccessories);
        Assert.Equal(AppearanceDefaults.DefaultCharacter.AccessoryTintColor, normChar.AccessoryTintColor);

        var dirtyPanel = new PanelAppearance(
            Theme: (PanelThemePreset)888,
            BackgroundColor: "bad",
            BackgroundOpacity: 0.01,
            AccentColor: "#112233",
            CardOpacity: 2.5,
            CornerRadius: 100.0,
            CardDensity: (CardDensity)777,
            EnableGlassBlur: true,
            SynchronizeAccent: true
        );

        var normPanel = AppearanceValidator.Normalize(dirtyPanel, normChar.AccentColor);
        Assert.Equal(PanelThemePreset.Dark, normPanel.Theme);
        Assert.Equal("#0F172A", normPanel.BackgroundColor);
        Assert.Equal(0.35, normPanel.BackgroundOpacity);
        Assert.Equal("#38BDF8", normPanel.AccentColor); // Synchronized from character accent
        Assert.Equal(1.0, normPanel.CardOpacity);
        Assert.Equal(28.0, normPanel.CornerRadius);
        Assert.Equal(CardDensity.Comfortable, normPanel.CardDensity);
    }

    [Fact]
    public void AppearancePresets_AppliesAllCharacterAndPanelPresetsCleanly()
    {
        var baseChar = AppearanceDefaults.DefaultCharacter;
        foreach (var preset in Enum.GetValues<CharacterMaterialPreset>())
        {
            var applied = AppearancePresets.ApplyCharacterPreset(baseChar, preset);
            var normalized = AppearanceValidator.Normalize(applied);
            Assert.Equal(preset, normalized.Preset);
            Assert.InRange(normalized.Opacity, 0.25, 1.0);
            Assert.InRange(normalized.Roughness, 0.0, 1.0);
            Assert.InRange(normalized.Metalness, 0.0, 1.0);
        }

        var basePanel = AppearanceDefaults.DefaultPanel;
        foreach (var theme in Enum.GetValues<PanelThemePreset>())
        {
            var applied = AppearancePresets.ApplyPanelPreset(basePanel, theme);
            var normalized = AppearanceValidator.Normalize(applied, baseChar.AccentColor);
            Assert.Equal(theme, normalized.Theme);
            Assert.InRange(normalized.BackgroundOpacity, 0.35, 1.0);
            Assert.InRange(normalized.CardOpacity, 0.35, 1.0);
        }
    }

    [Fact]
    public void AppearanceRepository_SupportsCreateDuplicateRenameActivateResetAndDelete()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m5_crud_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var repo = new AppearanceRepository(db);

            // Cannot delete the last remaining profile
            Assert.False(repo.DeleteProfile(AppearanceDefaults.DefaultProfileId));

            // Create custom profile
            var customChar = AppearancePresets.ApplyCharacterPreset(AppearanceDefaults.DefaultCharacter, CharacterMaterialPreset.LiquidGlass) with
            {
                BodyColor = "#8B5CF6",
                AccentColor = "#C4B5FD",
                CharacterScale = 1.25,
                EquippedAccessories = ["crown", "wings"]
            };
            var customPanel = AppearancePresets.ApplyPanelPreset(AppearanceDefaults.DefaultPanel, PanelThemePreset.LiquidGlass);

            var created = repo.CreateProfile("Cyber Violet", customChar, customPanel, setActive: true);
            Assert.Equal("Cyber Violet", created.Name);
            Assert.True(created.IsActive);
            Assert.Equal(created.ProfileId, repo.GetActiveProfile().ProfileId);
            Assert.Equal(2, repo.GetProfiles().Count);

            // Duplicate
            var copy = repo.DuplicateProfile(created.ProfileId, "Cyber Violet Copy", setActive: false);
            Assert.Equal("Cyber Violet Copy", copy.Name);
            Assert.Equal("#8B5CF6", copy.Character.BodyColor);
            Assert.Equal(3, repo.GetProfiles().Count);

            // Rename
            var renamed = repo.RenameProfile(copy.ProfileId, "Royal Glass");
            Assert.Equal("Royal Glass", renamed.Name);

            // Set active
            var activated = repo.SetActiveProfile(renamed.ProfileId);
            Assert.True(activated.IsActive);
            Assert.Equal(renamed.ProfileId, repo.GetActiveProfile().ProfileId);

            // Reset to defaults
            var reset = repo.ResetProfileToDefaults(renamed.ProfileId);
            Assert.Equal(CharacterMaterialPreset.Classic, reset.Character.Preset);
            Assert.Equal("#0284C7", reset.Character.BodyColor);
            Assert.Empty(reset.Character.EquippedAccessories);

            // Delete active profile falls back to another profile
            Assert.True(repo.DeleteProfile(renamed.ProfileId));
            Assert.Equal(2, repo.GetProfiles().Count);
            Assert.NotNull(repo.GetActiveProfile());
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AppearanceRepository_RecoversGracefullyFromCorruptedJsonInDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m5_corrupt_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            using (var conn = db.CreateConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO appearance_profiles (
                        profile_id, profile_name, schema_version, character_json, panel_json, created_at_utc, updated_at_utc, is_active
                    ) VALUES (
                        'broken-profile', 'Broken', 1, '{not valid json', '<<<bad>>>', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 1
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            var repo = new AppearanceRepository(db);
            var active = repo.GetActiveProfile();
            Assert.Equal("broken-profile", active.ProfileId);
            Assert.Equal(CharacterMaterialPreset.Classic, active.Character.Preset);
            Assert.Equal("#0284C7", active.Character.BodyColor);
            Assert.Equal(PanelThemePreset.Dark, active.Panel.Theme);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void MovementController_RecalculatesPositionForScaleChange_AcrossAllEdgesAndStates()
    {
        var workArea = new WorkAreaBounds(0, 0, 1920, 1040, "primary");
        var controller = new MovementController(new Random(42));

        double size100 = AppearanceDefaults.CalculateWindowSizeDip(1.0);  // 260
        double size150 = AppearanceDefaults.CalculateWindowSizeDip(1.5);  // 390
        double size065 = AppearanceDefaults.CalculateWindowSizeDip(0.65); // 169
        Assert.Equal(260, size100);
        Assert.Equal(390, size150);
        Assert.Equal(169, size065);

        // 1. Free / Idle scaling preserves center point
        controller.SyncPosition(500, 400, "primary", DockingEdge.None);
        var freeRes = controller.RecalculatePositionForScaleChange(size100, size150, workArea);
        Assert.False(freeRes.IsDeferredUntilHopComplete);
        Assert.Equal(500 + 130 - 195, freeRes.NewLeftDip);
        Assert.Equal(400 + 130 - 195, freeRes.NewTopDip);

        // 2. Docked Right edge stays flush against workArea.Right - newWindowSizeDip
        controller.SyncPosition(1920 - size100, 300, "primary", DockingEdge.Right);
        var rightRes = controller.RecalculatePositionForScaleChange(size100, size150, workArea);
        Assert.False(rightRes.IsDeferredUntilHopComplete);
        Assert.NotNull(rightRes.DockPosition);
        Assert.Equal(1920 - size150, rightRes.NewLeftDip);

        // 3. Docked Bottom edge stays flush against workArea.Bottom - newWindowSizeDip
        controller.SyncPosition(600, 1040 - size150, "primary", DockingEdge.Bottom);
        var bottomRes = controller.RecalculatePositionForScaleChange(size150, size065, workArea);
        Assert.False(bottomRes.IsDeferredUntilHopComplete);
        Assert.Equal(1040 - size065, bottomRes.NewTopDip);

        // 4. Docked Left and Top edges stay anchored at workArea.Left and workArea.Top
        controller.SyncPosition(0, 300, "primary", DockingEdge.Left);
        var leftRes = controller.RecalculatePositionForScaleChange(size065, size150, workArea);
        Assert.Equal(0, leftRes.NewLeftDip);

        controller.SyncPosition(400, 0, "primary", DockingEdge.Top);
        var topRes = controller.RecalculatePositionForScaleChange(size150, size100, workArea);
        Assert.Equal(0, topRes.NewTopDip);

        // 5. Scaling during an active hop is deferred until hop completes so trajectory is never corrupted
        Assert.True(controller.TriggerManualAutonomousHop(size100, size100, workArea));
        Assert.Equal(MovementState.Hopping, controller.CurrentState);
        var deferredRes = controller.RecalculatePositionForScaleChange(size100, size150, workArea);
        Assert.True(deferredRes.IsDeferredUntilHopComplete);
    }
}
