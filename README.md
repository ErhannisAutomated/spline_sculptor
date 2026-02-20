# Spline Sculptor

A VR NURBS surface editor built with Godot 4 + C#, targeting HTC Vive via SteamVR/OpenXR.

## Quick Start

### Prerequisites

| Tool | Version |
|---|---|
| Godot 4 (.NET build) | 4.3+ |
| .NET SDK | 8.0 |
| SteamVR | Latest |

### Setup

```bash
# 1. Restore NuGet packages (rhino3dm)
dotnet restore

# 2. Open the project in Godot 4 (.NET build)
#    File → Open Project → select spline_sculptor/

# 3. Build C# solution (Godot: Build button or Ctrl+B)

# 4. Run (F5)
#    - With HMD connected: runs in VR
#    - Without HMD: desktop mode (mouse drag control points, right-drag orbit)
```

### rhino3dm native libs

rhino3dm ships native `.so`/`.dll` files inside the NuGet package.
After a `dotnet restore`, copy them alongside the Godot export binary:

```
# Linux
cp ~/.nuget/packages/rhino3dm/8.9.0/runtimes/linux-x64/native/librhino3dm.so  <export_dir>/

# Windows
copy %USERPROFILE%\.nuget\packages\rhino3dm\8.9.0\runtimes\win-x64\native\rhino3dm.dll  <export_dir>\
```

---

## Project Structure

```
spline_sculptor/
├── project.godot               # Godot project (OpenXR enabled)
├── spline_sculptor.csproj      # C# project (rhino3dm NuGet)
├── openxr_action_map.tres      # Vive + Touch controller bindings
├── src/
│   ├── Math/
│   │   ├── NurbsMath.cs        # Cox-de Boor basis, FindSpan, derivatives
│   │   └── NurbsSurface.cs     # Evaluate, Tessellate, InsertKnot, factories
│   ├── Model/
│   │   ├── SculptSurface.cs    # Data model + GeometryChanged event
│   │   ├── EdgeConstraint.cs   # G0/G1 continuity enforcement
│   │   ├── Polysurface.cs      # Surface group, AttachPatch, Split
│   │   ├── SculptScene.cs      # Root: Polysurfaces + UndoStack
│   │   └── Undo/               # ICommand, UndoStack, all command types
│   ├── Rendering/
│   │   ├── SurfaceNode.cs      # ArrayMesh from tessellation
│   │   ├── ControlNetNode.cs   # Sphere handles + line net
│   │   └── PolysurfaceNode.cs  # Groups surface + net nodes
│   ├── VR/
│   │   ├── VRManager.cs        # OpenXR init, default scene, keyboard undo
│   │   ├── ControllerHand.cs   # Per-hand grab state machine
│   │   ├── WorldNavigator.cs   # Two-hand pan/scale/rotate
│   │   └── DesktopInteraction.cs # Mouse-ray fallback for development
│   ├── Interaction/
│   │   ├── GrabTarget.cs       # IGrabTarget interface
│   │   ├── ControlPointHandle.cs # IGrabTarget for one control point
│   │   └── SelectionManager.cs # Selected surfaces tracking
│   └── IO/
│       └── Rhino3dmIO.cs       # Save/load .3dm (rhino3dm)
├── scenes/
│   ├── Main.tscn               # Root scene
│   ├── VRRig.tscn              # XROrigin3D + controllers
│   └── ControlPoint.tscn       # Sphere + ControlPointHandle
└── assets/materials/           # .tres material resources
```

---

## Controls

### Desktop (development / no HMD)

| Action | Input |
|---|---|
| Drag control point | Left-click + drag |
| Orbit camera | Right-click + drag |
| Zoom | Mouse wheel |
| Undo | Ctrl+Z |
| Redo | Ctrl+Y / Ctrl+Shift+Z |

### VR (HTC Vive)

| Action | Controller |
|---|---|
| Grab control point | Grip button (near handle) |
| Grab world | Grip button (empty space) |
| Two-hand world pan/scale/rotate | Both grips |
| Undo | Menu button |

---

## Architecture

### Data Flow

```
User input (VR grip / mouse drag)
    → ControlPointHandle.OnGrabMove()       [realtime, no undo]
    → SculptSurface.ApplyControlPointMove() [updates geometry]
    → SculptSurface.GeometryChanged event
    → SurfaceNode.RebuildMesh()             [updates ArrayMesh]

On grab release:
    → MoveControlPointCommand pushed to UndoStack
    → Polysurface.EnforceConstraints()      [one post-move pass]
```

### Undo System

All mutations go through `scene.UndoStack.Execute(cmd)`.
During a drag, moves are applied interactively without creating undo entries;
on release, an `AlreadyAppliedCommand` wraps the final position for undo.

### NURBS Math

- `NurbsMath`: Cox-de Boor recursion (Algorithms A2.1, A2.2, A2.3 from *The NURBS Book*)
- `NurbsSurface`: Rational tensor-product evaluation; Boehm knot insertion
- Default surface: degree-3 single-span Bezier patch (4×4 = 16 control points)

---

## Phased Roadmap

| Phase | Status | Goal |
|---|---|---|
| 1 — Foundation | ✅ Implemented | NURBS math, desktop drag, undo/redo |
| 2 — VR | ✅ Implemented | Vive grip interaction, world navigation |
| 3 — Surface Ops | Planned | AttachPatch, EdgeConstraint G1, knot insertion |
| 4 — 3DM I/O | ✅ Implemented | Save/load .3dm via rhino3dm |
| 5 — Polish | Planned | Adaptive tessellation, VR UI, nudge mode |
