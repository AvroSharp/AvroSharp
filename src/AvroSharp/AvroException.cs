using System;

namespace AvroSharp;

/// <summary>The base class for errors raised by AvroSharp.</summary>
public class AvroException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AvroException"/> class.</summary>
    public AvroException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AvroException"/> class with a message.</summary>
    /// <param name="message">The error message.</param>
    public AvroException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AvroException"/> class with a message and an inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public AvroException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
