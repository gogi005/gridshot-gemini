// ============================================================================
//  Trainer.Game :: Scripts :: Player :: FpsController.cs
//  Godot presentation/input layer ONLY. Converts RAW mouse counts into world
//  rotation using exact Valorant math (0.07 deg/count) and forwards trigger
//  pulls to the headless GridshotSim. No gameplay rules live here.
//
//  Coordinate contract with Trainer.Sim:
//      The Sim works in System.Numerics space with +Z as "forward".
//      Godot cameras look down their local -Z. To keep BOTH engines honest we
//      place the grid at +Z in Godot space as well and derive the shot ray from
//      Basis * -Vector3.Forward, then hand the Sim a numerically identical
//      Vector3 (Godot 4's Vector3 IS System.Numerics.Vector3 in .NET bindings).
//      Therefore no axis flipping is required anywhere - one convention, zero
//      conversion bugs.
// ============================================================================

using Godot;
using Trainer.Sim.Math;
using Trainer.Sim.Scenarios;

namespace Trainer.Game.Scripts.Player;

/// <summary>
/// FPS camera driven by raw, unaccelerated mouse input. Attach to a Camera3D
/// that is the child of the player root. GameLoop wires <see cref="Simulation"/>.
/// </summary>
public partial class FpsController : Camera3D
{
    /// <summary>Valorant-style sensitivity multiplier applied on top of 0.07 deg/count.</summary>
    [Export] public double Sensitivity = 1.0;

    /// <summary>Pitch clamp in degrees (90 would allow gimbal flip / over-under).</summary>
    [Export] public double PitchLimitDegrees = 89.0;

    /// <summary>Injected by GameLoop. The controller never constructs game logic.</summary>
    public GridshotSim Simulation;

    // Euler state kept in DEGREES - Valorant's native unit - so the 0.07
    // constant applies without any hidden radian round-trips.
    private double _yawDeg;
    private double _pitchDeg;

    public override void _Ready()
    {
        // Captured mode delivers pure motion DELTAS through InputEventMouseMotion
        // and bypasses OS pointer acceleration. Mandatory for an aim trainer:
        // any OS smoothing corrupts the 0.07 deg/count fidelity contract.
        Input.MouseMode = Input.MouseModeEnum.Captured;

        // Turn OFF Godot's per-frame input accumulation so every physical mouse
        // count arrives as its own event, exactly once, un-coalesced. This is
        // what makes "deltaX * 0.07 * sens" equal one hardware tick per click.
        UseAccumulatedInput = false;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion motion when Input.MouseMode == Input.MouseModeEnum.Captured:
                HandleMouseLook(motion.Relative);
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, Echo: false }:
                HandleClick();
                break;
        }
    }

    /// <summary>
    /// Exact Valorant rotation math:
    ///     yawDelta   = deltaX * 0.07 * sens   [degrees]
    ///     pitchDelta = deltaY * 0.07 * sens   [degrees]
    /// Screen Y grows downward, so pitch is subtracted (mouse down => look down).
    /// </summary>
    private void HandleMouseLook(Vector2 relative)
    {
        const float DegreesPerCount = Sensitivity_Constants.DegreesPerMouseCount;

        _yawDeg += relative.X * DegreesPerCount * Sensitivity;
        _pitchDeg -= relative.Y * DegreesPerCount * Sensitivity;

        // Wrap yaw into (-180, 180]; clamp pitch away from the poles.
        _yawDeg %= 360.0;
        if (_yawDeg > 180.0) _yawDeg -= 360.0;
        else if (_yawDeg < -180.0) _yawDeg += 360.0;

        double limit = PitchLimitDegrees;
        if (_pitchDeg > limit) _pitchDeg = limit;
        else if (_pitchDeg < -limit) _pitchDeg = -limit;
    }

    /// <summary>
    /// Semi-auto trigger model (press edge only, release ignored) - matches how
    /// Gridshot scoring behaves in commercial trainers. Builds the shot ray from
    /// THIS camera's global transform and hands it to the Sim. Zero allocation:
    /// two value-type Vector3s passed by value.
    /// </summary>
    private void HandleClick()
    {
        if (Simulation == null || Simulation.IsFinished) return;

        // Godot's camera looks along -Z of its basis. Our Sim shares Godot's
        // world axes, so the view direction is simply Basis * (0,0,-1).
        Vector3 origin = GlobalPosition;
        Vector3 forward = -Basis.Z; // equals Basis * Vector3.Forward, no temp alloc chain

        Simulation.ProcessClick(ToNumerics(origin), ToNumerics(forward));
    }

    public override void _Process(double delta)
    {
        // Push euler state into the engine transform. Rotation is radians, so
        // convert ONCE per frame at the presentation boundary only.
        Rotation = new Vector3(
            (float)(_pitchDeg * Mathf.DegToRad),
            (float)(_yawDeg * Mathf.DegToRad),
            0f);
    }

    /// <summary>
    /// Godot 4 .NET already aliases Vector3 to System.Numerics.Vector3; this
    /// explicit helper documents the boundary and stays valid if the binding
    /// ever changes representation. Compiles to a register move - no cost.
    /// </summary>
    private static System.Numerics.Vector3 ToNumerics(Vector3 v)
        => new(v.X, v.Y, v.Z);

    /// <summary>Public accessor used by GameLoop/HUD to display current aim angles.</summary>
    public (double Yaw, double Pitch) CurrentAnglesDegrees => (_yawDeg, _pitchDeg);
}

/// <summary>
/// Local alias so the hot path reads "0.07" literally, straight from the Sim's
/// single source of truth (<see cref="Trainer.Sim.Math.Sensitivity"/>).
/// </summary>
internal static class Sensitivity_Constants
{
    public const float DegreesPerMouseCount = Trainer.Sim.Math.Sensitivity.DegreesPerMouseCount;
}
