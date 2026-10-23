using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CodeCarver.Core.Util;

/// <summary>
/// A name held as a 128-bit key instead of a string: a set of millions of names (work's header carve took 18.9M
/// defines in one pass) costs 24 bytes an entry, where a string was some 100 more. Two 64-bit hashes of the name's
/// UTF-16 text, each with its own constants: a set skipping what it holds (a define already taken) misses a name only
/// when two distinct names share all 128 bits. A set that can only keep more on a collision may hold the lower 64.
/// </summary>
public static class NameKey
{
    const ulong P1 = 0x9E3779B97F4A7C15, P2 = 0xC2B2AE3D27D4EB4F, P3 = 0x165667B19E3779F9, P4 = 0x85EBCA77C2B2AE63;

    public static UInt128 Of(string name) => Of(name.AsSpan());

    public static UInt128 Of(ReadOnlySpan<char> name)
    {
        var bytes = MemoryMarshal.AsBytes(name);
        ulong a = P1 ^ (ulong)bytes.Length, b = P2 + (ulong)bytes.Length;
        var i = 0;
        for (; i + 8 <= bytes.Length; i += 8)
        {
            var v = BinaryPrimitives.ReadUInt64LittleEndian(bytes[i..]);
            a = BitOperations.RotateLeft(a ^ (v * P2), 31) * P1;
            b = BitOperations.RotateLeft(b + (v * P4), 27) * P3 + P2;
        }
        if (i < bytes.Length)
        {
            ulong v = 0;
            for (var k = bytes.Length - 1; k >= i; k--) v = (v << 8) | bytes[k];
            a = BitOperations.RotateLeft(a ^ (v * P2), 31) * P1;
            b = BitOperations.RotateLeft(b + (v * P4), 27) * P3 + P2;
        }
        return new UInt128(Mix(b ^ P3), Mix(a));
    }

    /// <summary>The lower 64 bits: enough for a set where a collision only keeps a name more.</summary>
    public static ulong Short(ReadOnlySpan<char> name) => (ulong)Of(name);

    // MurmurHash3's 64-bit finalizer: every input bit reaches every output bit.
    static ulong Mix(ulong h)
    {
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCD;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53;
        h ^= h >> 33;
        return h;
    }
}
