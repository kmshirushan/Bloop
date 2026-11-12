import * as THREE from 'three';
import { BloopModel } from '../scene/BloopModel';
import { ExpressionController, MascotStateName } from '../animation/ExpressionController';
import {
  DockingEdgeName,
  HopAnimationController
} from '../animation/HopAnimationController';

export interface IpcEnvelope<T = unknown> {
  type: string;
  timestamp: number;
  payload: T;
}

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: unknown): void;
        addEventListener(
          type: 'message',
          listener: (event: { data: unknown }) => void
        ): void;
      };
    };
  }
}

/**
 * Handles mouse/pointer hover, raycasting against the 3D mascot sphere,
 * single-click (happy bounce), double-click (demo panel), dragging with
 * elastic deformation, and typed JSON communication with the WPF WebView2 host.
 */
export class MouseController {
  private readonly raycaster = new THREE.Raycaster();
  private readonly pointerNdc = new THREE.Vector2();

  private isPointerDown = false;
  private isDragging = false;
  private downScreenX = 0;
  private downScreenY = 0;
  private lastScreenX = 0;
  private lastScreenY = 0;
  private lastMoveTime = 0;
  private clickTimeoutId: number | null = null;

  private readonly onPointerDownBound = (e: PointerEvent) => this.onPointerDown(e);
  private readonly onPointerMoveBound = (e: PointerEvent) => this.onPointerMove(e);
  private readonly onPointerUpBound = (e: PointerEvent) => this.onPointerUp(e);
  private readonly onPointerLeaveBound = () => this.onPointerLeave();
  private readonly onDoubleClickBound = (e: MouseEvent) => this.onDoubleClick(e);

  constructor(
    private readonly domElement: HTMLElement,
    private readonly camera: THREE.Camera,
    private readonly model: BloopModel,
    private readonly expressions: ExpressionController,
    private readonly hopController: HopAnimationController,
    private readonly onStateLabelChange?: (label: string) => void
  ) {
    this.domElement.addEventListener('pointerdown', this.onPointerDownBound);
    window.addEventListener('pointermove', this.onPointerMoveBound);
    window.addEventListener('pointerup', this.onPointerUpBound);
    this.domElement.addEventListener('pointerleave', this.onPointerLeaveBound);
    this.domElement.addEventListener('dblclick', this.onDoubleClickBound);

    this.setupHostMessageListener();
    this.postToHost('renderer:ready', { version: '1.0.0' });
  }

