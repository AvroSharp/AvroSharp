#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace AvroSharp.IO;

/// <summary>
/// Whether to use BMI2 PEXT/PDEP for varints. They are single-cycle instructions on Intel and on AMD from Zen 3
/// (family 19h), but microcoded, taking hundreds of cycles, on earlier AMD CPUs and on Hygon, where .NET still reports
/// BMI2 as supported. On a Zen+ CPU they made 5- to 10-byte varints up to 5.7 times slower than Apache.Avro (#39).
/// </summary>
internal static class FastBmi2
{
    /// <summary>First AMD family with fast PEXT/PDEP (Zen 3).</summary>
    private const int FirstFastAmdFamily = 0x19;

    /// <summary>
    /// Gets whether PEXT/PDEP are supported and fast. Computed once; the JIT treats a static readonly field as a
    /// constant in optimized code, so the check costs nothing on the hot path.
    /// </summary>
    public static readonly bool IsSupported = Bmi2.X64.IsSupported && !HasSlowPdepPext();

    /// <summary>
    /// Runs the class initializer. The JIT folds <see cref="IsSupported"/> to a constant only in code optimized after
    /// the class is initialized. A varint loop that never reaches a longer value, such as one writing only 2-byte
    /// values, would otherwise keep a class-initialization helper call in its body (seen in the tier-1 code; #102).
    /// The reader and writer constructors call this.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnsureInitialized() => _ = IsSupported;

    /// <summary>Decides from the CPUID vendor string and family whether PEXT/PDEP are microcoded.</summary>
    /// <param name="vendor">The CPUID leaf 0 vendor string, for example "AuthenticAMD".</param>
    /// <param name="family">The display family (base family plus extended family).</param>
    internal static bool IsSlowPdepPext(string vendor, int family) => vendor switch
    {
        "AuthenticAMD" => family < FirstFastAmdFamily,
        "HygonGenuine" => true,
        _ => false,
    };

    /// <summary>Gets the display family from CPUID leaf 1 EAX: the extended family is added when the base family is 0xF.</summary>
    /// <param name="eax">CPUID leaf 1 EAX.</param>
    internal static int DisplayFamily(int eax)
    {
        var baseFamily = (eax >> 8) & 0xF;
        return baseFamily == 0xF ? baseFamily + ((eax >> 20) & 0xFF) : baseFamily;
    }

    private static bool HasSlowPdepPext()
    {
        if (!X86Base.IsSupported)
        {
            return false;
        }

        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        var vendor = string.Concat(Ascii(ebx), Ascii(edx), Ascii(ecx));
        var (eax, _, _, _) = X86Base.CpuId(1, 0);
        return IsSlowPdepPext(vendor, DisplayFamily(eax));
    }

    private static string Ascii(int register) =>
        new([(char)(register & 0xFF), (char)((register >> 8) & 0xFF), (char)((register >> 16) & 0xFF), (char)((register >> 24) & 0xFF)]);
}
#endif
