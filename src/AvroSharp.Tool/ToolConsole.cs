using System;
using System.IO;

namespace AvroSharp.Tool;

/// <summary>
/// Where the tool reads and writes: text for schemas, results and errors, and binary streams for container files
/// given as <c>-</c> (standard input and output).
/// </summary>
/// <param name="Input">Standard input as text, for a schema given as <c>-</c>.</param>
/// <param name="Output">Results and help.</param>
/// <param name="Error">Errors.</param>
/// <param name="OpenStandardInput">Opens standard input as bytes, for a file given as <c>-</c>.</param>
/// <param name="OpenStandardOutput">Opens standard output as bytes, for <c>--output -</c>.</param>
internal sealed record ToolConsole(TextReader Input, TextWriter Output, TextWriter Error, Func<Stream> OpenStandardInput, Func<Stream> OpenStandardOutput);
