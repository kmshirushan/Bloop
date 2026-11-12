import * as THREE from 'three';
import { BloopModel } from '../scene/BloopModel';

export type DockingEdgeName = 'none' | 'left' | 'right' | 'top' | 'bottom';

export interface HopSequenceParams {
  edge: DockingEdgeName;
  targetOffsetX: number;
  targetOffsetY: number;
  directionX: number;
  directionY: number;
  anticipationMs: number;
  flightMs: number;
  landingMs: number;
}

export interface HopModifier {
  scaleYMultiplier: number;
  offsetYDelta: number;
  rollZDelta: number;
  lookOverrideX: number | null;
  lookOverrideY: number | null;
  isHopping: boolean;
}

/**
 * Dedicated 3D movement & docking animation controller in the Three.js renderer.
 * Coordinates:
 * - 4-stage hop sequence (Anticipation -> Takeoff -> Flight -> Landing)
 * - Edge docking viewport translation (approx 60% visible body area, clipped naturally at viewport border)
 * - Peek-on-approach when docked and the user's cursor approaches Bloop
 */
export class HopAnimationController {
  private currentEdge: DockingEdgeName = 'none';
  private baseOffsetX = 0;
  private baseOffsetY = 0;
  private currentRootX = 0;
  private currentRootY = 0;

  private isHopping = false;
  private hopElapsedSec = 0;
  private anticipationSec = 0.13;
  private flightSec = 0.52;
  private landingSec = 0.30;
  private hopStartRootX = 0;
  private hopStartRootY = 0;
  private hopTargetRootX = 0;
  private hopTargetRootY = 0;
  private hopDirX = 0;
  private hopDirY = 0;

  private cursorProximity = 0; // [0..1] when cursor approaches docked mascot

  constructor(private readonly model: BloopModel) {}

  public getDockedEdge(): DockingEdgeName {
    return this.currentEdge;
  }

  public isDocked(): boolean {
    return this.currentEdge !== 'none' && !this.isHopping;
  }

  public setDockedConfig(edge: DockingEdgeName, offsetX: number, offsetY: number): void {
    this.currentEdge = edge;
    this.baseOffsetX = offsetX;
    this.baseOffsetY = offsetY;
    if (!this.isHopping) {
      this.hopTargetRootX = offsetX;
      this.hopTargetRootY = offsetY;
    }
  }

  public clearDockForDrag(): void {
    this.currentEdge = 'none';
    this.baseOffsetX = 0;
    this.baseOffsetY = 0;
    this.hopTargetRootX = 0;
    this.hopTargetRootY = 0;
    this.isHopping = false;
  }

  public updateCursorProximity(normX: number, normY: number, isInsideWindow: boolean): void {
    if (this.currentEdge === 'none') {
      this.cursorProximity = 0;
      return;
    }
    const dist = Math.hypot(normX, normY);
    if (isInsideWindow || dist < 0.45) {
      this.cursorProximity = THREE.MathUtils.clamp(1.0 - dist / 0.55, 0.15, 1.0);
    } else {
      this.cursorProximity = 0;
    }
  }

  public startHop(params: HopSequenceParams): void {
    this.isHopping = true;
    this.hopElapsedSec = 0;
    this.currentEdge = params.edge;
    this.baseOffsetX = params.targetOffsetX;
    this.baseOffsetY = params.targetOffsetY;

    this.hopStartRootX = this.currentRootX;
    this.hopStartRootY = this.currentRootY;
    this.hopTargetRootX = params.targetOffsetX;
    this.hopTargetRootY = params.targetOffsetY;

    this.hopDirX = THREE.MathUtils.clamp(params.directionX, -1, 1);
    this.hopDirY = THREE.MathUtils.clamp(params.directionY, -1, 1);

    this.anticipationSec = Math.max(0.06, params.anticipationMs * 0.001);
    this.flightSec = Math.max(0.18, params.flightMs * 0.001);
    this.landingSec = Math.max(0.12, params.landingMs * 0.001);
  }

