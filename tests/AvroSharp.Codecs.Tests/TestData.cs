using System;
using System.IO;

namespace AvroSharp.Codecs.Tests;

internal static class TestData
{
    public static string PathOf(string relativePath) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", relativePath.Replace('/', Path.DirectorySeparatorChar));
}
