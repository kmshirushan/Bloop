import * as THREE from 'three';
import { BloopModel } from '../scene/BloopModel';
import { ACCESSORY_CATALOG } from './AccessoryCatalog';
import { AccessoryAnchorName } from './AppearanceTypes';

interface ActiveAccessoryInstance {
  id: string;
  group: THREE.Group;
  tintableMaterials: THREE.MeshStandardMaterial[];
  geometries: THREE.BufferGeometry[];
  materials: THREE.Material[];
}

/**
 * Manages procedural 3D accessories attached to Bloop's Head, Face, Front, and Back anchors.
 * Lazy-creates only equipped accessories and disposes geometries/materials when removed.
 */
export class AccessoryManager {
  private readonly equipped = new Map<string, ActiveAccessoryInstance>();
  private currentTintHex = '#FACC15';

  constructor(private readonly model: BloopModel) {}

  public getEquippedIds(): string[] {
    return Array.from(this.equipped.keys());
  }

  public syncAccessories(accessoryIds: readonly string[], tintColorHex?: string): void {
    if (tintColorHex) {
      this.currentTintHex = tintColorHex;
    }

    const desired = new Set<string>();
    for (const rawId of accessoryIds ?? []) {
      const normalized = String(rawId ?? '').trim().toLowerCase();
      if (ACCESSORY_CATALOG[normalized]) {
        desired.add(normalized);
      }
    }

    // 1. Remove accessories no longer equipped
    for (const [id, inst] of this.equipped.entries()) {
      if (!desired.has(id)) {
        this.disposeInstance(inst);
        this.equipped.delete(id);
      }
    }

    // 2. Equip newly requested accessories or update tint on existing ones
    for (const id of desired) {
      let inst = this.equipped.get(id);
      if (!inst) {
        inst = this.buildAccessory(id);
        if (inst) {
          const anchor = this.resolveAnchorGroup(ACCESSORY_CATALOG[id].anchor);
          anchor.add(inst.group);
          this.equipped.set(id, inst);
        }
      }
      if (inst) {
        this.applyTint(inst, this.currentTintHex || ACCESSORY_CATALOG[id].defaultTintHex);
      }
    }
  }

  public equipOne(rawId: string, equip: boolean, tintColorHex?: string): void {
    const id = String(rawId ?? '').trim().toLowerCase();
    if (!ACCESSORY_CATALOG[id]) return;

    const current = new Set(this.equipped.keys());
    if (equip) {
      current.add(id);
    } else {
      current.delete(id);
    }
    this.syncAccessories(Array.from(current), tintColorHex);
  }

  private resolveAnchorGroup(anchor: AccessoryAnchorName): THREE.Group {
    switch (anchor) {
      case 'Head':
        return this.model.headAnchor;
      case 'Face':
        return this.model.faceAnchor;
      case 'Front':
        return this.model.frontAnchor;
      case 'Back':
        return this.model.backAnchor;
    }
  }

  private applyTint(inst: ActiveAccessoryInstance, tintHex: string): void {
    const def = ACCESSORY_CATALOG[inst.id];
    const effectiveHex = tintHex || def?.defaultTintHex || '#FACC15';
    for (const mat of inst.tintableMaterials) {
      mat.color.set(effectiveHex);
      mat.needsUpdate = true;
    }
  }

