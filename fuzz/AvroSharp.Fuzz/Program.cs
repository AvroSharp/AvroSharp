using System;
using System.IO;
using System.Linq;
using AvroSharp.Fuzz;
using SharpFuzz;

// libFuzzer entry point. See fuzz/README.md for how to instrument AvroSharp.dll and run a target.
//   AvroSharp.Fuzz <target>                 run under libfuzzer-dotnet
//   AvroSharp.Fuzz --write-seeds <folder>   write the seed corpus, one subfolder per target
switch (args)
{
    case ["--write-seeds", var folder]:
        var count = 0;
        foreach (var group in FuzzTargets.Seeds().GroupBy(s => s.Target, StringComparer.Ordinal))
        {
            var target = Directory.CreateDirectory(Path.Combine(folder, group.Key));
            var index = 0;
            foreach (var seed in group)
            {
                File.WriteAllBytes(Path.Combine(target.FullName, $"seed-{index++:D3}"), seed.Input);
                count++;
            }
        }

        Console.WriteLine($"Wrote {count} seeds to {folder}.");
        return 0;
    case [nameof(FuzzTargets.GenericBinary)]:
        Fuzzer.LibFuzzer.Run(FuzzTargets.GenericBinary);
        return 0;
    case [nameof(FuzzTargets.GenericJson)]:
        Fuzzer.LibFuzzer.Run(FuzzTargets.GenericJson);
        return 0;
    case [nameof(FuzzTargets.SchemaParse)]:
        Fuzzer.LibFuzzer.Run(FuzzTargets.SchemaParse);
        return 0;
    case [nameof(FuzzTargets.ContainerFile)]:
        Fuzzer.LibFuzzer.Run(FuzzTargets.ContainerFile);
        return 0;
    case [nameof(FuzzTargets.SingleObject)]:
        Fuzzer.LibFuzzer.Run(FuzzTargets.SingleObject);
        return 0;
    case [nameof(FuzzTargets.Resolution)]:
        Fuzzer.LibFuzzer.Run(FuzzTargets.Resolution);
        return 0;
    case [nameof(FuzzTargets.RegistryMessage)]:
        Fuzzer.LibFuzzer.Run(FuzzTargets.RegistryMessage);
        return 0;
    default:
        Console.Error.WriteLine($"Usage: AvroSharp.Fuzz <{nameof(FuzzTargets.GenericBinary)}|{nameof(FuzzTargets.GenericJson)}|{nameof(FuzzTargets.SchemaParse)}|{nameof(FuzzTargets.ContainerFile)}|{nameof(FuzzTargets.SingleObject)}|{nameof(FuzzTargets.Resolution)}|{nameof(FuzzTargets.RegistryMessage)}>, or --write-seeds <folder>");
        return 1;
}
