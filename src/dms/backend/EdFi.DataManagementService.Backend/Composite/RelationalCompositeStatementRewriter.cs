// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Backend.Composite;

/// <summary>
/// A statement rewritten for co-batching: the same SQL its standalone builder produced, with every
/// parameter renamed through the composite command's allocator and any substituted parameter replaced
/// by a raw SQL expression.
/// </summary>
/// <param name="Sql">The rewritten statement SQL.</param>
/// <param name="Parameters">The renamed parameters, in the original binding order minus substitutions.</param>
internal sealed record RelationalCompositeRewrittenStatement(
    string Sql,
    IReadOnlyList<RelationalParameter> Parameters
);

/// <summary>
/// Adapts a <see cref="RelationalCommand"/> built by an existing standalone SQL builder so it can join a
/// composite command without duplicating any SQL construction.
/// </summary>
/// <remarks>
/// <para>
/// Standalone builders emit fixed parameter names (<c>@documentId</c>, <c>@p0</c>, ...), which would
/// collide across co-batched statements and are rejected by
/// <see cref="RelationalCompositeCommandBuilder"/>'s allocator guard. Rewriting renames each declared
/// parameter to an allocator-issued name and rewrites the SQL token-for-token, so the SQL itself keeps a
/// single source of truth in the standalone builder.
/// </para>
/// <para>
/// A substitution replaces a parameter reference with a raw SQL expression — the provider carrier's
/// captured-target expression — and drops the parameter from the bindings. That is how a statement built
/// against a client-known value consumes the in-command captured decision instead.
/// </para>
/// <para>
/// Rewriting is strict in both directions: a parameter token in the SQL that no declared parameter or
/// substitution explains fails the build, and a declared parameter the SQL never references fails the
/// build. Either mismatch means the standalone builder changed shape underneath this adapter, which must
/// surface as a loud error rather than a silently wrong command.
/// </para>
/// </remarks>
internal static class RelationalCompositeStatementRewriter
{
    public static RelationalCompositeRewrittenStatement Rewrite(
        RelationalCommand command,
        RelationalCompositeParameterAllocator allocator,
        int statementOrdinal,
        IReadOnlyDictionary<string, string>? parameterExpressionSubstitutions = null
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfNegative(statementOrdinal);

        Dictionary<string, string> replacementsByBareName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> substitutionsByBareName = new(StringComparer.OrdinalIgnoreCase);

        if (parameterExpressionSubstitutions is not null)
        {
            foreach (var (parameterName, expression) in parameterExpressionSubstitutions)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(expression);
                substitutionsByBareName[BareName(parameterName)] = expression;
            }
        }

        List<RelationalParameter> rewrittenParameters = new(command.Parameters.Count);

        foreach (var parameter in command.Parameters)
        {
            var bareName = BareName(parameter.Name);

            if (substitutionsByBareName.TryGetValue(bareName, out var expression))
            {
                replacementsByBareName[bareName] = expression;
                continue;
            }

            var issuedName = allocator.AllocateStatementScoped(bareName, statementOrdinal);
            replacementsByBareName[bareName] = issuedName;
            rewrittenParameters.Add(
                new RelationalParameter(issuedName, parameter.Value, parameter.ConfigureParameter)
            );
        }

        foreach (var (substitutedName, expression) in substitutionsByBareName)
        {
            // A substitution for a parameter the command never declared is still legal when the SQL
            // references the token directly; record it so the token rewrite below can resolve it.
            replacementsByBareName.TryAdd(substitutedName, expression);
        }

        HashSet<string> referencedBareNames = new(StringComparer.OrdinalIgnoreCase);

        var rewrittenSql = RewriteParameterTokens(
            command.CommandText,
            replacementsByBareName,
            referencedBareNames
        );

        var unreferenced = command
            .Parameters.Select(parameter => BareName(parameter.Name))
            .FirstOrDefault(bareName => !referencedBareNames.Contains(bareName));

        if (unreferenced is not null)
        {
            throw new InvalidOperationException(
                $"Command declares parameter '@{unreferenced}' but its SQL never references it, so the "
                    + "rewritten statement would bind a dangling parameter."
            );
        }

        return new RelationalCompositeRewrittenStatement(rewrittenSql, rewrittenParameters);
    }

    private static string BareName(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        return parameterName.TrimStart('@');
    }

    /// <summary>
    /// Rewrites executable parameter tokens while preserving every lexical region in which <c>@name</c>
    /// is data rather than a bind marker: string literals, quoted identifiers, SQL Server bracketed
    /// identifiers and system variables, PostgreSQL dollar-quoted strings, and line/block comments.
    /// This is deliberately a small SQL lexer rather than a regular expression; treating a payload or
    /// comment as executable SQL would either corrupt the statement or fabricate an undeclared parameter.
    /// </summary>
    private static string RewriteParameterTokens(
        string sql,
        IReadOnlyDictionary<string, string> replacementsByBareName,
        ISet<string> referencedBareNames
    )
    {
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
                            + "by the command nor substituted. The standalone builder and this rewrite "
                            + "have drifted apart."
                    );
                }

                referencedBareNames.Add(bareName);
                rewritten.Append(replacement);
                index = end;
                continue;
            }

            rewritten.Append(current);
            index++;
        }

        return rewritten.ToString();
    }

    private static int AppendDelimited(
        string sql,
        int start,
        char delimiter,
        StringBuilder rewritten
    )
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

    private static bool TryGetDollarQuoteDelimiter(
        string sql,
        int start,
        out string delimiter
    )
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
