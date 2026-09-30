using System;

namespace AvroSharp;

/// <summary>
/// Raised when a schema is invalid, either while parsing schema JSON or while constructing a schema in code.
/// </summary>
public class AvroSchemaException : AvroException
{
    /// <summary>Initializes a new instance of the <see cref="AvroSchemaException"/> class.</summary>
    public AvroSchemaException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AvroSchemaException"/> class with a message.</summary>
    /// <param name="message">The error message.</param>
    public AvroSchemaException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AvroSchemaException"/> class with a message and an inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public AvroSchemaException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AvroSchemaException"/> class with location information.</summary>
    /// <param name="message">The error message, without location information.</param>
    /// <param name="path">The JSON path of the offending element, for example <c>$.fields[2].type</c>.</param>
    /// <param name="lineNumber">The 1-based line of the offending element, when known.</param>
    /// <param name="bytePositionInLine">The 1-based UTF-8 byte column of the offending element, when known.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public AvroSchemaException(string message, string? path, long? lineNumber, long? bytePositionInLine, Exception? innerException = null)
        : base(FormatMessage(message, path, lineNumber, bytePositionInLine), innerException)
    {
        Reason = message;
        Path = path;
        LineNumber = lineNumber;
        BytePositionInLine = bytePositionInLine;
    }

    /// <summary>Gets the error message without location information.</summary>
    public string? Reason { get; }

    /// <summary>Gets the JSON path of the offending element (for example <c>$.fields[2].type</c>), when known.</summary>
    public string? Path { get; }

    /// <summary>Gets the 1-based line number of the offending element, when known.</summary>
    public long? LineNumber { get; }

    /// <summary>Gets the 1-based UTF-8 byte column of the offending element, when known.</summary>
    public long? BytePositionInLine { get; }

    private static string FormatMessage(string message, string? path, long? line, long? column)
    {
        if (path is null && line is null)
        {
            return message;
        }

        var location = line is { } l
            ? column is { } c ? $"line {l}, column {c}" : $"line {l}"
            : null;

        return (path, location) switch
        {
            (not null, not null) => $"{message} (at {path}, {location})",
            (not null, null) => $"{message} (at {path})",
            _ => $"{message} (at {location})",
        };
    }
}
