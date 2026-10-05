// ============================================================================
//  Trainer.Sim :: Math :: RaySphere.cs
//  Analytical ray/sphere intersection. No Godot, no physics engine, no GC.
//  All methods are static, allocation-free and branch-light so they can be
//  inlined into the click hot-path.
// ============================================================================

using System;
using System.Numerics;

namespace Trainer.Sim.Math;

/// <summary>
/// A finite ray: origin + direction * t, with t clamped to [0, MaxDistance].
/// Kept as a mutable struct so callers can build one per click on the stack
/// without ever touching the heap.
/// </summary>
public struct Ray3
{
    public Vector3 Origin;
    public Vector3 Direction;   // Normalized.
    public float MaxDistance;   // Finite range (the grid plane sits inside it).

    public Ray3(Vector3 origin, Vector3 direction, float maxDistance)
    {
        Origin = origin;
        Direction = direction.ForwardOrZero();
        MaxDistance = maxDistance;
    }

    /// <summary>Point at parameter t along the ray (no allocation).</summary>
    public readonly Vector3 PointAt(float t) => Origin + Direction * t;

    /// <summary>
    /// Reinterprets this ray as a (ox,oy,oz, dx,dy,dz) float64 sextet for
    /// presentation layers or analytics that work in doubles. Allocation-free.
    /// </summary>
    public readonly void Deconstruct(out double ox, out double oy, out double oz,
                                     out double dx, out double dy, out double dz)
    {
        ox = Origin.X; oy = Origin.Y; oz = Origin.Z;
        dx = Direction.X; dy = Direction.Y; dz = Direction.Z;
    }
}

/// <summary>
/// Axis-aligned bounding box used for the cheap rejection pass that runs
/// before the exact sphere test. Stored flat (Min/Max) - no padding fields.
/// </summary>
public struct Bounds3
{
    public Vector3 Min;
    public Vector3 Max;

    public Bounds3(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>
    /// Slab-method AABB overlap test against a finite ray. Allocation-free.
    /// Used as an early-out so a click never touches more than O(1) spheres.
    /// </summary>
    public readonly bool IntersectsRay(in Ray3 ray)
    {
        float tMin = 0f;
        float tMax = ray.MaxDistance;

        // X slab
        if (System.MathF.Abs(ray.Direction.X) < float.Epsilon)
        {
            if (ray.Origin.X < Min.X || ray.Origin.X > Max.X) return false;
        }
        else
        {
            float invD = 1f / ray.Direction.X;
            float t0 = (Min.X - ray.Origin.X) * invD;
            float t1 = (Max.X - ray.Origin.X) * invD;
            if (invD < 0f) (t0, t1) = (t1, t0);
            if (t0 > tMin) tMin = t0;
            if (t1 < tMax) tMax = t1;
            if (tMin > tMax) return false;
        }

        // Y slab
        if (System.MathF.Abs(ray.Direction.Y) < float.Epsilon)
        {
            if (ray.Origin.Y < Min.Y || ray.Origin.Y > Max.Y) return false;
        }
        else
        {
            float invD = 1f / ray.Direction.Y;
            float t0 = (Min.Y - ray.Origin.Y) * invD;
            float t1 = (Max.Y - ray.Origin.Y) * invD;
            if (invD < 0f) (t0, t1) = (t1, t0);
            if (t0 > tMin) tMin = t0;
            if (t1 < tMax) tMax = t1;
            if (tMin > tMax) return false;
        }

        // Z slab
        if (System.MathF.Abs(ray.Direction.Z) < float.Epsilon)
        {
            if (ray.Origin.Z < Min.Z || ray.Origin.Z > Max.Z) return false;
        }
        else
        {
            float invD = 1f / ray.Direction.Z;
            float t0 = (Min.Z - ray.Origin.Z) * invD;
            float t1 = (Max.Z - ray.Origin.Z) * invD;
            if (invD < 0f) (t0, t1) = (t1, t0);
            if (t0 > tMin) tMin = t0;
            if (t1 < tMax) tMax = t1;
            if (tMin > tMax) return false;
        }

        return true;
    }
}

/// <summary>Result of a ray/sphere query - returned by value, zero allocation.</summary>
public struct RayHitInfo
{
    public bool Hit;
    public float Distance;      // Along the ray to the nearest surface point.
    public Vector3 Point;       // World-space impact point.