  private buildAccessory(id: string): ActiveAccessoryInstance | undefined {
    const group = new THREE.Group();
    group.name = `Accessory_${id}`;
    group.renderOrder = 3;

    const tintableMaterials: THREE.MeshStandardMaterial[] = [];
    const geometries: THREE.BufferGeometry[] = [];
    const materials: THREE.Material[] = [];

    const def = ACCESSORY_CATALOG[id];
    const baseColor = def?.defaultTintHex ?? '#FACC15';

    const makeMat = (
      hex: string,
      roughness = 0.28,
      metalness = 0.15,
      tintable = false
    ): THREE.MeshStandardMaterial => {
      const mat = new THREE.MeshStandardMaterial({
        color: hex,
        roughness,
        metalness,
        transparent: false,
        opacity: 1.0,
        depthWrite: true
      });
      materials.push(mat);
      if (tintable) {
        tintableMaterials.push(mat);
      }
      return mat;
    };

    switch (id) {
      case 'wizard_hat': {
        const hatMat = makeMat(baseColor, 0.45, 0.08, true);
        const goldBandMat = makeMat('#FACC15', 0.22, 0.78, false);

        const brimGeo = new THREE.CylinderGeometry(0.72, 0.78, 0.06, 32);
        const coneGeo = new THREE.ConeGeometry(0.46, 0.88, 32);
        const bandGeo = new THREE.TorusGeometry(0.42, 0.045, 12, 32);
        geometries.push(brimGeo, coneGeo, bandGeo);

        const brim = new THREE.Mesh(brimGeo, hatMat);
        brim.position.set(0, -0.02, 0);

        const cone = new THREE.Mesh(coneGeo, hatMat);
        cone.position.set(0, 0.42, -0.03);
        cone.rotation.x = -0.14;

        const band = new THREE.Mesh(bandGeo, goldBandMat);
        band.rotation.x = Math.PI / 2;
        band.position.set(0, 0.03, 0);

        group.add(brim, cone, band);
        group.rotation.z = -0.05;
        break;
      }

      case 'cap': {
        const capMat = makeMat(baseColor, 0.48, 0.05, true);
        const buttonMat = makeMat('#FFFFFF', 0.35, 0.1, false);

        const domeGeo = new THREE.SphereGeometry(0.56, 28, 18, 0, Math.PI * 2, 0, Math.PI * 0.52);
        const visorGeo = new THREE.BoxGeometry(0.68, 0.045, 0.42);
        const btnGeo = new THREE.SphereGeometry(0.055, 12, 12);
        geometries.push(domeGeo, visorGeo, btnGeo);

        const dome = new THREE.Mesh(domeGeo, capMat);
        dome.position.set(0, -0.08, 0);

        const visor = new THREE.Mesh(visorGeo, capMat);
        visor.position.set(0, -0.05, 0.46);
        visor.rotation.x = 0.14;

        const btn = new THREE.Mesh(btnGeo, buttonMat);
        btn.position.set(0, 0.47, 0);

        group.add(dome, visor, btn);
        break;
      }

      case 'crown': {
        const goldMat = makeMat(baseColor, 0.18, 0.85, true);
        const gemMat = makeMat('#EF4444', 0.1, 0.25, false);

        const ringGeo = new THREE.CylinderGeometry(0.46, 0.42, 0.22, 24);
        const spikeGeo = new THREE.ConeGeometry(0.1, 0.24, 12);
        const gemGeo = new THREE.OctahedronGeometry(0.065, 0);
        geometries.push(ringGeo, spikeGeo, gemGeo);

        const ring = new THREE.Mesh(ringGeo, goldMat);
        ring.position.set(0, 0.08, 0);
        group.add(ring);

        const spikeCount = 5;
        for (let i = 0; i < spikeCount; i++) {
          const angle = (i / spikeCount) * Math.PI * 2;
          const spike = new THREE.Mesh(spikeGeo, goldMat);
          spike.position.set(Math.sin(angle) * 0.41, 0.28, Math.cos(angle) * 0.41);
          group.add(spike);
        }

        const frontGem = new THREE.Mesh(gemGeo, gemMat);
        frontGem.position.set(0, 0.09, 0.44);
        group.add(frontGem);
        break;
      }

      case 'sunglasses': {
        const frameMat = makeMat(baseColor, 0.2, 0.4, true);
        const lensMat = new THREE.MeshPhysicalMaterial({
          color: '#020617',
          roughness: 0.06,
          metalness: 0.25,
          clearcoat: 1.0,
          reflectivity: 1.0
        });
        materials.push(lensMat);

        const frameGeo = new THREE.BoxGeometry(0.32, 0.21, 0.045);
        const lensGeo = new THREE.BoxGeometry(0.27, 0.16, 0.052);
        const bridgeGeo = new THREE.BoxGeometry(0.18, 0.04, 0.04);
        geometries.push(frameGeo, lensGeo, bridgeGeo);

        const leftFrame = new THREE.Mesh(frameGeo, frameMat);
        leftFrame.position.set(-0.26, 0.01, 0.02);
        leftFrame.rotation.y = -0.16;

        const leftLens = new THREE.Mesh(lensGeo, lensMat);
        leftLens.position.copy(leftFrame.position);
        leftLens.rotation.copy(leftFrame.rotation);

        const rightFrame = new THREE.Mesh(frameGeo, frameMat);
        rightFrame.position.set(0.26, 0.01, 0.02);
        rightFrame.rotation.y = 0.16;

        const rightLens = new THREE.Mesh(lensGeo, lensMat);
        rightLens.position.copy(rightFrame.position);
        rightLens.rotation.copy(rightFrame.rotation);

        const bridge = new THREE.Mesh(bridgeGeo, frameMat);
        bridge.position.set(0, 0.05, 0.04);

        group.add(leftFrame, leftLens, rightFrame, rightLens, bridge);
        break;
      }

      case 'glasses': {
        const rimMat = makeMat(baseColor, 0.22, 0.72, true);
        const rimGeo = new THREE.TorusGeometry(0.145, 0.022, 12, 28);
        const bridgeGeo = new THREE.CylinderGeometry(0.016, 0.016, 0.14, 10);
        geometries.push(rimGeo, bridgeGeo);

        const leftRim = new THREE.Mesh(rimGeo, rimMat);
        leftRim.position.set(-0.26, 0.0, 0.03);
        leftRim.rotation.y = -0.16;

        const rightRim = new THREE.Mesh(rimGeo, rimMat);
        rightRim.position.set(0.26, 0.0, 0.03);
        rightRim.rotation.y = 0.16;

        const bridge = new THREE.Mesh(bridgeGeo, rimMat);
        bridge.rotation.z = Math.PI / 2;
        bridge.position.set(0, 0.02, 0.045);

        group.add(leftRim, rightRim, bridge);
        break;
      }

      case 'headphones': {
        const bandMat = makeMat('#1E293B', 0.35, 0.45, false);
        const cupMat = makeMat(baseColor, 0.25, 0.35, true);

        const arcGeo = new THREE.TorusGeometry(1.04, 0.055, 14, 36, Math.PI);
        const cupGeo = new THREE.CylinderGeometry(0.24, 0.24, 0.16, 24);
        geometries.push(arcGeo, cupGeo);

        const arc = new THREE.Mesh(arcGeo, bandMat);
        arc.position.set(0, -0.86, 0);

        const leftCup = new THREE.Mesh(cupGeo, cupMat);
        leftCup.rotation.z = Math.PI / 2;
        leftCup.position.set(-1.02, -0.86, 0);

        const rightCup = new THREE.Mesh(cupGeo, cupMat);
        rightCup.rotation.z = Math.PI / 2;
        rightCup.position.set(1.02, -0.86, 0);

        group.add(arc, leftCup, rightCup);
        break;
      }

      case 'scarf': {
        const scarfMat = makeMat(baseColor, 0.65, 0.02, true);
        const ringGeo = new THREE.TorusGeometry(0.76, 0.14, 16, 36);
        const tailGeo = new THREE.BoxGeometry(0.22, 0.48, 0.09);
        geometries.push(ringGeo, tailGeo);

        const neckRing = new THREE.Mesh(ringGeo, scarfMat);
        neckRing.rotation.x = Math.PI / 2 + 0.12;
        neckRing.position.set(0, -0.1, 0);

        const tail = new THREE.Mesh(tailGeo, scarfMat);
        tail.position.set(0.32, -0.34, 0.72);
        tail.rotation.z = -0.18;
        tail.rotation.x = -0.22;

        group.add(neckRing, tail);
        break;
      }

      case 'wings': {
        const wingMat = makeMat(baseColor, 0.32, 0.08, true);
        const primaryGeo = new THREE.CapsuleGeometry(0.14, 0.56, 12, 16);
        const secondaryGeo = new THREE.CapsuleGeometry(0.11, 0.38, 12, 16);
        geometries.push(primaryGeo, secondaryGeo);

        const makeWingSide = (sign: number): THREE.Group => {
          const side = new THREE.Group();
          const f1 = new THREE.Mesh(primaryGeo, wingMat);
          f1.position.set(sign * 1.08, 0.34, 0.15);
          f1.rotation.z = -sign * 0.68;

          const f2 = new THREE.Mesh(secondaryGeo, wingMat);
          f2.position.set(sign * 0.98, 0.06, 0.12);
          f2.rotation.z = -sign * 0.92;

          const f3 = new THREE.Mesh(secondaryGeo, wingMat);
          f3.position.set(sign * 0.84, -0.18, 0.08);
          f3.rotation.z = -sign * 1.15;

          side.add(f1, f2, f3);
          return side;
        };

        group.add(makeWingSide(-1), makeWingSide(1));
        break;
      }

      default:
        return undefined;
    }

    return {
      id,
      group,
      tintableMaterials,
      geometries,
      materials
    };
  }

  private disposeInstance(inst: ActiveAccessoryInstance): void {
    inst.group.parent?.remove(inst.group);
    for (const geo of inst.geometries) {
      geo.dispose();
    }
    for (const mat of inst.materials) {
      mat.dispose();
    }
  }

  public dispose(): void {
    for (const inst of this.equipped.values()) {
      this.disposeInstance(inst);
    }
    this.equipped.clear();
  }
}
