/**
 * Delta-time-based sinusoidal floating and breathing animation.
 */
export class IdleAnimation {
  private elapsed = 0;

  public update(dt: number): {
    offsetY: number;
    rollZ: number;
    breatheScaleX: number;
    breatheScaleY: number;
  } {
    this.elapsed += dt;

    // Gentle vertical hover (~0.45 Hz)
    const offsetY = Math.sin(this.elapsed * 2.6) * 0.085;
    // Subtle secondary sway
    const rollZ = Math.sin(this.elapsed * 1.3) * 0.025;
    // Micro breathing volume preservation
    const breathe = Math.sin(this.elapsed * 2.6 + Math.PI * 0.5) * 0.015;

    return {
      offsetY,
      rollZ,
      breatheScaleX: 1.0 - breathe * 0.5,
      breatheScaleY: 1.0 + breathe
    };
  }
}

