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

    /// <summary>Runs the tool with standard input and output as bytes, for container files given as <c>-</c>.</summary>
    public static (ToolResult Result, byte[] StandardOutput) RunWithStreams(byte[] standardInput, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var stdout = new MemoryStream();
        var console = new ToolConsole(new StringReader(string.Empty), output, error, () => new MemoryStream(standardInput, writable: false), () => new NonClosingStream(stdout));
        var code = Cli.Run(args, console);
        return (new ToolResult(code, output.ToString(), error.ToString()), stdout.ToArray());
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

    /// <summary>Copies a file of the repository's tests/TestData under the test's folder and returns its full path.</summary>
    public string CopyTestData(string relativePath)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", relativePath.Replace('/', Path.DirectorySeparatorChar));
        var path = PathOf(Path.GetFileName(source));
        File.Copy(source, path, overwrite: true);
        return path;
    }

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

    // Standard output stays readable after the tool disposes it.
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
