import * as THREE from 'three';
import { BloopModel } from '../scene/BloopModel';
import { IdleAnimation } from './IdleAnimation';
import { BlinkAnimation } from './BlinkAnimation';
import { HopModifier } from './HopAnimationController';

export type MascotStateName =
  | 'idle'
  | 'hover'
  | 'happy'
  | 'curious'
  | 'surprised'
  | 'sleepy'
  | 'annoyed'
  | 'notification'
  | 'dragging'
  | 'settling';

/**
 * Blends procedural animations (idle float, blinking, cursor eye-tracking,
 * happy bounce, squash-and-stretch, drag deformation, and smooth return to idle)
 * using frame-rate-independent exponential damping.
 */
export class ExpressionController {
  private readonly idleAnim = new IdleAnimation();
  private readonly blinkAnim = new BlinkAnimation();

  private currentState: MascotStateName = 'idle';
  private stateTimer = 0;

  // Normalized cursor look target in [-1, 1]
  private targetLookX = 0;
  private targetLookY = 0;
  private currentLookX = 0;
  private currentLookY = 0;

  // Spring physics for squash & stretch and happy bounce
  private bounceVelocityY = 0;
  private bounceOffsetY = 0;
  private squashStretchY = 1.0;
  private squashStretchVelocity = 0;

  // Drag deformation velocity
  private dragVelX = 0;
  private dragVelY = 0;
  private isHovered = false;
  private isDragging = false;
  private isQuietMode = false;

  // Expression-specific body tilt and gaze overrides
  private expressionRollOffset = 0;
  private expressionPitchOffset = 0;
  private expressionLookOverrideX: number | null = null;
  private expressionLookOverrideY: number | null = null;
  private sleepyPhase = 0;
  private annoyedPhase = 0;

  constructor(private readonly model: BloopModel) {}

  public getState(): MascotStateName {
    return this.currentState;
  }

  public setCursorTarget(normX: number, normY: number): void {
    this.targetLookX = THREE.MathUtils.clamp(normX, -1, 1);
    this.targetLookY = THREE.MathUtils.clamp(normY, -1, 1);
  }

  public setQuietMode(quiet: boolean): void {
    this.isQuietMode = quiet;
  }

  public setHovered(hovered: boolean): void {
    if (this.isHovered === hovered) return;
    this.isHovered = hovered;
    if (hovered && this.currentState === 'idle') {
      this.currentState = 'hover';
      // Subtle excited squash impulse on hover entry
      this.squashStretchVelocity += 1.35;
    } else if (!hovered && this.currentState === 'hover') {
      this.currentState = 'idle';
    }
  }

  public triggerHappyBounce(): void {
    this.currentState = 'happy';
    this.stateTimer = 1.25;
    this.expressionRollOffset = 0;
    this.expressionPitchOffset = 0;
    this.expressionLookOverrideX = null;
    this.expressionLookOverrideY = null;
    // Launch upward with an initial squash-and-stretch impulse
    this.bounceVelocityY = 2.45;
    this.squashStretchY = 0.76;
    this.squashStretchVelocity = 4.4;
    this.blinkAnim.triggerImmediateBlink();
  }

  public triggerSurprised(): void {
    this.currentState = 'surprised';
    this.stateTimer = 1.65;
    this.expressionRollOffset = 0;
    this.expressionPitchOffset = -0.14;
    this.expressionLookOverrideX = 0;
    this.expressionLookOverrideY = 0.25;
    // Quick startle upward hop + tall stretch + widened eyes
    this.bounceVelocityY = 1.85;
    this.squashStretchY = 1.22;
    this.squashStretchVelocity = 2.8;
  }

  public triggerCurious(): void {
    this.currentState = 'curious';
    this.stateTimer = 2.2;
    // Inquisitive head tilt and side-upward glance
    const side = Math.random() < 0.5 ? -1 : 1;
    this.expressionRollOffset = 0.28 * side;
    this.expressionPitchOffset = 0.08;
    this.expressionLookOverrideX = 0.68 * side;
    this.expressionLookOverrideY = 0.35;
    this.bounceVelocityY = 0.65;
    this.squashStretchY = 1.07;
    this.squashStretchVelocity = 1.4;
  }

  public triggerSleepy(): void {
    this.currentState = 'sleepy';
    this.stateTimer = 3.2;
    this.sleepyPhase = 0;
    this.expressionRollOffset = 0.1;
    this.expressionPitchOffset = 0.22; // Drooping forward nod
    this.expressionLookOverrideX = 0;
    this.expressionLookOverrideY = -0.45;
    this.squashStretchY = 0.91;
    this.squashStretchVelocity = -0.6;
  }

