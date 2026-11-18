export type CharacterMaterialPresetName =
  | 'Classic'
  | 'Matte'
  | 'Glass'
  | 'LiquidGlass'
  | 'Metallic'
  | 'Soft';

export type EyeStyleName = 'Classic' | 'Starry' | 'Warm' | 'Cyber';

export type AccessoryAnchorName = 'Head' | 'Face' | 'Front' | 'Back';

export interface CharacterAppearanceConfig {
  preset: CharacterMaterialPresetName;
  bodyColor: string;
  accentColor: string;
  eyeStyle: EyeStyleName;
  roughness: number;
  metalness: number;
  opacity: number;
  enableGlow: boolean;
  glowIntensity: number;
  characterScale: number;
  equippedAccessories: string[];
  accessoryTintColor?: string;
}

export interface HostAppearanceMessagePayload {
  profileId: string;
  character: CharacterAppearanceConfig;
  isPreview?: boolean;
}

export const DEFAULT_CHARACTER_APPEARANCE: CharacterAppearanceConfig = {
  preset: 'Classic',
  bodyColor: '#0284C7',
  accentColor: '#38BDF8',
  eyeStyle: 'Classic',
  roughness: 0.14,
  metalness: 0.04,
  opacity: 1.0,
  enableGlow: false,
  glowIntensity: 0.35,
  characterScale: 1.0,
  equippedAccessories: [],
  accessoryTintColor: '#FACC15'
};

