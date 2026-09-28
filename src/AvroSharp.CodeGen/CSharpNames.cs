using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AvroSharp.Schemas;

namespace AvroSharp.CodeGen;

/// <summary>Maps Avro names to C# identifiers and literals.</summary>
internal sealed class CSharpNames(CodeGenOptions options)
{
    private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>Escapes a C# keyword with <c>@</c>. Avro names are already valid C# identifier characters.</summary>
    public static string Identifier(string name) => s_keywords.Contains(name) ? "@" + name : name;

    /// <summary>
    /// Converts <c>first_name</c>, <c>firstName</c> or <c>FIRST_NAME</c> to <c>FirstName</c>. A segment in capitals
    /// (<c>USER</c>, <c>ID</c>, <c>HTTP2</c>) is title-cased; a segment with lower-case letters keeps its inner capitals.
    /// </summary>
    public static string Pascal(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var part in name.Split('_').Where(part => part.Length > 0))
        {
            var rest = part[1..];
            if (part.Any(char.IsLetter) && !part.Any(char.IsLower))
            {
#pragma warning disable CA1308 // An identifier in capitals becomes title case, not a normalized comparison key.
                rest = rest.ToLowerInvariant();
#pragma warning restore CA1308
            }

            result.Append(char.ToUpperInvariant(part[0])).Append(rest);
        }

        return result.Length == 0 || char.IsDigit(result[0]) ? "Field" + result : result.ToString();
    }

    /// <summary>A C# string literal.</summary>
    public static string Literal(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    result.Append("\\\"");
                    break;
                case '\\':
                    result.Append("\\\\");
                    break;
                case '\n':
                    result.Append("\\n");
                    break;
                case '\r':
                    result.Append("\\r");
                    break;
                case '\t':
                    result.Append("\\t");
                    break;
                default:
                    if (c < ' ' || char.IsSurrogate(c) || c > '~')
                    {
                        result.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        result.Append(c);
                    }

                    break;
            }
        }

        return result.Append('"').ToString();
    }

    /// <summary>Escapes text for an XML documentation comment.</summary>
    public static string Xml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Splits schema text (a <c>doc</c>) into lines that are safe inside <c>//</c> or <c>///</c> comments. C# ends a
    /// comment at any of its line terminators (\r, \n, U+0085, U+2028, U+2029), so each one starts a new line here;
    /// otherwise the text after it would be compiled as code. Other control characters, which are not valid in XML
    /// documentation, are dropped.
    /// </summary>
    /// <param name="text">The schema text.</param>
    public static IEnumerable<string> CommentLines(string text)
    {
        var line = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                yield return line.ToString();
                line.Clear();
            }
            else if (c == '\t' || !char.IsControl(c))
            {
                line.Append(c);
            }
        }

        yield return line.ToString();
    }

    /// <summary>Gets the C# namespace for a named type, or <see langword="null"/> for the global namespace.</summary>
    public string? Namespace(NamedSchema schema)
    {
        var ns = string.IsNullOrEmpty(schema.Name.Namespace) ? options.DefaultNamespace : schema.Name.Namespace;
        return string.IsNullOrEmpty(ns) ? null : string.Join(".", ns!.Split('.').Select(Identifier));
    }

    /// <summary>Gets the fully qualified C# name of a named type.</summary>
    public string TypeName(NamedSchema schema)
    {
        var ns = Namespace(schema);
        return "global::" + (ns is null ? string.Empty : ns + ".") + Identifier(schema.Name.Name);
    }
}
