using System.Text;

namespace AvroSharp.CodeGen;

/// <summary>Builds indented source text with <c>\n</c> line endings, so output is identical on every platform.</summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _text = new();
    private int _indent;

    public void Line(string line = "")
    {
        if (line.Length > 0)
        {
            _text.Append(' ', _indent * 4).Append(line);
        }

        _text.Append('\n');
    }

    /// <summary>A preprocessor directive, written at the start of the line.</summary>
    public void Directive(string directive) => _text.Append(directive).Append('\n');

    public void Open(string? header = null)
    {
        if (header is not null)
        {
            Line(header);
        }

        Line("{");
        _indent++;
    }

    public void Close(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
    }

    public void Indent() => _indent++;

    public void Outdent() => _indent--;

    public override string ToString() => _text.ToString();
}
