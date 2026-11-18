import { AccessoryAnchorName } from './AppearanceTypes';

export interface RendererAccessoryDefinition {
  id: string;
  displayName: string;
  anchor: AccessoryAnchorName;
  defaultTintHex: string;
}

export const ACCESSORY_CATALOG: Readonly<Record<string, RendererAccessoryDefinition>> = {
  wizard_hat: {
    id: 'wizard_hat',
    displayName: 'Wizard Hat',
    anchor: 'Head',
    defaultTintHex: '#6366F1'
  },
  cap: {
    id: 'cap',
    displayName: 'Baseball Cap',
    anchor: 'Head',
    defaultTintHex: '#EF4444'
  },
  crown: {
    id: 'crown',
    displayName: 'Royal Crown',
    anchor: 'Head',
    defaultTintHex: '#FACC15'
  },
  sunglasses: {
    id: 'sunglasses',
    displayName: 'Cool Shades',
    anchor: 'Face',
    defaultTintHex: '#0F172A'
  },
  glasses: {
    id: 'glasses',
    displayName: 'Reading Glasses',
    anchor: 'Face',
    defaultTintHex: '#E2E8F0'
  },
  headphones: {
    id: 'headphones',
    displayName: 'Studio Headphones',
    anchor: 'Head',
    defaultTintHex: '#38BDF8'
  },
  scarf: {
    id: 'scarf',
    displayName: 'Cozy Scarf',
    anchor: 'Front',
    defaultTintHex: '#F43F5E'
  },
  wings: {
    id: 'wings',
    displayName: 'Angel Wings',
    anchor: 'Back',
    defaultTintHex: '#F8FAFC'
  }
};

