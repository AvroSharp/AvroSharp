using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AvroSharp.Tests;

internal static class TestData
{
    public static string PathOf(string relativePath) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Reads Apache Avro's <c>schema-tests.txt</c>: blocks of <c>&lt;&lt;INPUT</c>, <c>&lt;&lt;canonical</c> and an optional
    /// <c>&lt;&lt;fingerprint</c>, where a multi-line input is written as <c>&lt;&lt;INPUT</c> ... <c>INPUT</c>.
    /// </summary>
    public static IReadOnlyList<SchemaTestVector> ReadApacheSchemaVectors()
    {
        var lines = File.ReadAllLines(PathOf("apache-avro/schema-tests.txt"));
        var vectors = new List<SchemaTestVector>();
        string? input = null;
        string? canonical = null;
        long? fingerprint = null;
        var number = 0;

        void Flush()
        {
            if (input is not null)
            {
                vectors.Add(new SchemaTestVector(number++, input, canonical ?? throw new InvalidDataException($"Vector {number} has no canonical form."), fingerprint));
            }

            input = null;
            canonical = null;
            fingerprint = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("//", StringComparison.Ordinal) || (line.Length > 0 && line[0] == '#'))
            {
                continue;
            }

            if (line.StartsWith("<<INPUT", StringComparison.Ordinal))
            {
                Flush();
                var rest = line["<<INPUT".Length..].Trim();
                if (rest.Length > 0)
                {
                    input = rest;
                }
                else
                {
                    var builder = new StringBuilder();
                    while (!string.Equals(lines[++i], "INPUT", StringComparison.Ordinal))
                    {
                        builder.AppendLine(lines[i]);
                    }

                    input = builder.ToString();
                }
            }
            else if (line.StartsWith("<<canonical ", StringComparison.Ordinal))
            {
                canonical = line["<<canonical ".Length..];
            }
            else if (line.StartsWith("<<fingerprint ", StringComparison.Ordinal))
            {
                fingerprint = long.Parse(line["<<fingerprint ".Length..], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        Flush();
        return vectors;
    }
}

public sealed record SchemaTestVector(int Number, string Input, string Canonical, long? Fingerprint)
{
    public override string ToString() => $"#{Number:000} {Canonical}";
}