  public update(rawDt: number): HopModifier {
    const dt = Math.min(rawDt, 0.05);
    const damp = 1.0 - Math.exp(-12.0 * dt);

    const modifier: HopModifier = {
      scaleYMultiplier: 1.0,
      offsetYDelta: 0,
      rollZDelta: 0,
      lookOverrideX: null,
      lookOverrideY: null,
      isHopping: this.isHopping
    };

    if (this.isHopping) {
      this.hopElapsedSec += dt;
      const tAnt = this.anticipationSec;
      const tFlightEnd = tAnt + this.flightSec;
      const tTotal = tFlightEnd + this.landingSec;

      if (this.hopElapsedSec <= tAnt) {
        // Phase A: Anticipation (crouch down & compress body, eyes look toward destination)
        const p = THREE.MathUtils.clamp(this.hopElapsedSec / tAnt, 0, 1);
        const ease = Math.sin(p * Math.PI * 0.5);
        modifier.scaleYMultiplier = 1.0 - 0.22 * ease;
        modifier.offsetYDelta = -0.14 * ease;
        modifier.lookOverrideX = this.hopDirX * 0.85;
        modifier.lookOverrideY = this.hopDirY * 0.65;
      } else if (this.hopElapsedSec <= tFlightEnd) {
        // Phase B & C: Takeoff & Parabolic Flight
        const p = THREE.MathUtils.clamp((this.hopElapsedSec - tAnt) / this.flightSec, 0, 1);
        const smoothP = p * p * (3.0 - 2.0 * p); // smoothstep

        // Interpolate root viewport offset smoothly toward docked/free target
        this.currentRootX = THREE.MathUtils.lerp(this.hopStartRootX, this.hopTargetRootX, smoothP);
        this.currentRootY = THREE.MathUtils.lerp(this.hopStartRootY, this.hopTargetRootY, smoothP);

        // Parabolic vertical arc: 4 * p * (1 - p) peaks at p = 0.5
        const arc = 4.0 * p * (1.0 - p);
        modifier.offsetYDelta = arc * 0.34;
        // Stretch during takeoff/ascent, normalize near apex
        modifier.scaleYMultiplier = 1.0 + Math.sin(p * Math.PI) * 0.20;
        modifier.rollZDelta = -this.hopDirX * Math.sin(p * Math.PI) * 0.26;
        modifier.lookOverrideX = this.hopDirX * 0.7;
        modifier.lookOverrideY = this.hopDirY * 0.5;
      } else if (this.hopElapsedSec <= tTotal) {
        // Phase D: Landing impact squash & damped settling bounce
        this.currentRootX = this.hopTargetRootX;
        this.currentRootY = this.hopTargetRootY;

        const p = THREE.MathUtils.clamp((this.hopElapsedSec - tFlightEnd) / this.landingSec, 0, 1);
        const decay = Math.exp(-5.0 * p);
        const wave = Math.cos(p * Math.PI * 2.5);
        modifier.scaleYMultiplier = 1.0 - 0.20 * decay * wave;
        modifier.offsetYDelta = -0.09 * decay * wave;
      } else {
        this.isHopping = false;
        this.currentRootX = this.hopTargetRootX;
        this.currentRootY = this.hopTargetRootY;
      }
    } else {
      // When docked and cursor approaches, peek out slightly (~18% closer to center)
      const peekFactor = this.currentEdge !== 'none' ? 1.0 - this.cursorProximity * 0.22 : 1.0;
      const desiredX = this.baseOffsetX * peekFactor;
      const desiredY = this.baseOffsetY * peekFactor;

      this.currentRootX = THREE.MathUtils.lerp(this.currentRootX, desiredX, damp);
      this.currentRootY = THREE.MathUtils.lerp(this.currentRootY, desiredY, damp);

      // Subtle docked lean toward the desktop interior
      if (this.currentEdge === 'left') {
        modifier.rollZDelta = -0.10;
      } else if (this.currentEdge === 'right') {
        modifier.rollZDelta = 0.10;
      }
    }

    this.model.root.position.x = this.currentRootX;
    this.model.root.position.y = this.currentRootY;
    // Hide floating floor shadow when docked to top/left/right edge so it looks anchored to the screen bezel
    this.model.shadowMesh.visible =
      this.currentEdge === 'none' || this.currentEdge === 'bottom';

    return modifier;
  }
}

