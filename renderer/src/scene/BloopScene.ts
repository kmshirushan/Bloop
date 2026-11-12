import * as THREE from 'three';
import { BloopModel } from './BloopModel';
import { Lighting } from './Lighting';
import { ExpressionController } from '../animation/ExpressionController';
import { HopAnimationController } from '../animation/HopAnimationController';
import { MouseController } from '../interaction/MouseController';

/**
 * Manages the transparent Three.js WebGLRenderer, camera, resize handling,
 * 60 FPS requestAnimationFrame loop, and graceful WebGL context loss recovery.
 */
export class BloopScene {
  private readonly scene = new THREE.Scene();
  private readonly camera: THREE.PerspectiveCamera;
  private readonly renderer: THREE.WebGLRenderer;
  private readonly model: BloopModel;
  private readonly lighting: Lighting;
  private readonly expressions: ExpressionController;
  private readonly hopController: HopAnimationController;
  private readonly mouseController: MouseController;

  private rafId: number | null = null;
  private lastFrameTime = 0;
  private isContextLost = false;
  private badgeTimeoutId: number | null = null;

  private readonly onResizeBound = () => this.onResize();
  private readonly onContextLostBound = (e: Event) => this.onContextLost(e);
  private readonly onContextRestoredBound = () => this.onContextRestored();

  constructor(private readonly container: HTMLElement) {
    const width = container.clientWidth || 260;
    const height = container.clientHeight || 260;

    this.camera = new THREE.PerspectiveCamera(34, width / height, 0.1, 25);
    this.camera.position.set(0, 0.05, 4.7);
    this.camera.lookAt(0, -0.05, 0);

    this.renderer = new THREE.WebGLRenderer({
      alpha: true,
      antialias: true,
      powerPreference: 'low-power',
      premultipliedAlpha: true
    });
    this.renderer.setClearColor(0x000000, 0); // 100% transparent clear color
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(width, height);
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    this.renderer.toneMappingExposure = 1.12;

    this.container.appendChild(this.renderer.domElement);

    this.lighting = new Lighting(this.scene, this.renderer);
    this.model = new BloopModel();
    this.scene.add(this.model.root);

    this.expressions = new ExpressionController(this.model);
    this.hopController = new HopAnimationController(this.model);
    this.mouseController = new MouseController(
      this.renderer.domElement,
      this.camera,
      this.model,
      this.expressions,
      this.hopController,
      (label) => this.showStatusBadge(label)
    );

    window.addEventListener('resize', this.onResizeBound);
    this.renderer.domElement.addEventListener('webglcontextlost', this.onContextLostBound, false);
    this.renderer.domElement.addEventListener(
      'webglcontextrestored',
      this.onContextRestoredBound,
      false
    );
  }

  private showStatusBadge(label: string): void {
    const badge = document.getElementById('status-badge');
    if (!badge) return;
    badge.textContent = label;
    badge.classList.add('visible');
    if (this.badgeTimeoutId !== null) {
      window.clearTimeout(this.badgeTimeoutId);
    }
    this.badgeTimeoutId = window.setTimeout(() => {
      badge.classList.remove('visible');
    }, 1100);
  }

  public start(): void {
    if (this.rafId !== null) return;
    this.lastFrameTime = performance.now();
    const tick = (now: number) => {
      this.rafId = requestAnimationFrame(tick);
      if (this.isContextLost || document.hidden) return;

      const dt = (now - this.lastFrameTime) * 0.001;
      this.lastFrameTime = now;

      const hopMod = this.hopController.update(dt);
      this.expressions.update(dt, hopMod);
      this.renderer.render(this.scene, this.camera);
    };
    this.rafId = requestAnimationFrame(tick);
  }

  private onResize(): void {
    const width = this.container.clientWidth || 260;
    const height = this.container.clientHeight || 260;
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(width, height);
  }

  private onContextLost(event: Event): void {
    event.preventDefault();
    this.isContextLost = true;
    this.mouseController.postToHost('renderer:context_status', { lost: true });
  }

  private onContextRestored(): void {
    this.isContextLost = false;
    this.lastFrameTime = performance.now();
    this.mouseController.postToHost('renderer:context_status', { lost: false });
  }

  public dispose(): void {
    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId);
      this.rafId = null;
    }
    window.removeEventListener('resize', this.onResizeBound);
    this.renderer.domElement.removeEventListener('webglcontextlost', this.onContextLostBound);
    this.renderer.domElement.removeEventListener(
      'webglcontextrestored',
      this.onContextRestoredBound
    );
    this.mouseController.dispose();
    this.lighting.dispose();
    this.model.dispose();
    this.renderer.dispose();
  }
}

