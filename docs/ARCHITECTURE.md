# Bloop — Architecture Documentation

## 1. Overview & Module Responsibilities

Bloop is separated into two isolated runtime contexts—the **Native Windows Host** (.NET 10 / WPF) and the **3D Renderer** (TypeScript / Three.js / Vite)—backed by modular domain, OS-integration, and storage libraries.

```
Bloop/
├── src/
│   ├── Bloop.Desktop/      # Native WPF Window Host, Win32 interop, System Tray, WebView2 Bridge
│   ├── Bloop.Core/         # Pure domain models, IPC message contracts, Personality state machine
│   ├── Bloop.Windows/      # OS integration (UserNotificationListener, System power/session events)
│   └── Bloop.Storage/      # Local SQLite persistence for settings and companion state
├── renderer/               # Three.js / TypeScript / Vite 3D mascot renderer
├── tests/                  # Unit tests for Core and Windows modules
└── docs/                   # Architecture & Development documentation
```

### Module Breakdown

1. **`Bloop.Core`**
   - **Responsibilities**:
     - `PersonalityEngine` & `MascotState`: Mood/expression state machine (`Idle`, `Hover`, `Happy`, `Curious`, `Surprised`, `Sleepy`, `Dragging`, `Settling`, `Notification`) with priority rules and repeatable command dispatch.
     - `MovementController`, `MovementState`, `DockingEdge`, `MovementSettings`, `DockingPosition`: Platform-independent physical movement & edge-docking subsystem (`Free`, `Dragging`, `PreparingToHop`, `Hopping`, `Docked`, `Undocking`, `Paused`), 4-edge work-area proximity detection, 60% peek-docking viewport offset math, and autonomous edge-hopping scheduler with cooldown and interruption guards.
     - `IpcMessage<T>`: Strongly-typed JSON IPC message envelopes shared with the TypeScript renderer.
   - **Dependencies**: None (pure .NET 10 class library).

2. **`Bloop.Desktop`**
   - **Responsibilities**:
     - `MascotWindow`: Borderless floating window (`WindowStyle="None"`, `Topmost="True"`), Win32 `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` extended styles so clicking/dragging the mascot never steals keyboard focus from the user's active application.
     - `WebViewService`: Manages `CompositionController`-backed `WebView2` (`WebView2CompositionControl`) with `DefaultBackgroundColor = Color.Transparent`, virtual host mapping (`https://bloop.app/`), and bidirectional JSON message dispatch.
     - `WindowService`: Multi-monitor Per-Monitor V2 DPI awareness, screen-bound clamping, smooth window repositioning, and system tray (`NotifyIcon`) lifecycle.
     - `NotificationWindow` & `SettingsWindow`: Expandable floating notification/demo panel and settings view.

3. **`renderer` (Three.js + TypeScript + Vite)**
   - **Responsibilities**:
     - `BloopScene`: Transparent WebGLRenderer (`alpha: true`, `premultipliedAlpha: true`), perspective camera, render loop with delta-time clamping (`Math.min(dt, 0.1)`), and graceful `webglcontextlost` / `webglcontextrestored` handling.
     - `BloopModel`: Procedural 3D electric-blue glossy spherical character (`MeshPhysicalMaterial` with clearcoat, subtle vertical gradient shader/emissive rim, and two expressive white capsule/ellipsoid eyes with pupils/highlights). Zero allocations inside the animation loop.
     - `Lighting`: Soft three-point studio lighting (key, cool rim, warm fill) + procedural environment reflection for crisp specular highlights on the glossy sphere.
     - `IdleAnimation`, `BlinkAnimation`, `ExpressionController`: Interruptible, damped-spring (`lerp` / exponential decay) procedural animations: sine-wave floating, randomized natural double/single blinks, squash-and-stretch, happy bounce, drag velocity deformation, and smooth return to idle.
     - `MouseController`: Raycasting against the mascot sphere, normalized cursor tracking for eye look-at and body tilt, click/double-click detection, and pointer-capture window drag deltas posted over the JSON IPC protocol.

4. **`Bloop.Windows`**
   - **Responsibilities**: Encapsulates Windows Runtime (`Windows.UI.Notifications.Management.UserNotificationListener`) and Win32 display/session queries behind clean interfaces (`INotificationService`). (Scoped as clean contract + stubbed/ready boundary in Milestone 1 per milestone instructions).

5. **`Bloop.Storage`**
   - **Responsibilities**: Encapsulates SQLite local storage (`SettingsRepository`, `DatabaseService`) for persisting mascot window coordinates, monitor affinity, and user preferences across restarts.

---

## 2. Typed JSON IPC Protocol

