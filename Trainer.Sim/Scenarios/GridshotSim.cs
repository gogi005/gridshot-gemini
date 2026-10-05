// ============================================================================
//  Trainer.Sim :: Scenarios :: GridshotSim.cs
//  The complete, headless Gridshot rule set. Zero Godot references, zero
//  allocation in the steady state (click / respawn loop).
// ============================================================================

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Trainer.Sim.Core;
using Trainer.Sim.Math;

namespace Trainer.Sim.Scenarios;

/// <summary>Immutable-ish configuration for a Gridshot session (a struct: no heap).</summary>
public struct GridshotConfig
{
    /// <summary>Grid columns / rows on the invisible plane (4x3 by default).</summary>
    public int Columns;
    public int Rows;

    /// <summary>World-space center of the grid plane and its distance from the player.</summary>
    public Vector3 GridCenter;
    public float CellSpacing;

    /// <summary>Target sphere radius used by the analytical hit test.</summary>
    public float TargetRadius;

    /// <summary>How many targets are alive at all times (exactly 3 for Gridshot).</summary>
    public int ActiveTargetCount;

    /// <summary>Max ray length for the click test - must exceed grid distance + radius.</summary>
    public float RayRange;

    /// <summary>Session length in seconds; the sim auto-completes past this time.</summary>
    public float DurationSeconds;

    /// <summary>Deterministic seed so runs can be replayed/verified byte-for-byte.</summary>
    public uint Seed;

    public static GridshotConfig Default => new GridshotConfig
    {
        Columns = 4,
        Rows = 3,
        // Godot/C# world convention here: +Z is forward out of the camera.
        GridCenter = new Vector3(0f, 1.6f, 4f),
        CellSpacing = 0.9f,
        TargetRadius = 0.22f,
        ActiveTargetCount = 3,
        RayRange = 50f,
        DurationSeconds = 30f,
        Seed = 0x5EEDB1Du
    };
}

/// <summary>Outcome of one processed click. Returned by value - never allocated.</summary>
public readonly struct ClickResult
{
    public readonly bool Hit;
    public readonly int SlotIndex;      // Which of the 3 live slots was hit (-1 on miss)
    public readonly int NodeIndex;      // Grid node the destroyed target occupied (-1 on miss)
    public readonly Vector3 HitPoint;   // Exact impact point on the sphere surface

    internal ClickResult(bool hit, int slotIndex, int nodeIndex, Vector3 hitPoint)
    {
        Hit = hit;
        SlotIndex = slotIndex;
        NodeIndex = nodeIndex;
        HitPoint = hitPoint;
    }

    public static ClickResult Miss => new ClickResult(false, -1, -1, Vector3.Zero);
}

/// <summary>
/// Gridshot V1 simulation. Owns:
///   * the baked grid-node position table (pre-allocated, never resized),
///   * an occupancy flag per node,
///   * exactly N pre-allocated target slots whose Vector3 positions the renderer
///     reads directly every frame (sync-by-reference, no Instantiate/QueueFree),
///   * score state (Hits, Misses, Accuracy, Timer).
/// The renderer must treat <see cref="TargetPositions"/> as read-only.
/// </summary>
public sealed class GridshotSim
{
    private readonly GridshotConfig _cfg;
    private readonly int _nodeCount;

    // ---- Pre-allocated world tables (built once in the ctor, mutated in place) ----
    private readonly Vector3[] _nodePositions;   // Baked local->world grid nodes.
    private readonly bool[] _nodeOccupied;       // Occupancy flag per node.
    private readonly int[] _slotToNode;          // Live slot -> node index.

    /// <summary>
    /// THE hot array: exactly ActiveTargetCount spheres. The Godot layer reads
    /// these three Vector3 values each frame and writes them into the transforms
    /// of its three MeshInstance3D nodes. Respawn = overwrite one element here.
    /// </summary>
    public Vector3[] TargetPositions { get; }

