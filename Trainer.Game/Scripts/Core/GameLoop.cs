// ============================================================================
//  Trainer.Game :: Scripts :: Core :: GameLoop.cs
//  The ONLY bridge between the Godot scene tree and Trainer.Sim. Responsibilities:
//    * construct GridshotSim once (all heap work happens here, at _Ready),
//    * bind exactly 3 pre-existing MeshInstance3D nodes to the Sim's 3-slot
//      Vector3 array,
//    * push sim positions into mesh transforms every frame (pure struct copies),
//    * drive the session timer + minimal HUD text.
//  It contains NO gameplay rules and NO hit math - those live in Trainer.Sim.
// ============================================================================

using Godot;
using Trainer.Game.Scripts.Player;
using Trainer.Sim.Math;
using Trainer.Sim.Scenarios;

namespace Trainer.Game.Scripts.Core;

/// <summary>
/// Scene wiring contract (set these up in the editor, then this script does
/// the rest with zero runtime node creation):
///   GameLoop (this Node)
///     +- PlayerRoot (Node3D)
///     !    +- FpsController (Camera3D)          -> exported as Camera
///     +- Targets (Node3D)
///          +- Target0 (MeshInstance3D, SphereMesh radius = Sim.TargetRadius)
///          +- Target1 (MeshInstance3D)
///          +- Target2 (MeshInstance3D)
///   HudScore (Label)                            -> optional
/// </summary>
public partial class GameLoop : Node
{
    /// <summary>The three visual targets. Exactly 3 nodes exist for all of time.</summary>
    [Export] public MeshInstance3D[] Targets = new MeshInstance3D[3];

    /// <summary>Player camera that produces shot rays.</summary>
    [Export] public FpsController Camera;

    /// <summary>Optional score readout.</summary>
    [Export] public Label HudScore;

    /// <summary>Seconds before an auto-restart after the session completes.</summary>
    [Export] public double RestartDelaySeconds = 1.5;

    /// <summary>Session seed. Change per run or leave fixed for reproducible drills.</summary>
    [Export] public uint Seed = 0x5EEDB1Du;

    /// <summary>Headless game logic. Created ONCE in _Ready - never re-allocated.</summary>
    public GridshotSim Simulation { get; private set; }

    // Cached so the frame loop touches no properties/exports and allocates nothing.
    private readonly Vector3[] _lastSyncedPositions = new Vector3[3];
    private double _finishCooldown;
    private string _hudCache = string.Empty;   // avoids redundant Label text sets

    public override void _Ready()
    {
        GD.PrintAssert(Targets != null && Targets.Length == 3,
            "GameLoop: exactly 3 MeshInstance3D targets must be assigned.");
        GD.PrintAssert(Camera != null,
            "GameLoop: Camera (FpsController) must be assigned.");

        // ---- THE single allocation point of the whole application ----
        GridshotConfig cfg = GridshotConfig.Default;
        cfg.Seed = Seed;
        Simulation = new GridshotSim(cfg);

        // Hand the rules object to the input layer. Presentation never decides
        // what a "hit" is; it only asks the Sim to ProcessClick.
        Camera.Simulation = Simulation;

        // Match rendered sphere size to the analytical hit volume so WYSIWYG
        // holds by CONSTRUCTION, not by hoping the artist got the mesh right.
        SyncMeshRadius(Simulation.TargetRadius);

        // Force a full first sync (NaN never compares equal to any real value).
        for (int i = 0; i < Targets.Length; i++)
            _lastSyncedPositions[i] = new Vector3(float.NaN, float.NaN, float.NaN);

        if (HudScore != null)
            HudScore.Text = FormatHud();
    }

    public override void _Process(double delta)
    {
        if (Simulation == null) return;

        float dt = (float)delta;
        Simulation.Tick(dt);

        // ---- SYNC: Sim Vector3 array -> MeshInstance3D transforms ----
        // Respawn inside the Sim is just an array write; here we detect the
        // change and copy the position into the existing node. No Instantiate,
        // no QueueFree, no `new` keyword below this line except value structs.
        System.Numerics.Vector3[] simPositions = Simulation.TargetPositions;
        int count = simPositions.Length < Targets.Length ? simPositions.Length : Targets.Length;

        for (int i = 0; i < count; i++)
        {
            ref Vector3 last = ref _lastSyncedPositions[i];
            System.Numerics.Vector3 p = simPositions[i];

            // Cheap dirty check: skip the engine transform write when the
            // target did not move (the common case between hits).
            if (p.X != last.X || p.Y != last.Y || p.Z != last.Z)
            {
                last = p;
                Targets[i].GlobalPosition = new Vector3(p.X, p.Y, p.Z);
            }
        }

        // ---- HUD: rebuild the string only when a number actually changed ----
        if (HudScore != null)
        {
            string text = FormatHud();
            if (text != _hudCache)
            {
                _hudCache = text;
                HudScore.Text = text;
            }
        }

        // ---- Session end handling (presentation concern: restart timing) ----
        if (Simulation.IsFinished)
        {
            _finishCooldown += delta;
            if (_finishCooldown >= RestartDelaySeconds)
            {
                _finishCooldown = 0.0;
                Simulation.Reset(Seed ^ (uint)(Godot.OS.GetTicksMsec() & 0xFFFFFFFFu));
            }
        }
        else
        {
            _finishCooldown = 0.0;
        }
    }

    /// <summary>Manual restart hook (bind a button/action to this if desired).</summary>
    public void RestartSession(uint? newSeed = null)
    {
        Simulation?.Reset(newSeed ?? Seed);
        _finishCooldown = 0.0;
    }

    // ------------------------------------------------------------------ //
    //  Internals
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Replaces each mesh's radius so the drawn sphere equals the analytical
    /// hit sphere. Done once at startup; meshes are shared resources, so we
    /// mutate their cached params instead of creating new geometry per target.
    /// </summary>
    private void SyncMeshRadius(float simRadius)
    {
        foreach (MeshInstance3D t in Targets)
        {
            if (t == null) continue;
            Mesh m = t.Mesh;
            if (m is not SphereMesh sphere) continue;

            // Godot SphereMesh is diameter-based; our sim is radius-based.
            float d = simRadius * 2f;
            if (!Mathf.IsEqualApprox((float)sphere.Radius, simRadius))
            {
                sphere.Radius = simRadius;
                sphere.Height = d;
            }
        }
    }

    /// <summary>
    /// Builds the HUD line. Called at most once per frame and its result is
    /// compared against a cache, so Label.Set happens only on real changes.
    /// </summary>
    private string FormatHud()
    {
        GridshotSim s = Simulation;
        // %0.0 accuracy, integer counts, one-decimal clock - stable widths.
        return $"HITS {s.Hits}   MISS {s.Misses}   ACC {s.Accuracy * 100f:0}%   " +
               $"TIME {s.RemainingSeconds:0.0}s";
    }

    /// <summary>
    /// Editor-time sanity helper exposed to the debugger: verifies the vertical
    /// FOV that maps Valorant's 103-degree horizontal FOV onto a 16:9 viewport.
    /// Call from a console/debug overlay; not part of the frame loop.
    /// </summary>
    public static double RecommendedVerticalFovDegrees(double aspectX = 16.0, double aspectY = 9.0)
    {
        float vRad = Sensitivity.VerticalFovRadiansFromHorizontal(
            Sensitivity.HorizontalFovDegrees, (float)(aspectX / aspectY));
        return vRad * Mathf.RadToDeg;
    }
}