  public triggerAnnoyed(): void {
    this.currentState = 'annoyed';
    this.stateTimer = 2.0;
    this.annoyedPhase = 0;
    this.expressionRollOffset = 0;
    this.expressionPitchOffset = 0.12;
    this.expressionLookOverrideX = 0;
    this.expressionLookOverrideY = -0.1;
    // Agitated double-huff compression
    this.bounceVelocityY = 1.65;
    this.squashStretchY = 0.74;
    this.squashStretchVelocity = 4.8;
  }

  public triggerNotificationAlert(): void {
    this.currentState = 'notification';
    this.stateTimer = 1.8;
    this.expressionRollOffset = 0;
    this.expressionPitchOffset = -0.10;
    this.expressionLookOverrideX = 0;
    this.expressionLookOverrideY = 0.15;
    this.bounceVelocityY = 2.1;
    this.squashStretchY = 1.18;
    this.squashStretchVelocity = 3.2;
  }

  public setDragging(dragging: boolean): void {
    if (this.isDragging === dragging) return;
    this.isDragging = dragging;
    if (dragging) {
      this.currentState = 'dragging';
      this.squashStretchVelocity += 1.6;
    } else {
      this.currentState = 'settling';
      this.stateTimer = 0.65;
      // Elastic settle impulse on release
      this.squashStretchY = 0.84;
      this.squashStretchVelocity = 3.2;
      this.dragVelX = 0;
      this.dragVelY = 0;
    }
  }

  public setDragVelocity(vx: number, vy: number): void {
    this.dragVelX = THREE.MathUtils.clamp(vx, -1.5, 1.5);
    this.dragVelY = THREE.MathUtils.clamp(vy, -1.5, 1.5);
  }

  public setHostState(state: MascotStateName): void {
    switch (state) {
      case 'happy':
        this.triggerHappyBounce();
        return;
      case 'surprised':
        this.triggerSurprised();
        return;
      case 'curious':
        this.triggerCurious();
        return;
      case 'sleepy':
        this.triggerSleepy();
        return;
      case 'annoyed':
        this.triggerAnnoyed();
        return;
      case 'notification':
        this.triggerNotificationAlert();
        return;
      default:
        this.currentState = state;
        this.expressionRollOffset = 0;
        this.expressionPitchOffset = 0;
        this.expressionLookOverrideX = null;
        this.expressionLookOverrideY = null;
        break;
    }
  }

