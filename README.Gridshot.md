# Gridshot V1 — Valorant-Style Aim Trainer (Godot 4 .NET + pure C# Sim)

Two-assembly architecture with a strict dependency direction:

```
Trainer.Game (Godot: rendering / raw input / sync)  -->  Trainer.Sim (pure C#: rules / math)
                            never the reverse
```

## Files
| File | Role |
|---|---|
| `Trainer.Sim/Math/RaySphere.cs` | Analytical ray-sphere intersection, finite rays, AABB rejection pass, exact Valorant constants (`Sensitivity`: 0.07°/count, 103° H-FOV → vertical FOV converter). |
| `Trainer.Sim/Core/XorShift32.cs` | Deterministic struct PRNG (seeded, zero-allocation spawning). |
| `Trainer.Sim/Scenarios/GridshotSim.cs` | 4×3 grid nodes baked once, occupancy table, exactly **3** pre-allocated target slots, Hits/Misses/Accuracy/timer, `ProcessClick()` = hit-test + instant respawn. |
| `Trainer.Game/Scripts/Player/FpsController.cs` | `MouseMode.Captured`, `UseAccumulatedInput = false`, `yaw += deltaX * 0.07 * sens`, click → shot ray from camera basis → `GridshotSim.ProcessClick`. |
| `Trainer.Game/Scripts/Core/GameLoop.cs` | Constructs the Sim once, binds 3 existing `MeshInstance3D`s to `Simulation.TargetPositions`, dirty-checked per-frame transform sync, HUD, auto-restart. |

## Zero-allocation contract
* All heap work happens in `_Ready`/constructors only.
* Respawn = one `Vector3` struct write into the shared array; the renderer copies it into an existing node's transform. No `Instantiate`, no `QueueFree`, no LINQ, no closures, no boxing in the hot path.
* Verified headlessly: **200,000 clicks + respawns allocated ~304 bytes total** (the harness's own `Random`), i.e. effectively zero from the Sim.

## Scene setup (editor)
```
GameLoop (Node, GameLoop.cs)
├── PlayerRoot (Node3D @ y=1.6)
│   └── Camera (Camera3D, FpsController.cs)     -> export "Camera"
│       • set its Fov to 70.53 (103° horizontal @ 16:9; helper:
│         GameLoop.RecommendedVerticalFovDegrees())
├── Targets (Node3D)
│   ├── Target0 (MeshInstance3D + SphereMesh)   \
│   ├── Target1 (MeshInstance3D + SphereMesh)    > export all three as "Targets"
│   └── Target2 (MeshInstance3D + SphereMesh)   /
└── HudScore (Label)                             -> export "HudScore" (optional)
```
Radius is pushed from the Sim into the meshes at startup, so the drawn sphere
always equals the analytical hit volume.

## Build notes
* `Trainer.Game.csproj` uses `Godot.NET.Sdk/4.3.0`; bump the suffix to your
  engine line (e.g. `4.2.0`) if needed. `UseAccumulatedInput` requires Godot ≥ 4.2.
* Place this repo root as the Godot project folder (add a `project.godot` via
  the editor) or copy the two folders into an existing Godot .NET project.
