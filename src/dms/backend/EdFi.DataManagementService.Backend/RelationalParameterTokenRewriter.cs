// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Replaces executable <c>@name</c> bind markers in a statement with caller-supplied text, which is
/// either another parameter name or a raw SQL expression that takes the parameter's place.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a small SQL lexer rather than a regular expression. Every lexical region in
/// which <c>@name</c> is data rather than a bind marker is preserved verbatim: string literals, quoted
/// identifiers, SQL Server bracketed identifiers and system variables, PostgreSQL dollar-quoted strings,
/// and line and block comments. Treating a payload or a comment as executable SQL would either corrupt
/// the statement or fabricate an undeclared parameter.
/// </para>
/// <para>
/// Rewriting is strict: a parameter token the caller's map does not explain fails loudly, because an
/// unexplained token means the statement and the caller's binding list have drifted apart.
/// </para>
/// </remarks>
internal static class RelationalParameterTokenRewriter
{
    /// <summary>
    /// Rewrites every bind marker in <paramref name="sql"/> using <paramref name="replacementsByBareName"/>,
    /// keyed by parameter name without its leading <c>@</c>. Bare names of the markers actually
    /// encountered are added to <paramref name="referencedBareNames"/> when it is supplied, so a caller
    /// can detect a declared parameter the SQL never references.
    /// </summary>
    public static string Rewrite(
        string sql,
        IReadOnlyDictionary<string, string> replacementsByBareName,
        ISet<string>? referencedBareNames = null
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(replacementsByBareName);

        StringBuilder rewritten = new(sql.Length);

        var index = 0;

        while (index < sql.Length)
        {
            var current = sql[index];

            if (current is '\'' or '"')
            {
                index = AppendDelimited(sql, index, current, rewritten);
                continue;
            }

            if (current == '[')
            {
                index = AppendBracketedIdentifier(sql, index, rewritten);
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index = AppendLineComment(sql, index, rewritten);
                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index = AppendBlockComment(sql, index, rewritten);
                continue;
            }

            if (current == '$' && TryGetDollarQuoteDelimiter(sql, index, out var delimiter))
            {
                index = AppendDollarQuotedString(sql, index, delimiter, rewritten);
                continue;
            }

            if (
                current == '@'
                && index + 1 < sql.Length
                && IsParameterNameStart(sql[index + 1])
                && (index == 0 || (sql[index - 1] != '@' && !IsParameterNamePart(sql[index - 1])))
            )
            {
                var end = index + 2;

                while (end < sql.Length && IsParameterNamePart(sql[end]))
                {
                    end++;
                }

                var bareName = sql[(index + 1)..end];

                if (!replacementsByBareName.TryGetValue(bareName, out var replacement))
                {
                    throw new InvalidOperationException(
                        $"Statement SQL references parameter '@{bareName}', which is neither declared "
                            + "by the command nor substituted. The statement and this rewrite have drifted "
                            + "apart."
                    );
                }

                referencedBareNames?.Add(bareName);
                rewritten.Append(replacement);
                index = end;
                continue;
            }

            rewritten.Append(current);
            index++;
        }

        return rewritten.ToString();
    }

    /// <summary>Strips a leading <c>@</c> so callers can key replacements consistently.</summary>
    public static string BareName(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        return parameterName.TrimStart('@');
    }

    private static int AppendDelimited(string sql, int start, char delimiter, StringBuilder rewritten)
    {
        rewritten.Append(delimiter);

        var index = start + 1;

        while (index < sql.Length)
        {
            rewritten.Append(sql[index]);

            if (sql[index] != delimiter)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == delimiter)
            {
                rewritten.Append(sql[index + 1]);
                index += 2;
                continue;
            }

            return index + 1;
        }

        return sql.Length;
    }

    private static int AppendBracketedIdentifier(string sql, int start, StringBuilder rewritten)
    {
        rewritten.Append('[');

        var index = start + 1;

        while (index < sql.Length)
        {
            rewritten.Append(sql[index]);

            if (sql[index] != ']')
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == ']')
            {
                rewritten.Append(sql[index + 1]);
                index += 2;
                continue;
            }

            return index + 1;
        }

        return sql.Length;
    }

    private static int AppendLineComment(string sql, int start, StringBuilder rewritten)
    {
        var index = start;

        while (index < sql.Length)
        {
            var current = sql[index++];
            rewritten.Append(current);

            if (current is '\r' or '\n')
            {
                break;
            }
        }

        return index;
    }

    private static int AppendBlockComment(string sql, int start, StringBuilder rewritten)
    {
        var index = start;
        var depth = 0;

        while (index < sql.Length)
        {
            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                rewritten.Append("/*");
                index += 2;
                depth++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
            {
                rewritten.Append("*/");
                index += 2;
                depth--;

                if (depth == 0)
                {
                    break;
                }

                continue;
            }

            rewritten.Append(sql[index++]);
        }

        return index;
    }

    private static bool TryGetDollarQuoteDelimiter(string sql, int start, out string delimiter)
    {
        var end = start + 1;

        while (end < sql.Length && IsParameterNamePart(sql[end]))
        {
            end++;
        }

        if (end < sql.Length && sql[end] == '$')
        {
            delimiter = sql[start..(end + 1)];
            return true;
        }

        delimiter = string.Empty;
        return false;
    }

    private static int AppendDollarQuotedString(
        string sql,
        int start,
        string delimiter,
        StringBuilder rewritten
    )
    {
        var contentStart = start + delimiter.Length;
        var closing = sql.IndexOf(delimiter, contentStart, StringComparison.Ordinal);

        if (closing < 0)
        {
            rewritten.Append(sql.AsSpan(start));
            return sql.Length;
        }

        var end = closing + delimiter.Length;
        rewritten.Append(sql.AsSpan(start, end - start));
        return end;
    }

    private static bool IsParameterNameStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsParameterNamePart(char value) => char.IsLetterOrDigit(value) || value == '_';
}