  public update(rawDt: number, hopMod?: HopModifier): void {
    const dt = Math.min(rawDt, 0.05); // Guard against tab/window suspension spikes
    const damp = 1.0 - Math.exp(-12.0 * dt);

    // 1. State timer decay back to idle/hover
    if (this.stateTimer > 0) {
      this.stateTimer -= dt;
      if (this.stateTimer <= 0 && !this.isDragging) {
        this.currentState = this.isHovered ? 'hover' : 'idle';
        this.expressionRollOffset = 0;
        this.expressionPitchOffset = 0;
        this.expressionLookOverrideX = null;
        this.expressionLookOverrideY = null;
      }
    }

    // 2. Smooth cursor look interpolation (with hop & curious/sleepy gaze override support)
    const desiredLookX = hopMod?.lookOverrideX ?? this.expressionLookOverrideX ?? this.targetLookX;
    const desiredLookY = hopMod?.lookOverrideY ?? this.expressionLookOverrideY ?? this.targetLookY;
    this.currentLookX = THREE.MathUtils.lerp(this.currentLookX, desiredLookX, damp);
    this.currentLookY = THREE.MathUtils.lerp(this.currentLookY, desiredLookY, damp);

    // 3. Idle float + breathing (slower, gentler when sleepy or in Focus/Quiet mode)
    const idleTimeScale =
      this.currentState === 'sleepy' ? 0.45 : this.isQuietMode ? 0.55 : 1.0;
    const idle = this.idleAnim.update(dt * idleTimeScale);
    if (this.currentState === 'sleepy') {
      this.sleepyPhase += dt;
    } else if (this.currentState === 'annoyed') {
      this.annoyedPhase += dt;
    }

    // 4. Damped spring for happy bounce vertical trajectory
    const bounceStiffness = 55.0;
    const bounceDamping = 7.5;
    const bounceAccel = -bounceStiffness * this.bounceOffsetY - bounceDamping * this.bounceVelocityY;
    this.bounceVelocityY += bounceAccel * dt;
    this.bounceOffsetY += this.bounceVelocityY * dt;

    // 5. Damped spring for volume-preserving squash & stretch
    const targetSquashY = this.isDragging
      ? 1.0 + Math.abs(this.dragVelY) * 0.16 - Math.abs(this.dragVelX) * 0.08
      : this.isHovered
        ? 1.03
        : 1.0;
    const squashStiffness = 110.0;
    const squashDamping = 10.5;
    const squashAccel =
      -squashStiffness * (this.squashStretchY - targetSquashY) -
      squashDamping * this.squashStretchVelocity;
    this.squashStretchVelocity += squashAccel * dt;
    this.squashStretchY += this.squashStretchVelocity * dt;

    const hopScaleY = hopMod?.scaleYMultiplier ?? 1.0;
    const clampedScaleY = THREE.MathUtils.clamp(
      this.squashStretchY * idle.breatheScaleY * hopScaleY,
      0.68,
      1.32
    );
    // Preserve volume: scaleX * scaleY * scaleZ ≈ 1
    const inverseRoot = 1.0 / Math.sqrt(clampedScaleY);
    const finalScaleX = inverseRoot * idle.breatheScaleX;
    const finalScaleZ = inverseRoot;

    // Apply position & scale to body pivot
    const hopOffsetY = hopMod?.offsetYDelta ?? 0;
    const totalY = idle.offsetY + Math.max(-0.18, this.bounceOffsetY) + hopOffsetY;
    this.model.bodyPivot.position.y = totalY;
    this.model.bodyPivot.scale.set(finalScaleX, clampedScaleY, finalScaleZ);
    this.model.setShadowHeightFactor(totalY);

    // 6. Body orientation & drag tilt
    const sleepyNod = this.currentState === 'sleepy' ? Math.sin(this.sleepyPhase * 2.0) * 0.08 : 0;
    const annoyedShakeYaw =
      this.currentState === 'annoyed'
        ? Math.sin(this.annoyedPhase * 34.0) * 0.28 * Math.exp(-this.annoyedPhase * 0.9)
        : 0;
    const hopRoll = hopMod?.rollZDelta ?? 0;
    const targetRotY = this.currentLookX * 0.32 + annoyedShakeYaw;
    const targetRotX =
      -this.currentLookY * 0.24 + this.dragVelY * 0.22 + this.expressionPitchOffset + sleepyNod;
    const targetRotZ =
      idle.rollZ -
      this.dragVelX * 0.28 -
      this.currentLookX * 0.06 +
      this.expressionRollOffset +
      hopRoll;

    this.model.bodyPivot.rotation.x = THREE.MathUtils.lerp(
      this.model.bodyPivot.rotation.x,
      targetRotX,
      damp
    );
    this.model.bodyPivot.rotation.y = THREE.MathUtils.lerp(
      this.model.bodyPivot.rotation.y,
      targetRotY,
      damp
    );
    this.model.bodyPivot.rotation.z = THREE.MathUtils.lerp(
      this.model.bodyPivot.rotation.z,
      targetRotZ,
      damp
    );

    // 7. Eye tracking & expression shaping
    const blinkScaleY = this.blinkAnim.update(dt);
    let exprEyeScaleY = 1.0;
    let exprEyeScaleX = 1.0;

    if (this.currentState === 'happy') {
      exprEyeScaleY = 1.18;
      exprEyeScaleX = 1.08;
    } else if (this.currentState === 'hover' || this.currentState === 'curious') {
      exprEyeScaleY = 1.1;
      exprEyeScaleX = 1.05;
    } else if (this.currentState === 'surprised' || this.currentState === 'notification') {
      exprEyeScaleY = 1.28;
      exprEyeScaleX = 1.15;
    } else if (this.currentState === 'sleepy') {
      exprEyeScaleY = 0.42;
    } else if (this.currentState === 'annoyed') {
      exprEyeScaleY = 0.32;
      exprEyeScaleX = 1.18;
    }

    const eyeOffsetX = this.currentLookX * 0.11;
    const eyeOffsetY = this.currentLookY * 0.09;

    this.model.faceGroup.position.x = THREE.MathUtils.lerp(
      this.model.faceGroup.position.x,
      eyeOffsetX,
      damp
    );
    this.model.faceGroup.position.y = THREE.MathUtils.lerp(
      this.model.faceGroup.position.y,
      0.08 + eyeOffsetY,
      damp
    );

    const targetEyeScaleY = blinkScaleY * exprEyeScaleY;
    this.model.leftEyeMesh.scale.set(exprEyeScaleX, targetEyeScaleY, 1.0);
    this.model.rightEyeMesh.scale.set(exprEyeScaleX, targetEyeScaleY, 1.0);

    // Decay drag velocities smoothly
    this.dragVelX = THREE.MathUtils.lerp(this.dragVelX, 0, damp);
    this.dragVelY = THREE.MathUtils.lerp(this.dragVelY, 0, damp);
  }
}