    /// <summary>Shared radius for every target (targets are uniform in Gridshot).</summary>
    public float TargetRadius => _cfg.TargetRadius;

    // ---- Deterministic RNG (struct field: lives inside this object, no extra alloc) ----
    private XorShift32 _rng;

    // ---- Score / session state ----
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public int TotalClicks => Hits + Misses;
    public float Accuracy => TotalClicks == 0 ? 1f : (float)Hits / TotalClicks;
    public float ElapsedSeconds { get; private set; }
    public float RemainingSeconds => Max(0f, _cfg.DurationSeconds - ElapsedSeconds);
    public bool IsFinished => ElapsedSeconds >= _cfg.DurationSeconds;

    // Cached AABB that encloses the whole grid - cheap rejection before sphere math.
    private Bounds3 _gridBounds;

    public GridshotSim(GridshotConfig cfg)
    {
        // Clamp config to sane bounds; never trust caller data in a hot engine.
        if (cfg.Columns < 1) cfg.Columns = 4;
        if (cfg.Rows < 1) cfg.Rows = 3;
        if (cfg.ActiveTargetCount < 1) cfg.ActiveTargetCount = 3;
        if (cfg.TargetRadius <= 0f) cfg.TargetRadius = 0.22f;
        if (cfg.CellSpacing <= 0f) cfg.CellSpacing = 0.9f;
        if (cfg.RayRange <= 0f) cfg.RayRange = 50f;
        if (cfg.DurationSeconds <= 0f) cfg.DurationSeconds = 30f;

        _cfg = cfg;
        _nodeCount = cfg.Columns * cfg.Rows;

        if (cfg.ActiveTargetCount > _nodeCount)
            cfg.ActiveTargetCount = _nodeCount; // cannot spawn more than there are nodes
        _cfg = cfg;

        // Single allocation event, at construction only.
        _nodePositions = new Vector3[_nodeCount];
        _nodeOccupied = new bool[_nodeCount];
        _slotToNode = new int[cfg.ActiveTargetCount];
        TargetPositions = new Vector3[cfg.ActiveTargetCount];

        BakeGridNodes();
        Reset(cfg.Seed);
    }

    /// <summary>Restarts the session deterministically from a seed. No allocation.</summary>
    public void Reset(uint seed)
    {
        _rng = new XorShift32(seed);
        Hits = 0;
        Misses = 0;
        ElapsedSeconds = 0f;

        // Clear ALL state first - including stale slot->node links - so the
        // occupancy table can never leak claimed nodes across resets.
        Array.Clear(_nodeOccupied, 0, _nodeCount);
        for (int i = 0; i < _slotToNode.Length; i++)
            _slotToNode[i] = -1;

        for (int i = 0; i < _slotToNode.Length; i++)
        {
            int node = ClaimRandomFreeNode();
            _slotToNode[i] = node;
            TargetPositions[i] = node >= 0 ? _nodePositions[node] : _cfg.GridCenter;
        }
    }

    public void Reset() => Reset(_cfg.Seed);

    /// <summary>Advances the session clock. Called once per rendered frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tick(float deltaSeconds)
    {
        if (deltaSeconds > 0f && !IsFinished)
        {
            ElapsedSeconds += deltaSeconds;
            if (ElapsedSeconds > _cfg.DurationSeconds)
                ElapsedSeconds = _cfg.DurationSeconds;
        }
    }

