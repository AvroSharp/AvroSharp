using System;
using System.IO;

namespace AvroSharp.Tool.Tests;

/// <summary>The result of one run of the tool.</summary>
internal sealed record ToolResult(int ExitCode, string Output, string Error);

/// <summary>Runs the tool in-process, and gives each test a folder of its own.</summary>
internal sealed class ToolRunner : IDisposable
{
    public ToolRunner() => Folder = Directory.CreateTempSubdirectory("avrosharp-tool-").FullName;

    public string Folder { get; }

    public static ToolResult Run(params string[] args) => RunWithInput(string.Empty, args);

    public static ToolResult RunWithInput(string input, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var reader = new StringReader(input);
        var code = Cli.Run(args, reader, output, error);
        return new ToolResult(code, output.ToString(), error.ToString());
    }

    /// <summary>Writes a file under the test's folder and returns its full path.</summary>
    public string Write(string relativePath, string text)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public string PathOf(string relativePath) => Path.Combine(Folder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS to clean up.
        }
    }
}
