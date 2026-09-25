using System.Threading.Tasks;

namespace AvroSharp.Tests;

public class AvroCodecNamesTests
{
    // Values copied from the "Required Codecs" / "Optional Codecs" sections of the Avro 1.12 specification.
    [Test]
    [Arguments("null")]
    [Arguments("deflate")]
    [Arguments("snappy")]
    [Arguments("bzip2")]
    [Arguments("xz")]
    [Arguments("zstandard")]
    public async Task IsStandard_AcceptsEverySpecificationCodec(string name)
    {
        await Assert.That(AvroCodecNames.IsStandard(name)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("Deflate")]
    [Arguments("zstd")]
    [Arguments("lz4")]
    [Arguments("brotli")]
    public async Task IsStandard_RejectsOtherNames(string? name)
    {
        await Assert.That(AvroCodecNames.IsStandard(name)).IsFalse();
    }

    [Test]
    public async Task Constants_MatchSpecificationSpelling()
    {
        await Assert.That(AvroCodecNames.Null).IsEqualTo("null");
        await Assert.That(AvroCodecNames.Deflate).IsEqualTo("deflate");
        await Assert.That(AvroCodecNames.Snappy).IsEqualTo("snappy");
        await Assert.That(AvroCodecNames.Bzip2).IsEqualTo("bzip2");
        await Assert.That(AvroCodecNames.Xz).IsEqualTo("xz");
        await Assert.That(AvroCodecNames.Zstandard).IsEqualTo("zstandard");
    }
}
