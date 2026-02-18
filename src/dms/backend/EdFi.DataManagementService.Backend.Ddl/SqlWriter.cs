// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// A deterministic, canonicalized SQL writer with indentation management.
/// Produces stable, byte-for-byte identical output for identical inputs.
/// </summary>
/// <remarks>
/// Canonicalization rules:
/// <list type="bullet">
/// <item>Line endings: Unix-style LF only (\n)</item>
/// <item>Indentation: 4 spaces per level (no tabs)</item>
/// <item>No trailing whitespace on any line</item>
/// </list>
/// </remarks>
public sealed class SqlWriter
{
    private readonly StringBuilder _builder;
    private int _indentLevel;
    private bool _atLineStart = true;

    private const int SpacesPerIndent = 4;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlWriter"/> class.
    /// </summary>
    /// <param name="dialect">The SQL dialect to use for quoting and type rendering.</param>
    /// <param name="initialCapacity">Initial capacity of the internal buffer.</param>
    public SqlWriter(ISqlDialect dialect, int initialCapacity = 4096)
    {
        Dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _builder = new StringBuilder(initialCapacity);
    }

    /// <summary>
    /// Gets the SQL dialect used for quoting and type rendering.
    /// </summary>
    public ISqlDialect Dialect { get; }

    /// <summary>
    /// Creates an indented scope that increments indent on construction and decrements on disposal.
    /// </summary>
    /// <returns>An <see cref="IndentScope"/> that decrements indentation when disposed.</returns>
    public IndentScope Indent()
    {
        _indentLevel++;
        return new IndentScope(this);
    }

    /// <summary>
    /// Appends text without adding a newline. Applies indentation if at line start.
    /// </summary>
    /// <param name="text">The text to append.</param>
    /// <returns>This writer for method chaining.</returns>
    public SqlWriter Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return this;
        }

        WriteIndentIfNeeded();
        _builder.Append(text);
        return this;
    }

    /// <summary>
    /// Appends text followed by a newline.
    /// </summary>
    /// <param name="text">The text to append.</param>
    /// <returns>This writer for method chaining.</returns>
    public SqlWriter AppendLine(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            WriteIndentIfNeeded();
            _builder.Append(text);
        }

        _builder.Append('\n');
        _atLineStart = true;
        return this;
    }

    /// <summary>
    /// Appends an empty newline.
    /// </summary>
    /// <returns>This writer for method chaining.</returns>
    public SqlWriter AppendLine()
    {
        _builder.Append('\n');
        _atLineStart = true;
        return this;
    }

    /// <summary>
    /// Appends a quoted identifier using the dialect's quoting rules.
    /// </summary>
    /// <param name="identifier">The identifier to quote and append.</param>
    /// <returns>This writer for method chaining.</returns>
    public SqlWriter AppendQuoted(string identifier)
    {
        WriteIndentIfNeeded();
        _builder.Append(Dialect.QuoteIdentifier(identifier));
        return this;
    }

    /// <summary>
    /// Appends a qualified table name (schema.table) with quoting.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <returns>This writer for method chaining.</returns>
    public SqlWriter AppendTable(DbTableName table)
    {
        WriteIndentIfNeeded();
        _builder.Append(Dialect.QualifyTable(table));
        return this;
    }

    /// <summary>
    /// Appends a column type definition.
    /// </summary>
    /// <param name="scalarType">The scalar type metadata.</param>
    /// <returns>This writer for method chaining.</returns>
    public SqlWriter AppendColumnType(RelationalScalarType scalarType)
    {
        WriteIndentIfNeeded();
        _builder.Append(Dialect.RenderColumnType(scalarType));
        return this;
    }

    /// <summary>
    /// Returns the canonical SQL output with Unix line endings and no trailing whitespace.
    /// </summary>
    /// <returns>The canonical SQL string.</returns>
    public override string ToString()
    {
        return Canonicalize(_builder.ToString());
    }

    /// <summary>
    /// Gets the current length of the internal buffer.
    /// </summary>
    public int Length => _builder.Length;

    /// <summary>
    /// Clears the internal buffer and resets indentation.
    /// </summary>
    public void Clear()
    {
        _builder.Clear();
        _indentLevel = 0;
        _atLineStart = true;
    }

    /// <summary>
    /// Decrements the indentation level. Called by <see cref="IndentScope"/> on disposal.
    /// </summary>
    internal void Outdent()
    {
        if (_indentLevel > 0)
        {
            _indentLevel--;
        }
    }

    /// <summary>
    /// Writes indentation spaces when starting a new line and indentation is non-zero.
    /// </summary>
    private void WriteIndentIfNeeded()
    {
        if (_atLineStart && _indentLevel > 0)
        {
            _builder.Append(' ', _indentLevel * SpacesPerIndent);
        }

        _atLineStart = false;
    }

    /// <summary>
    /// Applies canonicalization rules to the raw SQL text.
    /// Single-pass algorithm that normalizes line endings and removes trailing whitespace
    /// without intermediate array allocations.
    /// </summary>
    private static string Canonicalize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw ?? string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        var lineStart = 0;
        var lastNonWhitespaceInLine = -1;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            if (c == '\r')
            {
                // End of line - append content up to last non-whitespace
                if (lastNonWhitespaceInLine >= lineStart)
                {
                    builder.Append(raw, lineStart, lastNonWhitespaceInLine - lineStart + 1);
                }

                builder.Append('\n');

                // Skip \n if this is \r\n
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                {
                    i++;
                }

                lineStart = i + 1;
                lastNonWhitespaceInLine = -1;
            }
            else if (c == '\n')
            {
                // End of line - append content up to last non-whitespace
                if (lastNonWhitespaceInLine >= lineStart)
                {
                    builder.Append(raw, lineStart, lastNonWhitespaceInLine - lineStart + 1);
                }

                builder.Append('\n');
                lineStart = i + 1;
                lastNonWhitespaceInLine = -1;
            }
            else if (c != ' ' && c != '\t')
            {
                // Track position of last non-whitespace character on current line
                lastNonWhitespaceInLine = i;
            }
        }

        // Handle final line (if no trailing newline)
        if (lastNonWhitespaceInLine >= lineStart)
        {
            builder.Append(raw, lineStart, lastNonWhitespaceInLine - lineStart + 1);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Represents an indented scope that decrements indentation on disposal.
/// </summary>
public readonly struct IndentScope : IDisposable
{
    private readonly SqlWriter? _writer;

    /// <summary>
    /// Initializes a new indentation scope for the supplied writer.
    /// </summary>
    internal IndentScope(SqlWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Decrements the indentation level of the associated writer.
    /// </summary>
    public void Dispose()
    {
        _writer?.Outdent();
    }
}