  public postToHost<T>(type: string, payload: T): void {
    const message: IpcEnvelope<T> = {
      type,
      timestamp: Date.now(),
      payload
    };
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(message);
    }
  }

  private setupHostMessageListener(): void {
    window.chrome?.webview?.addEventListener('message', (event) => {
      const raw = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
      const msg = raw as IpcEnvelope<Record<string, unknown>>;
      if (!msg || typeof msg.type !== 'string') return;

      switch (msg.type) {
        case 'host:cursor_position': {
          const normX = Number(msg.payload?.normX ?? 0);
          const normY = Number(msg.payload?.normY ?? 0);
          const inside = Boolean(msg.payload?.isInsideWindow ?? false);
          this.expressions.setCursorTarget(normX, normY);
          this.hopController.updateCursorProximity(normX, normY, inside);
          break;
        }
        case 'host:state_change': {
          const state = String(msg.payload?.state ?? 'idle') as MascotStateName;
          const commandId = msg.payload?.commandId ? String(msg.payload.commandId) : null;
          this.expressions.setHostState(state);
          this.onStateLabelChange?.(state.charAt(0).toUpperCase() + state.slice(1));
          this.postToHost('renderer:animation_ack', {
            state,
            commandId
          });
          break;
        }
        case 'host:dock_preview': {
          const edge = String(msg.payload?.edge ?? 'none').toLowerCase() as DockingEdgeName;
          this.updateEdgeGlow(edge);
          break;
        }
        case 'host:hop_start': {
          const edge = String(msg.payload?.edge ?? 'none').toLowerCase() as DockingEdgeName;
          this.updateEdgeGlow('none');
          this.hopController.startHop({
            edge,
            targetOffsetX: Number(msg.payload?.targetOffsetX ?? 0),
            targetOffsetY: Number(msg.payload?.targetOffsetY ?? 0),
            directionX: Number(msg.payload?.directionX ?? 0),
            directionY: Number(msg.payload?.directionY ?? 0),
            anticipationMs: Number(msg.payload?.anticipationMs ?? 130),
            flightMs: Number(msg.payload?.flightMs ?? 520),
            landingMs: Number(msg.payload?.landingMs ?? 300)
          });
          this.onStateLabelChange?.(edge === 'none' ? 'Hopping' : `Dock ${edge}`);
          break;
        }
        case 'host:dock_config': {
          const edge = String(msg.payload?.edge ?? 'none').toLowerCase() as DockingEdgeName;
          this.hopController.setDockedConfig(
            edge,
            Number(msg.payload?.offsetX ?? 0),
            Number(msg.payload?.offsetY ?? 0)
          );
          break;
        }
      }
    });
  }

  private hitTestMascot(clientX: number, clientY: number): boolean {
    const rect = this.domElement.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return false;

    this.pointerNdc.x = ((clientX - rect.left) / rect.width) * 2 - 1;
    this.pointerNdc.y = -((clientY - rect.top) / rect.height) * 2 + 1;

    this.raycaster.setFromCamera(this.pointerNdc, this.camera);
    const intersects = this.raycaster.intersectObject(this.model.bodyMesh, false);
    return intersects.length > 0;
  }

  private onPointerDown(e: PointerEvent): void {
    if (e.button !== 0) return;
    if (!this.hitTestMascot(e.clientX, e.clientY)) return;

    this.isPointerDown = true;
    this.isDragging = false;
    this.downScreenX = e.screenX;
    this.downScreenY = e.screenY;
    this.lastScreenX = e.screenX;
    this.lastScreenY = e.screenY;
    this.lastMoveTime = performance.now();

    try {
      this.domElement.setPointerCapture(e.pointerId);
    } catch {
      // Ignore if pointer capture is unavailable
    }
  }

  private onPointerMove(e: PointerEvent): void {
    const rect = this.domElement.getBoundingClientRect();
    if (rect.width > 0 && rect.height > 0) {
      const normX = ((e.clientX - rect.left) / rect.width) * 2 - 1;
      const normY = -((e.clientY - rect.top) / rect.height) * 2 + 1;
      this.expressions.setCursorTarget(normX, normY);
    }

    if (!this.isPointerDown) {
      const hit = this.hitTestMascot(e.clientX, e.clientY);
      this.expressions.setHovered(hit);
      return;
    }

    const totalDx = e.screenX - this.downScreenX;
    const totalDy = e.screenY - this.downScreenY;

    if (!this.isDragging && Math.hypot(totalDx, totalDy) > 4) {
      this.isDragging = true;
      this.hopController.clearDockForDrag();
      this.expressions.setDragging(true);
      this.onStateLabelChange?.('Dragging');
      this.postToHost('pointer:drag_start', {
        screenX: this.downScreenX,
        screenY: this.downScreenY
      });
    }

    if (this.isDragging) {
      const now = performance.now();
      const dtMs = Math.max(8, now - this.lastMoveTime);
      const stepDx = e.screenX - this.lastScreenX;
      const stepDy = e.screenY - this.lastScreenY;

      const vx = (stepDx / dtMs) * 0.65;
      const vy = -(stepDy / dtMs) * 0.65;
      this.expressions.setDragVelocity(vx, vy);

      this.postToHost('pointer:drag_move', {
        screenX: e.screenX,
        screenY: e.screenY,
        velocityX: vx,
        velocityY: vy
      });

      this.lastScreenX = e.screenX;
      this.lastScreenY = e.screenY;
      this.lastMoveTime = now;
    }
  }

  private onPointerUp(e: PointerEvent): void {
    if (!this.isPointerDown) return;
    this.isPointerDown = false;

    try {
      if (this.domElement.hasPointerCapture(e.pointerId)) {
        this.domElement.releasePointerCapture(e.pointerId);
      }
    } catch {
      // Ignore
    }

    if (this.isDragging) {
      this.isDragging = false;
      this.expressions.setDragging(false);
      this.onStateLabelChange?.('Settling');
      this.postToHost('pointer:drag_end', {
        screenX: e.screenX,
        screenY: e.screenY
      });
      return;
    }

    // Single click vs double click disambiguation
    if (this.clickTimeoutId !== null) {
      window.clearTimeout(this.clickTimeoutId);
      this.clickTimeoutId = null;
      return;
    }

    this.clickTimeoutId = window.setTimeout(() => {
      this.clickTimeoutId = null;
      this.expressions.triggerHappyBounce();
      this.onStateLabelChange?.('Happy!');
      this.postToHost('interaction:click', { state: 'happy' });
    }, 190);
  }

  private onDoubleClick(e: MouseEvent): void {
    if (!this.hitTestMascot(e.clientX, e.clientY)) return;
    if (this.clickTimeoutId !== null) {
      window.clearTimeout(this.clickTimeoutId);
      this.clickTimeoutId = null;
    }
    this.expressions.triggerHappyBounce();
    this.onStateLabelChange?.('Companion Panel');
    this.postToHost('interaction:double_click', {});
  }

  private updateEdgeGlow(activeEdge: DockingEdgeName): void {
    const edges: DockingEdgeName[] = ['left', 'right', 'top', 'bottom'];
    for (const edge of edges) {
      const el = document.getElementById(`glow-${edge}`);
      if (el) {
        el.classList.toggle('active', edge === activeEdge);
      }
    }
  }

  private onPointerLeave(): void {
    if (!this.isDragging) {
      this.expressions.setHovered(false);
    }
  }

  public dispose(): void {
    this.domElement.removeEventListener('pointerdown', this.onPointerDownBound);
    window.removeEventListener('pointermove', this.onPointerMoveBound);
    window.removeEventListener('pointerup', this.onPointerUpBound);
    this.domElement.removeEventListener('pointerleave', this.onPointerLeaveBound);
    this.domElement.removeEventListener('dblclick', this.onDoubleClickBound);
  }
}