Communication between `Bloop.Desktop` (C#) and `renderer` (TypeScript) uses `window.chrome.webview.postMessage` and `CoreWebView2.PostWebMessageAsJson`. Every message follows a small discriminated union envelope:

```json
{
  "type": "pointer:drag_move",
  "timestamp": 1762934500123,
  "payload": { "screenDeltaX": 4.0, "screenDeltaY": -2.0 }
}
```

### Renderer → Host (`RendererToHostMessage`)
| `type` | Payload | Description |
|---|---|---|
| `renderer:ready` | `{ version: string }` | Sent once WebGL scene is initialized. |
| `pointer:drag_start` | `{ screenX: number, screenY: number }` | User pressed pointer on mascot and exceeded drag threshold. |
| `pointer:drag_move` | `{ screenX: number, screenY: number, velocityX: number, velocityY: number }` | Continuous screen-space drag update. |
| `pointer:drag_end` | `{ screenX: number, screenY: number }` | User released pointer after dragging; host persists position. |
| `interaction:click` | `{ state: string }` | Single click triggered happy bounce. |
| `interaction:double_click` | `{}` | Double click toggles the demonstration / companion panel. |
| `renderer:animation_ack` | `{ state: string, commandId: string }` | Explicit acknowledgement emitted when the renderer activates a host expression command. |
| `renderer:context_status` | `{ lost: boolean }` | Reports WebGL context loss/restoration. |

### Host → Renderer (`HostToRendererMessage`)
| `type` | Payload | Description |
|---|---|---|
| `host:state_change` | `{ state: string, priority: number, commandId?: string }` | Commands `ExpressionController` to transition state (`idle`, `happy`, `curious`, `surprised`, `sleepy`). |
| `host:cursor_position` | `{ normX: number, normY: number, isInsideWindow: boolean }` | Global cursor normalized relative to mascot center (`[-1, 1]`) so eyes track cursor and docked peek responds to proximity. |
| `host:dock_preview` | `{ edge: string }` | Toggles the subtle edge-proximity indicator bar (`left`, `right`, `top`, `bottom`, `none`) during drag. |
| `host:hop_start` | `{ edge, targetOffsetX, targetOffsetY, directionX, directionY, anticipationMs, flightMs, landingMs }` | Starts the 4-stage hop sequence (`Anticipation` → `Takeoff` → `Flight` → `Landing`) synchronized with WPF `CompositionTarget.Rendering` window motion. |
| `host:dock_config` | `{ edge, offsetX, offsetY, visibleRatio }` | Synchronizes the docked viewport offset (~60% visible body area) without keeping any off-screen window coordinates. |
| `host:window_settled` | `{ x: number, y: number, dpiScale: number }` | Confirms window position and DPI scale. |

---

## 3. Architectural Decisions & Known Windows API Limitations

### 3.1 WPF + WebView2 Transparency (Step 1 Verification Findings)
- **Classic `AllowsTransparency="True"` vs `HwndHost` Conflict**: Setting `AllowsTransparency="True"` on a WPF `Window` switches the top-level HWND to a legacy `UpdateLayeredWindow` (`WS_EX_LAYERED`) software composition path, which clips or hides child `HwndHost` windows such as `Microsoft.Web.WebView2.Wpf.WebView2` (`WebView2CompositionControl` is `internal` in the NuGet assembly).
- **Verified Solution Used**:
  1. Keep `AllowsTransparency="False"` on `MascotWindow` (`WindowStyle="None"`, `Background="Transparent"`), set `WindowChrome` with `GlassFrameThickness="-1"` and `ResizeBorderThickness="0"`, set `HwndSource.CompositionTarget.BackgroundColor = Colors.Transparent`, and call `DwmExtendFrameIntoClientArea` (`margins = -1`) + `DwmEnableBlurBehindWindow` with an empty region and `WM_NCCALCSIZE` returning `0`.
  2. Set `WebView2.DefaultBackgroundColor = System.Drawing.Color.Transparent` (alpha = 0) before and after `EnsureCoreWebView2Async`, and initialize `THREE.WebGLRenderer({ alpha: true, antialias: true, powerPreference: 'low-power' })` with `renderer.setClearColor(0x000000, 0)`.
  3. This enables hardware-accelerated DirectComposition per-pixel alpha blending through `WebView2` directly onto the Windows desktop without window borders.
- **Known Limitation — Click-Through Regions**:
  - Within the 260×260 DIP bounding box of `MascotWindow`, transparent corners outside the spherical mascot still belong to the WebView2 surface.
  - To prevent the invisible corners of the 260×260 window from blocking clicks on desktop icons behind the corners, `MascotWindow` dynamically updates a circular Win32 region / hit-test or toggles `WS_EX_TRANSPARENT` when the global cursor is outside the mascot's spherical radius (`r = 95 DIP`), giving pixel-accurate click-through around the sphere while preserving smooth WebGL glow and shadow falloff!

### 3.2 Preventing Focus Stealing (`WS_EX_NOACTIVATE`)
- A desktop companion must never deactivate the user's active IDE/browser window when clicked or dragged.
- `MascotWindow` applies `WS_EX_NOACTIVATE (0x08000000)` and `WS_EX_TOOLWINDOW (0x00000080)` in `OnSourceInitialized` and intercepts `WM_MOUSEACTIVATE` returning `MA_NOACTIVATE (3)`. This keeps Bloop out of `Alt+Tab` and prevents stealing keyboard focus.

### 3.3 Global Eye Tracking Without High CPU Overhead
- Tracking the cursor only via DOM `pointermove` stops working the moment the cursor leaves the 260×260 mascot window.
- Instead of installing a heavy global low-level mouse hook (`WH_MOUSE_LL`), `Bloop.Desktop` polls `GetCursorPos` on a lightweight 33ms (`~30 Hz`) timer only when the mascot is awake and visible, sending `host:cursor_position` only when the cursor actually moves (`delta > 1px`). When the cursor hasn't moved, zero IPC messages are sent.
