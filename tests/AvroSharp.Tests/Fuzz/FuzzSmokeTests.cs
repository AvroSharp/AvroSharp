using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Fuzz;

namespace AvroSharp.Tests.Fuzz;

/// <summary>
/// Runs the fuzz targets on seeded random mutations of valid inputs. It is not a replacement for coverage-guided
/// fuzzing (fuzz/README.md), but it runs on every build and catches crashes near valid input: truncation, bit flips,
/// boundary bytes and repeated chunks. A failure prints the input, which reproduces it.
/// </summary>
public class FuzzSmokeTests
{
    private const int MutationsPerSeed = 300;

    public static IEnumerable<string> Targets() =>
        [nameof(FuzzTargets.GenericBinary), nameof(FuzzTargets.GenericJson), nameof(FuzzTargets.SchemaParse), nameof(FuzzTargets.ContainerFile), nameof(FuzzTargets.SingleObject), nameof(FuzzTargets.Resolution), nameof(FuzzTargets.RegistryMessage)];

    [Test]
    [MethodDataSource(nameof(Targets))]
    public async Task MutatedInputs_OnlyRaiseAvroExceptions(string target)
    {
        FuzzTarget run = target switch
        {
            nameof(FuzzTargets.GenericBinary) => FuzzTargets.GenericBinary,
            nameof(FuzzTargets.GenericJson) => FuzzTargets.GenericJson,
            nameof(FuzzTargets.ContainerFile) => FuzzTargets.ContainerFile,
            nameof(FuzzTargets.SingleObject) => FuzzTargets.SingleObject,
            nameof(FuzzTargets.Resolution) => FuzzTargets.Resolution,
            nameof(FuzzTargets.RegistryMessage) => FuzzTargets.RegistryMessage,
            _ => FuzzTargets.SchemaParse,
        };

        var seeds = FuzzTargets.Seeds().Where(s => string.Equals(s.Target, target, StringComparison.Ordinal)).Select(s => s.Input).ToList();
        var random = new Random(target.Length * 7919);
        var runs = 0;
        foreach (var seed in seeds)
        {
            // Unmutated seeds must be accepted without error.
            run(seed);
            for (var i = 0; i < MutationsPerSeed; i++)
            {
                var input = Mutate(seed, random);
                try
                {
                    run(input);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"{target} failed on input {Convert.ToHexString(input)}: {ex.GetType().Name}: {ex.Message}", ex);
                }

                runs++;
            }
        }

        await Assert.That(runs).IsEqualTo(seeds.Count * MutationsPerSeed);
    }

    // libFuzzer's timeout from the Fuzz workflow (2026-10-12): a "null"-schema file whose 140 bytes declare 16,777,153
    // objects in one zero-byte block, under the default MaxZeroSizeValuesPerBlock. Round-tripping them all took 12.5 s
    // uninstrumented; the harness's own limit rejects the block.
    [Test]
    public async Task ContainerFile_ManyZeroSizeObjects_FinishesQuickly()
    {
        var input = Convert.FromHexString(
            "4F626A0104166176726F2E736368656D6196017B2274797065223A226E756C6C222C22646C756573223A7B2274797065223A2261727261" +
            "79222C226974656D73223A5B226E756C6C222C226C64626F7565222C22737472696E67225D7D7D00000000000000000000000000000000" +
            "00000082FFFF0F0000000000000000000000000000000000000000000000");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        FuzzTargets.ContainerFile(input);
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }

    private static byte[] Mutate(byte[] seed, Random random)
    {
        var data = new List<byte>(seed);
        for (var edits = random.Next(1, 4); edits > 0; edits--)
        {
            var position = data.Count == 0 ? 0 : random.Next(data.Count);
            switch (random.Next(6))
            {
                case 0 when data.Count > 0:
                    data[position] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when data.Count > 0:
                    data[position] = s_interesting[random.Next(s_interesting.Length)];
                    break;
                case 2:
                    data.Insert(position, s_interesting[random.Next(s_interesting.Length)]);
                    break;
                case 3 when data.Count > 0:
                    data.RemoveAt(position);
                    break;
                case 4:
                    data.RemoveRange(position, data.Count - position);
                    break;
                case 5 when data.Count > 0:
                    var length = random.Next(1, Math.Min(16, data.Count - position) + 1);
                    data.InsertRange(position, data.GetRange(position, length));
                    break;
            }
        }

        return [.. data];
    }

    // Varint boundaries, sign bits, and JSON structure characters.
    private static readonly byte[] s_interesting = [0x00, 0x01, 0x7F, 0x80, 0xFF, 0xFE, (byte)'"', (byte)'{', (byte)'}', (byte)'[', (byte)']', (byte)':', (byte)',', (byte)'\\'];

    private delegate void FuzzTarget(ReadOnlySpan<byte> data);
}
