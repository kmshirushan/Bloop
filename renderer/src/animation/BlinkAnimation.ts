/**
 * Randomized natural blinking controller supporting occasional double-blinks.
 */
export class BlinkAnimation {
  private timeUntilNextBlink = 2.4;
  private blinkProgress = -1; // -1 when not blinking, [0..1] during blink
  private readonly blinkDuration = 0.15;
  private pendingDoubleBlink = false;

  public triggerImmediateBlink(): void {
    if (this.blinkProgress < 0) {
      this.blinkProgress = 0;
    }
  }

  /**
   * Returns vertical eye scale multiplier in [0.06 .. 1.0].
   */
  public update(dt: number): number {
    if (this.blinkProgress >= 0) {
      this.blinkProgress += dt / this.blinkDuration;
      if (this.blinkProgress >= 1.0) {
        this.blinkProgress = -1;
        if (this.pendingDoubleBlink) {
          this.pendingDoubleBlink = false;
          this.timeUntilNextBlink = 0.12;
        } else {
          // Next random blink between 2.2s and 5.6s
          this.timeUntilNextBlink = 2.2 + Math.random() * 3.4;
          this.pendingDoubleBlink = Math.random() < 0.22;
        }
        return 1.0;
      }

      // Smooth close and open curve: sin(pi * t)
      const closure = Math.sin(this.blinkProgress * Math.PI);
      return Math.max(0.06, 1.0 - closure * 0.94);
    }

    this.timeUntilNextBlink -= dt;
    if (this.timeUntilNextBlink <= 0) {
      this.blinkProgress = 0;
    }

    return 1.0;
  }
}