    /// <summary>
    /// Processes one trigger pull. Builds the shot ray analytically from the
    /// camera transform supplied by the renderer, tests it against the live
    /// spheres, updates score, and instantly respawns on a free node when hit.
    /// This is the ONLY gameplay entry point for input. Allocation-free.
    /// </summary>
    public ClickResult ProcessClick(Vector3 cameraOrigin, Vector3 cameraForward)
    {
        if (IsFinished) return ClickResult.Miss;

        var ray = new Ray3(cameraOrigin, cameraForward, _cfg.RayRange);

        // Cheap rejection: if the shot leaves the grid volume entirely, it is a
        // guaranteed miss and we skip all sphere math.
        if (!_gridBounds.IntersectsRay(ray))
        {
            Misses++;
            return ClickResult.Miss;
        }

        // Exact analytical test against the 3 live spheres; closest wins.
        int bestSlot = -1;
        float bestT = float.PositiveInfinity;
        Vector3 bestPoint = Vector3.Zero;

        for (int i = 0; i < TargetPositions.Length; i++)
        {
            RayHitInfo h = RaySphere.Intersect(ray, TargetPositions[i], _cfg.TargetRadius);
            if (h.Hit && h.Distance < bestT)
            {
                bestT = h.Distance;
                bestSlot = i;
                bestPoint = h.Point;
            }
        }

        if (bestSlot < 0)
        {
            Misses++;
            return ClickResult.Miss;
        }

        // ---- HIT: destroy, reclaim node, respawn instantly on a free node ----
        Hits++;
        ReleaseNode(_slotToNode[bestSlot]);

        int newNode = ClaimRandomFreeNode();
        _slotToNode[bestSlot] = newNode;
        // The "respawn" is literally one struct write into the shared array.
        TargetPositions[bestSlot] = newNode >= 0
            ? _nodePositions[newNode]
            : _cfg.GridCenter; // defensive: only reachable if every node is full

        return new ClickResult(true, bestSlot, newNode, bestPoint);
    }

    /// <summary>Read-only view of which grid node each live slot occupies (HUD/debug).</summary>
    public int GetNodeIndexForSlot(int slot)
        => (uint)slot < (uint)_slotToNode.Length ? _slotToNode[slot] : -1;

    // ------------------------------------------------------------------ //
    //  Internals
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Bakes every grid node's world position ONCE. The grid is centered on
    /// GridCenter in the X/Y plane facing +Z, matching the camera-forward axis.
    /// Also computes the enclosing AABB used for the rejection pass.
    /// </summary>
    private void BakeGridNodes()
    {
        int col = _cfg.Columns, row = _cfg.Rows;
        float totalW = (col - 1) * _cfg.CellSpacing;
        float totalH = (row - 1) * _cfg.CellSpacing;
        float startX = _cfg.GridCenter.X - totalW * 0.5f;
        float startY = _cfg.GridCenter.Y - totalH * 0.5f;

        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        int idx = 0;
        for (int r = 0; r < row; r++)
        {
            for (int c = 0; c < col; c++)
            {
                var p = new Vector3(startX + c * _cfg.CellSpacing,
                                    startY + r * _cfg.CellSpacing,
                                    _cfg.GridCenter.Z);
                _nodePositions[idx++] = p;

                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        // Pad the AABB by the target radius so edge spheres still pass rejection.
        float pad = _cfg.TargetRadius;
        _gridBounds = new Bounds3(min - new Vector3(pad, pad, pad),
                                  max + new Vector3(pad, pad, pad));
    }

    /// <summary>
    /// Picks a uniformly random unoccupied node using reservoir-style selection
    /// over the occupancy flags (O(nodeCount), allocation-free, unbiased).
    /// Returns -1 only if the grid is completely full.
    /// </summary>
    private int ClaimRandomFreeNode()
    {
        int seen = 0;
        int chosen = -1;

        for (int i = 0; i < _nodeCount; i++)
        {
            if (_nodeOccupied[i]) continue;
            seen++;
            // Classic reservoir sampling: keep this candidate with prob 1/seen.
            if (_rng.NextInt(seen) == 0)
                chosen = i;
        }

        if (chosen >= 0) _nodeOccupied[chosen] = true;
        return chosen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReleaseNode(int nodeIndex)
    {
        if ((uint)nodeIndex < (uint)_nodeCount)
            _nodeOccupied[nodeIndex] = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Max(float a, float b) => a > b ? a : b;
}
