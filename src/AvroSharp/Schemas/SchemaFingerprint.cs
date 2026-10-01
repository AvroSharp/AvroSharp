using System;
using System.Security.Cryptography;

namespace AvroSharp.Schemas;

/// <summary>
/// Schema fingerprints computed over the <see cref="AvroSchema.CanonicalForm">Parsing Canonical Form</see>, as
/// described by the specification.
/// </summary>
/// <seealso cref="AvroSchema.Fingerprint64"/>
public static class SchemaFingerprint
{
    /// <summary>The CRC-64-AVRO fingerprint of empty input, as <see cref="Crc64Avro(ReadOnlySpan{byte})"/> returns it.</summary>
    public const long Crc64AvroEmpty = unchecked((long)Crc64Polynomial);

    // The specification's EMPTY constant: the fingerprint's initial value and the polynomial of its table.
    private const ulong Crc64Polynomial = 0xC15D213AA4D7A795UL;

    private static readonly ulong[] s_crc64Table = CreateCrc64Table();

    /// <summary>Computes the CRC-64-AVRO (Rabin) fingerprint of arbitrary bytes.</summary>
    /// <param name="data">The bytes to fingerprint, usually a UTF-8 canonical form.</param>
    public static long Crc64Avro(ReadOnlySpan<byte> data)
    {
        var table = s_crc64Table;
        var fp = Crc64Polynomial;
        foreach (var b in data)
        {
            fp = (fp >> 8) ^ table[(int)((fp ^ b) & 0xFF)];
        }

        return unchecked((long)fp);
    }

    /// <summary>Gets the CRC-64-AVRO fingerprint of a schema's canonical form (same as <see cref="AvroSchema.Fingerprint64"/>).</summary>
    /// <param name="schema">The schema.</param>
    public static long Crc64Avro(AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.Fingerprint64;
    }

    /// <summary>Computes the 16-byte MD5 fingerprint of a schema's canonical form.</summary>
    /// <param name="schema">The schema.</param>
#pragma warning disable CA5351 // MD5 is one of the fingerprints the Avro specification defines; it is not used for security.
    public static byte[] Md5(AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
#if NET8_0_OR_GREATER
        return MD5.HashData(schema.GetCanonicalUtf8());
#else
        using var md5 = MD5.Create();
        return md5.ComputeHash(schema.GetCanonicalUtf8());
#endif
    }
#pragma warning restore CA5351

    /// <summary>Computes the 32-byte SHA-256 fingerprint of a schema's canonical form.</summary>
    /// <param name="schema">The schema.</param>
    public static byte[] Sha256(AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
#if NET8_0_OR_GREATER
        return SHA256.HashData(schema.GetCanonicalUtf8());
#else
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(schema.GetCanonicalUtf8());
#endif
    }

    private static ulong[] CreateCrc64Table()
    {
        var table = new ulong[256];
        for (var i = 0; i < table.Length; i++)
        {
            var fp = (ulong)i;
            for (var j = 0; j < 8; j++)
            {
                fp = (fp >> 1) ^ (Crc64Polynomial & (0UL - (fp & 1)));
            }

            table[i] = fp;
        }

        return table;
    }
}
