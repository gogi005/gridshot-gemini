// ============================================================================
//  Trainer.Sim :: Core :: XorShift32.cs
//  Deterministic, allocation-free PRNG. Struct-based so a scenario can own one
//  by value inside its own state (no heap object, no GC pressure) while still
//  mutating in place via `ref this` semantics on the fields.
// ============================================================================

using System;
using System.Runtime.CompilerServices;

namespace Trainer.Sim.Core;

/// <summary>
/// Marsaglia's XORSHIFT-32. Period 2^32 - 1, passes basic diehard-style sanity
/// for gameplay spawning, and costs ~4 CPU cycles per draw. Chosen over
/// <see cref="System.Random"/> because Random is a class (heap), not
/// deterministic across runtimes, and allocates on some paths.
/// </summary>
public struct XorShift32
{
    private uint _state;

    /// <summary>Seeds the generator. A zero state is fixed-point, so it is remapped.</summary>
    public XorShift32(uint seed)
    {
        _state = seed != 0u ? seed : 0x9E3779B9u; // golden ratio constant
    }

    /// <summary>Re-seeds an existing generator without allocating a new one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reseed(uint seed) => _state = seed != 0u ? seed : 0x9E3779B9u;

    /// <summary>Raw 32-bit pseudo-random output.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Next()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>Uniform float in [0, 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextSingle() => Next() * (1f / 4294967296f);

    /// <summary>Uniform integer in [0, exclusiveMax). Returns 0 when max &lt;= 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextInt(int exclusiveMax)
    {
        if (exclusiveMax <= 0) return 0;
        // Modulo bias is irrelevant at 2^32 vs grid sizes of ~12 nodes.
        return (int)(Next() % (uint)exclusiveMax);
    }

    /// <summary>Uniform integer in [inclusiveMin, exclusiveMax).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextInt(int inclusiveMin, int exclusiveMax)
        => inclusiveMin + NextInt(exclusiveMax - inclusiveMin);

    /// <summary>Uniform float in [min, max).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextRange(float min, float max) => min + NextSingle() * (max - min);
}
