import * as THREE from 'three';
import { BloopModel } from '../scene/BloopModel';

/**
 * Coordinates character scale and camera framing so that scaled characters (0.65x - 1.50x)
 * and tall/wide 3D accessories (e.g. wizard_hat, crown, wings) never clip inside the viewport
 * during hops, bounces, or docking.
 */
export class CharacterScaleController {
  private currentScale = 1.0;

  constructor(
    private readonly model: BloopModel,
    private readonly camera: THREE.PerspectiveCamera
  ) {}

  public getScale(): number {
    return this.currentScale;
  }

  public applyScaleAndFraming(scale: number, equippedAccessories: readonly string[]): void {
    this.currentScale = THREE.MathUtils.clamp(Number.isFinite(scale) ? scale : 1.0, 0.65, 1.5);

    const hasWizardHat = equippedAccessories.includes('wizard_hat');
    const hasCrownOrCap =
      equippedAccessories.includes('crown') ||
      equippedAccessories.includes('cap') ||
      equippedAccessories.includes('headphones');
    const hasWings = equippedAccessories.includes('wings');

    // Adjust camera distance & vertical framing when tall/wide accessories are equipped
    // so the tip of the wizard hat or wings never clips at the viewport boundary.
    let targetCameraZ = 4.7;
    let targetCameraY = 0.05;
    let lookAtY = -0.05;

    if (hasWizardHat) {
      targetCameraZ = 5.45;
      targetCameraY = 0.22;
      lookAtY = 0.14;
    } else if (hasCrownOrCap || hasWings) {
      targetCameraZ = 5.05;
      targetCameraY = 0.12;
      lookAtY = 0.04;
    }

    this.camera.position.set(0, targetCameraY, targetCameraZ);
    this.camera.lookAt(0, lookAtY, 0);
    this.camera.updateProjectionMatrix();

    // Keep root scale at 1.0 inside the normalized frustum while WPF scales the host window
    // (260 * scale DIP), ensuring crisp rasterization at 1.5x and zero frustum clipping.
    this.model.root.scale.setScalar(1.0);
  }
}

