// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
/// Rewriting is strict in both directions: <see cref="RelationalParameterTokenRewriter"/> fails on a
/// parameter token in the SQL that no declared parameter or substitution explains, and this adapter fails
/// on a declared parameter the SQL never references. Either mismatch means the standalone builder changed
/// shape underneath this adapter, which must surface as a loud error rather than a silently wrong command.
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
                substitutionsByBareName[RelationalParameterTokenRewriter.BareName(parameterName)] =
                    expression;
            }
        }

        List<RelationalParameter> rewrittenParameters = new(command.Parameters.Count);

        foreach (var parameter in command.Parameters)
        {
            var bareName = RelationalParameterTokenRewriter.BareName(parameter.Name);

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

        var rewrittenSql = RelationalParameterTokenRewriter.Rewrite(
            command.CommandText,
            replacementsByBareName,
            referencedBareNames
        );

        var unreferenced = command
            .Parameters.Select(parameter => RelationalParameterTokenRewriter.BareName(parameter.Name))
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
}
