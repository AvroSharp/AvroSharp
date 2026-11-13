using System;
using System.CommandLine;
using System.IO.Compression;
using AvroSharp.Codecs;
using AvroSharp.Containers;

namespace AvroSharp.Tool;

/// <summary>The <c>--codec</c> and <c>--level</c> options of the commands that write container files.</summary>
internal sealed class CodecOptions
{
    private const string Null = "null";
    private const string Deflate = "deflate";
    private const string Snappy = "snappy";
    private const string Zstandard = "zstandard";
    private const string Zstd = "zstd";
    private const string Bzip2 = "bzip2";
    private const string Xz = "xz";

    public CodecOptions(string? defaultCodec)
    {
        Codec = new Option<string?>("--codec", "-c")
        {
            Description = defaultCodec is null
                ? "The codec that compresses the blocks: null, deflate, snappy, zstandard (or zstd), bzip2 or xz. Without it, the first input's."
                : $"The codec that compresses the blocks: null, deflate, snappy, zstandard (or zstd), bzip2 or xz. The default is {defaultCodec}.",
        };
        Codec.AcceptOnlyFromAmong(Null, Deflate, Snappy, Zstandard, Zstd, Bzip2, Xz);
        if (defaultCodec is not null)
        {
            Codec.DefaultValueFactory = _ => defaultCodec;
        }

        Level = new Option<int?>("--level", "-l")
        {
            Description = "The compression level: deflate 0 to 9 (0 stores, 1 to 3 fastest, 4 to 6 optimal, 7 to 9 smallest), zstandard its levels (3 by default), bzip2 1 to 9 (the block size in 100 kB), xz 0 to 9. Without it, each codec's default: unlike Java's avro-tools, xz works without a level.",
        };
    }

    public Option<string?> Codec { get; }

    public Option<int?> Level { get; }

    public void AddTo(Command command)
    {
        command.Options.Add(Codec);
        command.Options.Add(Level);
        command.Validators.Add(result =>
        {
            // In a validator, an option not on the command line reads as null, not as its default.
            if (result.GetValue(Level) is not { } level)
            {
                return;
            }

            if (result.GetValue(Codec) is not { } codec)
            {
                result.AddError("--level needs --codec.");
            }
            else if (TryCreate(codec, level, out _) is { } problem)
            {
                result.AddError(problem);
            }
        });
    }

    /// <summary>The codec chosen on the command line, or <see langword="null"/> when none is.</summary>
    public AvroCodec? Get(ParseResult result) =>
        result.GetValue(Codec) is { } name
            ? (TryCreate(name, result.GetValue(Level), out var codec) is null ? codec : throw new InvalidOperationException("Validated."))
            : null;

    // Null when the codec was created; otherwise why the level doesn't suit it.
    private static string? TryCreate(string name, int? level, out AvroCodec codec)
    {
        codec = AvroCodec.Null;
        switch (name)
        {
            case Null when level is null:
                return null;
            case Deflate:
                if (level is < 0 or > 9)
                {
                    return "--level for deflate must be from 0 to 9.";
                }

                codec = level switch
                {
                    null => AvroCodec.Deflate,
                    0 => new DeflateCodec(CompressionLevel.NoCompression),
                    <= 3 => new DeflateCodec(CompressionLevel.Fastest),
                    <= 6 => new DeflateCodec(CompressionLevel.Optimal),
                    _ => new DeflateCodec(CompressionLevel.SmallestSize),
                };
                return null;
            case Snappy when level is null:
                codec = SnappyCodec.Default;
                return null;
            case Zstandard or Zstd:
                if (level is { } zstdLevel && (zstdLevel < ZstandardCodec.MinLevel || zstdLevel > ZstandardCodec.MaxLevel))
                {
                    return $"--level for zstandard must be from {ZstandardCodec.MinLevel} to {ZstandardCodec.MaxLevel}.";
                }

                codec = level is { } zl ? new ZstandardCodec(zl) : ZstandardCodec.Default;
                return null;
            case Bzip2:
                if (level is < 1 or > 9)
                {
                    return "--level for bzip2 must be from 1 to 9.";
                }

                codec = level is { } blockSize ? new Bzip2Codec(blockSize) : Bzip2Codec.Default;
                return null;
            case Xz:
                if (level is < 0 or > 9)
                {
                    return "--level for xz must be from 0 to 9.";
                }

                codec = level is { } preset ? new XzCodec(preset) : XzCodec.Default;
                return null;
            default:
                return $"The {name} codec has no levels.";
        }
    }
}
