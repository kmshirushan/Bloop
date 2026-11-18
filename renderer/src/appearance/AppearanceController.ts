import * as THREE from 'three';
import { BloopModel } from '../scene/BloopModel';
import {
  CharacterAppearanceConfig,
  DEFAULT_CHARACTER_APPEARANCE,
  HostAppearanceMessagePayload
} from './AppearanceTypes';
import { CharacterMaterialController } from './CharacterMaterialController';
import { CharacterScaleController } from './CharacterScaleController';
import { AccessoryManager } from './AccessoryManager';

/**
 * Coordinates live character material updates, character scaling/camera framing,
 * 3D accessory attachments, and non-destructive live preview rollback.
 */
export class AppearanceController {
  private readonly materialController: CharacterMaterialController;
  private readonly scaleController: CharacterScaleController;
  private readonly accessoryManager: AccessoryManager;

  private committedProfileId = 'classic-blue';
  private committedConfig: CharacterAppearanceConfig = {
    ...DEFAULT_CHARACTER_APPEARANCE,
    equippedAccessories: [...DEFAULT_CHARACTER_APPEARANCE.equippedAccessories]
  };

  private activeConfig: CharacterAppearanceConfig = {
    ...DEFAULT_CHARACTER_APPEARANCE,
    equippedAccessories: [...DEFAULT_CHARACTER_APPEARANCE.equippedAccessories]
  };

  constructor(model: BloopModel, camera: THREE.PerspectiveCamera) {
    this.materialController = new CharacterMaterialController(model);
    this.scaleController = new CharacterScaleController(model, camera);
    this.accessoryManager = new AccessoryManager(model);

    this.applyInternal(this.committedConfig);
  }

  public applyMessage(payload: HostAppearanceMessagePayload, isPreview: boolean) {
    const profileId = String(payload?.profileId || this.committedProfileId);
    const merged = this.normalizeConfig(payload?.character);

    if (!isPreview) {
      this.committedProfileId = profileId;
      this.committedConfig = this.cloneConfig(merged);
    }

    this.activeConfig = this.cloneConfig(merged);
    this.applyInternal(this.activeConfig);

    return {
      profileId,
      preset: this.activeConfig.preset,
      characterScale: this.scaleController.getScale(),
      equippedAccessories: this.accessoryManager.getEquippedIds()
    };
  }

  public cancelPreview() {
    this.activeConfig = this.cloneConfig(this.committedConfig);
    this.applyInternal(this.activeConfig);
    return {
      profileId: this.committedProfileId,
      preset: this.activeConfig.preset,
      characterScale: this.scaleController.getScale(),
      equippedAccessories: this.accessoryManager.getEquippedIds()
    };
  }

  public applyScaleOnly(scale: number) {
    const clamped = THREE.MathUtils.clamp(Number.isFinite(scale) ? scale : 1.0, 0.65, 1.5);
    this.activeConfig.characterScale = clamped;
    this.committedConfig.characterScale = clamped;
    this.scaleController.applyScaleAndFraming(
      clamped,
      this.accessoryManager.getEquippedIds()
    );
    return {
      profileId: this.committedProfileId,
      preset: this.activeConfig.preset,
      characterScale: this.scaleController.getScale(),
      equippedAccessories: this.accessoryManager.getEquippedIds()
    };
  }

  public updateAccessory(accessoryId: string, equip: boolean, tintColor?: string) {
    if (tintColor) {
      this.activeConfig.accessoryTintColor = tintColor;
    }
    this.accessoryManager.equipOne(accessoryId, equip, this.activeConfig.accessoryTintColor);
    const equipped = this.accessoryManager.getEquippedIds();
    this.activeConfig.equippedAccessories = equipped;
    this.committedConfig.equippedAccessories = [...equipped];
    this.scaleController.applyScaleAndFraming(this.activeConfig.characterScale, equipped);

    return {
      profileId: this.committedProfileId,
      preset: this.activeConfig.preset,
      characterScale: this.scaleController.getScale(),
      equippedAccessories: equipped
    };
  }

  public resetToDefault() {
    this.committedProfileId = 'classic-blue';
    this.committedConfig = this.cloneConfig(DEFAULT_CHARACTER_APPEARANCE);
    this.activeConfig = this.cloneConfig(DEFAULT_CHARACTER_APPEARANCE);
    this.applyInternal(this.activeConfig);
    return {
      profileId: this.committedProfileId,
      preset: this.activeConfig.preset,
      characterScale: this.scaleController.getScale(),
      equippedAccessories: this.accessoryManager.getEquippedIds()
    };
  }

  private applyInternal(config: CharacterAppearanceConfig): void {
    this.materialController.apply(config);
    this.accessoryManager.syncAccessories(
      config.equippedAccessories,
      config.accessoryTintColor
    );
    this.scaleController.applyScaleAndFraming(
      config.characterScale,
      this.accessoryManager.getEquippedIds()
    );
  }

  private normalizeConfig(partial?: Partial<CharacterAppearanceConfig>): CharacterAppearanceConfig {
    return {
      preset: partial?.preset ?? this.activeConfig.preset ?? 'Classic',
      bodyColor: partial?.bodyColor ?? this.activeConfig.bodyColor ?? '#0284C7',
      accentColor: partial?.accentColor ?? this.activeConfig.accentColor ?? '#38BDF8',
      eyeStyle: partial?.eyeStyle ?? this.activeConfig.eyeStyle ?? 'Classic',
      roughness: Number(partial?.roughness ?? this.activeConfig.roughness ?? 0.14),
      metalness: Number(partial?.metalness ?? this.activeConfig.metalness ?? 0.04),
      opacity: Number(partial?.opacity ?? this.activeConfig.opacity ?? 1.0),
      enableGlow: Boolean(partial?.enableGlow ?? this.activeConfig.enableGlow ?? false),
      glowIntensity: Number(partial?.glowIntensity ?? this.activeConfig.glowIntensity ?? 0.35),
      characterScale: Number(partial?.characterScale ?? this.activeConfig.characterScale ?? 1.0),
      equippedAccessories: Array.isArray(partial?.equippedAccessories)
        ? partial!.equippedAccessories.map((x) => String(x))
        : [...this.activeConfig.equippedAccessories],
      accessoryTintColor:
        partial?.accessoryTintColor ?? this.activeConfig.accessoryTintColor ?? '#FACC15'
    };
  }

  private cloneConfig(c: CharacterAppearanceConfig): CharacterAppearanceConfig {
    return {
      ...c,
      equippedAccessories: [...(c.equippedAccessories ?? [])]
    };
  }

  public dispose(): void {
    this.accessoryManager.dispose();
  }
}