    public static readonly RayHitInfo Miss = default;
}

/// <summary>
/// Static analytical intersection helpers. This is the entire "physics" of the
/// trainer: closed-form math only, which keeps CPU near zero and produces no
/// garbage for the collector to stutter on.
/// </summary>
public static class RaySphere
{
    /// <summary>
    /// Nearest-positive intersection between a finite ray and a sphere.
    ///
    /// Derivation: substituting P(t) = O + Dt into |P - C|^2 = r^2 gives
    ///     t^2 + 2(b)r? ... solved via the reduced quadratic
    ///     t = -b +/- sqrt(b^2 - c),  b = D . (O - C),  c = |O - C|^2 - r^2.
    /// The discriminant check alone answers "does this ray hit?" for hits that
    /// straddle the sphere (c &lt; 0), so camera-inside-sphere is handled too.
    /// </summary>
    public static RayHitInfo Intersect(in Ray3 ray, in Vector3 center, float radius)
    {
        Vector3 oc = ray.Origin - center;

        float b = Vector3.Dot(oc, ray.Direction);          // half of the classic 'B'
        float c = Vector3.Dot(oc, oc) - radius * radius;  // classic 'C'
        float discriminant = b * b - c;

        if (discriminant < 0f)
            return RayHitInfo.Miss;                        // ray misses entirely

        float s = System.MathF.Sqrt(discriminant);

        float t = -b - s;                                  // nearest surface...
        if (t < 0f)
            t = -b + s;                                    // ...or we start inside

        if (t < 0f || t > ray.MaxDistance)
            return RayHitInfo.Miss;                        // behind or out of range

        return new RayHitInfo
        {
            Hit = true,
            Distance = t,
            Point = ray.Origin + ray.Direction * t
        };
    }

    /// <summary>Boolean-only fast path when the caller does not need the hit point.</summary>
    public static bool Hits(in Ray3 ray, in Vector3 center, float radius)
    {
        Vector3 oc = ray.Origin - center;
        float b = Vector3.Dot(oc, ray.Direction);
        float c = Vector3.Dot(oc, oc) - radius * radius;
        float d = b * b - c;
        if (d < 0f) return false;
        float t = -b - System.MathF.Sqrt(d);
        if (t < 0f) t = -b + System.MathF.Sqrt(d);
        return t >= 0f && t <= ray.MaxDistance;
    }

    /// <summary>
    /// Returns the smallest number of raw mouse counts needed to rotate from
    /// 'fromDegrees' to 'toDegrees' using Valorant's fixed 0.07 deg/count yaw.
    /// Used by analytics/FOV mapping, never in the click hot path.
    /// </summary>
    public static float MouseCountsBetweenAngles(float fromDegrees, float toDegrees,
                                                 float degreesPerCount = Sensitivity.DegreesPerMouseCount)
    {
        float delta = System.MathF.Abs(toDegrees - fromDegrees);
        if (delta > 180f) delta = 360f - delta;
        return delta / degreesPerCount;
    }

    /// <summary>
    /// Nearest intersection across a densely packed span of spheres, keeping the
    /// classic "closest hit wins" rule. The span is backed by a pre-allocated
    /// array, so this allocates nothing regardless of how many targets are live.
    /// </summary>
    public static RayHitInfo IntersectNearest(in Ray3 ray, Vector3[] centers, int count, float radius)
    {
        RayHitInfo best = RayHitInfo.Miss;
        float bestT = float.PositiveInfinity;

        for (int i = 0; i < count; i++)
        {
            RayHitInfo h = Intersect(ray, centers[i], radius);
            if (h.Hit && h.Distance < bestT)
            {
                bestT = h.Distance;
                best = h;
            }
        }

        return best;
    }
}

/// <summary>
/// Exact Valorant aim-math constants. One source of truth for both the Sim
/// (hit volumes) and the Godot layer (mouse -> rotation conversion).
/// </summary>
public static class Sensitivity
{
    /// <summary>Valorant rotates 0.07 world degrees per raw mouse count.</summary>
    public const float DegreesPerMouseCount = 0.07f;

    /// <summary>Valorant's horizontal field of view, in degrees (16:9).</summary>
    public const float HorizontalFovDegrees = 103f;

    /// <summary>Converts a mouse-count delta into radians for the engine.</summary>
    public static float CountsToRadians(float counts, float sensMultiplier = 1f)
        => counts * DegreesPerMouseCount * sensMultiplier * (System.MathF.PI / 180f);

    /// <summary>
    /// Converts Valorant's 103-degree horizontal FOV into the vertical FOV
    /// (radians) that Godot's projection expects for a 16:9 viewport.
    /// </summary>
    public static float VerticalFovRadiansFromHorizontal(float horizontalDegrees, float aspect = 16f / 9f)
    {
        float halfH = horizontalDegrees * 0.5f * (System.MathF.PI / 180f);
        return 2f * System.MathF.Atan(System.MathF.Tan(halfH) / aspect);
    }
}

internal static class VectorHelpers
{
    /// <summary>Normalizes a direction, returning UnitZ for a degenerate vector.</summary>
    public static Vector3 ForwardOrZero(this Vector3 v)
    {
        float lenSq = v.LengthSquared();
        if (lenSq < 1e-12f) return Vector3.UnitZ;
        return v * (1f / System.MathF.Sqrt(lenSq));
    }
}
