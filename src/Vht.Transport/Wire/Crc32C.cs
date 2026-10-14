using System.Buffers.Binary;

namespace Vht.Transport;

/// <summary>CRC-32C (Castagnoli, reflected polynomial 0x82F63B78) — the per-frame integrity check of ARCHITECTURE.md §8.</summary>
/// <remarks>
/// <para>
/// Hand-rolled on purpose rather than taking a dependency: the .NET
/// <c>System.IO.Hashing.Crc32</c> package implements the IEEE polynomial, not Castagnoli, and this
/// is a few dozen lines of table-driven code with a published test vector.
/// </para>
/// <para>
/// <b>Why slice-by-8.</b> This CRC runs over every payload byte of every compressed frame, so a
/// byte-at-a-time loop is the throughput ceiling of the whole <c>TlsLz4</c> path. The slice-by-8 form
/// below (eight 256-entry tables, eight bytes per iteration) is the standard remedy: same polynomial,
/// same output, several times the byte rate. The vectorised SSE4.2 / ARM-CRC instruction would be
/// faster still and is deliberately not used — it is architecture-specific, needs a runtime dispatch
/// path that cannot be exercised on every target, and the remaining gain is small next to the TLS
/// record layer above it.
/// </para>
/// </remarks>
public static class Crc32C
{
    private const uint Polynomial = 0x82F63B78u;

    /// <summary><c>Tables[0]</c> is the classic byte-at-a-time table; <c>Tables[k]</c> is it advanced by <c>k</c> bytes.</summary>
    private static readonly uint[][] Tables = BuildTables();

    /// <summary>Computes the CRC-32C of <paramref name="data"/>.</summary>
    /// <remarks>Test vector: <c>Compute("123456789"u8)</c> == <c>0xE3069283</c>.</remarks>
    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFFu, data);

    /// <summary>Continues a CRC-32C computation over a second segment.</summary>
    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        ~Update(Update(0xFFFFFFFFu, first), second);

    /// <summary>The un-inverted running CRC, so two spans can be chained without a restart.</summary>
    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var remaining = data;

        // Eight bytes per iteration: one little-endian 32-bit read folded into the CRC, eight table
        // lookups. The bounds are safe because the span still holds a full ulong here.
        while (remaining.Length >= sizeof(ulong))
        {
            crc ^= BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            crc = Tables[7][crc & 0xFF]
                ^ Tables[6][(crc >> 8) & 0xFF]
                ^ Tables[5][(crc >> 16) & 0xFF]
                ^ Tables[4][(crc >> 24) & 0xFF]
                ^ Tables[3][remaining[4]]
                ^ Tables[2][remaining[5]]
                ^ Tables[1][remaining[6]]
                ^ Tables[0][remaining[7]];
            remaining = remaining[sizeof(ulong)..];
        }

        foreach (var b in remaining)
        {
            crc = Tables[0][(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[][] BuildTables()
    {
        var tables = new uint[8][];
        var first = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            }

            first[i] = c;
        }

        tables[0] = first;
        for (var n = 1; n < 8; n++)
        {
            var previous = tables[n - 1];
            var table = new uint[256];
            for (var i = 0; i < 256; i++)
            {
                table[i] = (previous[i] >> 8) ^ first[previous[i] & 0xFF];
            }

            tables[n] = table;
        }

        return tables;
    }
}
