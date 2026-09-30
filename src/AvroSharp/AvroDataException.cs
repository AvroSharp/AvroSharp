using System;

namespace AvroSharp;

/// <summary>Raised when Avro data being read, binary or JSON, is malformed or truncated, or does not match the schema.</summary>
public class AvroDataException : AvroException
{
    /// <summary>Initializes a new instance of the <see cref="AvroDataException"/> class.</summary>
    public AvroDataException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AvroDataException"/> class with a message.</summary>
    /// <param name="message">The error message.</param>
    public AvroDataException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AvroDataException"/> class with a message and an inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public AvroDataException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
