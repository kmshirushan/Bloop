# Bloop — Development & Verification Guide (Milestone 1)

## 1. Prerequisites

- **OS**: Windows 10 (1809+) or Windows 11 with **Microsoft Edge WebView2 Runtime** installed.
- **.NET SDK**: .NET 10 SDK (`10.0.100` or newer; tested with `10.0.404`).
- **Node.js**: Node.js `20+` (tested with `v24.18.0`) and `npm`.

---

## 2. Build & Run Instructions

### Step A — Build the Three.js 3D Renderer
```powershell
cd renderer
npm install
npm run build
```
This compiles TypeScript strictly (`tsc`) and bundles the procedural 3D character into `renderer/dist/`.

### Step B — Build & Test the .NET 10 Solution
```powershell
cd ..
dotnet build Bloop.sln -c Release
dotnet test Bloop.sln -c Release
```

### Step C — Run the Desktop Companion
```powershell
dotnet run --project src/Bloop.Desktop/Bloop.Desktop.csproj -c Release
```
> During build, `Bloop.Desktop.csproj` automatically copies `renderer/dist/**` into `bin/<Config>/net10.0-windows/wwwroot/` and maps it to `https://bloop.app/index.html` via `SetVirtualHostNameToFolderMapping`.

---

## 3. Milestone 1 Verification & Test Checklist

| # | Requirement | Verification Procedure | Status |
|---|---|---|---|
| 1 | **Transparent Feasibility (Step 1)** | Launch `Bloop.Desktop`. Confirm desktop wallpaper/icons are visible directly around the spherical mascot with no window chrome or opaque rectangle. | Verified |
| 2 | **No Focus Stealing** | Open Notepad or an IDE and start typing. Click and drag Bloop while typing; verify the active editor retains keyboard focus (`WS_EX_NOACTIVATE` + `MA_NOACTIVATE`). | Verified |
| 3 | **3D Procedural Mascot (Step 2)** | Inspect Bloop: electric-blue glossy sphere (`MeshPhysicalMaterial` + clearcoat + vertical color gradient), two white capsule eyes with specular glints, and soft contact shadow underneath. | Verified |
| 4 | **Idle Floating & Blinking (Step 3)** | Leave cursor still; observe smooth sinusoidal vertical hovering, subtle breathing volume preservation, and randomized natural single/double blinks. | Verified |
| 5 | **Global Cursor Eye Tracking (Step 3 & 4)** | Move mouse anywhere across the desktop and hover directly over Bloop; verify eyes and head tilt smoothly toward the cursor via damped spring interpolation. | Verified |
| 6 | **Single-Click Happy Bounce (Step 3 & 4)** | Left-click Bloop once; verify immediate squash-and-stretch launch, upward bounce, blink, and smooth spring settle back to idle. | Verified |
| 7 | **Drag & Elastic Settle (Step 4)** | Drag Bloop across the screen; verify velocity-based tilt/stretch during drag and elastic bounce settle upon release, plus SQLite persistence of the new position. | Verified |
| 8 | **Double-Click Demo Panel & Expression Buttons (M1 & M2)** | Double-click Bloop to open the Companion Panel. Click `Happy Bounce`, `Surprised`, `Curious`, and `Sleepy`; verify distinct animations (bounce, startle widen, head tilt + side gaze, drooping nod + half-closed eyes) and live `renderer:animation_ack` status confirmation. | Verified |
| 9 | **4-Edge Peek Docking & Animated Hopping (M2)** | Drag Bloop within 35 DIP of Left, Right, Top, or Bottom screen work-area edges; verify edge glow preview, release to trigger 4-phase animated hop (`Anticipation` → `Takeoff` → `Flight` → `Landing`), and ~60% visible docked peek inside work-area bounds. | Verified |
| 10 | **Docked Approach Peek & Click Pop-Out Undock (M2)** | Move cursor near docked Bloop to observe subtle peek-out and eye tracking; single-click docked Bloop to trigger pop-out undock hop back to a free position. | Verified |
| 11 | **Autonomous Edge Hopping & Live Settings (M2 & M3)** | Open Settings or Companion Panel; click `Dev: Trigger Autonomous Hop Now` or wait for scheduled timer; verify 60 Hz `DispatcherTimer` + `Stopwatch` hop flight, diagnostic log entries (`SchedulerInitialized`, `EligibilityChecked`, `TargetSelected`, `MovementDispatched`, `MovementCompleted`), and SQLite persistence. | Verified |
| 12 | **Behaviour Engine: Weighted Clicks, Progressive Annoyance, Peekaboo & Window Exploration (M3)** | Single-click for deduplicated weighted reactions; click rapidly (3–5+ times within 3.5s) for progressive `Mild` → `Annoyed` head-shake reaction; double-click to open Companion Panel without triggering annoyance; test `Peekaboo` while docked and foreground window-boundary perching. | Verified |
| 13 | **Performance & Resource Cleanup (Step 5)** | Confirm zero per-frame object allocations in `ExpressionController.update` and `HopAnimationController.update`, `document.hidden` frame skipping, `webglcontextlost` recovery handler, and clean disposal of `WebView2` + tray icon on exit. | Verified |

