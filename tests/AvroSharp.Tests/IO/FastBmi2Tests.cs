#if NET8_0_OR_GREATER
using System.Threading.Tasks;
using AvroSharp.IO;

namespace AvroSharp.Tests.IO;

/// <summary>
/// The CPU check that keeps microcoded PEXT/PDEP (AMD before Zen 3, Hygon) off the varint path (#39). The varint
/// results themselves are covered by the encoding tests on whichever path the machine takes: this i5 and
/// DOTNET_EnableHWIntrinsic=0 take shifts and masks, BMI2 CI runners take PEXT/PDEP.
/// </summary>
public class FastBmi2Tests
{
    [Test]
    [Arguments("AuthenticAMD", 0x15, true)]   // Excavator: BMI2, microcoded PDEP/PEXT
    [Arguments("AuthenticAMD", 0x17, true)]   // Zen, Zen+, Zen 2
    [Arguments("AuthenticAMD", 0x18, true)]   // boundary: every family below 19h is treated as slow
    [Arguments("AuthenticAMD", 0x19, false)]  // Zen 3, Zen 4
    [Arguments("AuthenticAMD", 0x1A, false)]  // Zen 5
    [Arguments("HygonGenuine", 0x18, true)]
    [Arguments("GenuineIntel", 0x06, false)]
    [Arguments("CentaurHauls", 0x07, false)]
    public async Task SlowPdepPext_IsDetectedFromVendorAndFamily(string vendor, int family, bool slow) =>
        await Assert.That(FastBmi2.IsSlowPdepPext(vendor, family)).IsEqualTo(slow);

    [Test]
    [Arguments(0x00810F81, 0x17)]  // Ryzen 5 3500U (Zen+)
    [Arguments(0x00A20F10, 0x19)]  // Ryzen 9 5950X (Zen 3)
    [Arguments(0x00B40F40, 0x1A)]  // Ryzen 9000 (Zen 5)
    [Arguments(0x000306A9, 0x06)]  // Core i5-3570K (Ivy Bridge): base family 6, extended family ignored
    [Arguments(0x000906A3, 0x06)]  // Core i7-12800H (Alder Lake)
    public async Task DisplayFamily_AddsTheExtendedFamilyOnlyForBaseFamilyF(int eax, int family) =>
        await Assert.That(FastBmi2.DisplayFamily(eax)).IsEqualTo(family);
}
#endif
