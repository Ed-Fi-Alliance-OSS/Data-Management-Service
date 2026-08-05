// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// How a statement obtains the root <c>DocumentId</c>: a value it binds, or a raw SQL expression emitted
/// where the bind marker stood, because the value is produced server-side by an earlier statement of the
/// same command and no client-side value exists yet.
/// </summary>
internal abstract record RelationalWriteRootDocumentIdSource
{
    public sealed record Bound(long DocumentId) : RelationalWriteRootDocumentIdSource;

    public sealed record Derived(string Sql) : RelationalWriteRootDocumentIdSource;
}

/// <summary>
/// Turns merged rows into the parameterized command a compiled write-plan statement expects. Shared by the
/// standalone per-statement write path and the co-batched DML command, so both bind the same values to the
/// same plan SQL and only their statement transport differs.
/// </summary>
internal static class RelationalWriteRowStatements
{
    public static RelationalCommand BuildRowCommand(
        TableWritePlan tableWritePlan,
        string sql,
        RelationalWriteMergedTableRow row,
        RelationalWriteRootDocumentIdSource rootDocumentId,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings
    )
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(row);

        List<RelationalParameter> parameters = new(tableWritePlan.ColumnBindings.Length);
        InlinedValueSubstitutions substitutions = new(tableWritePlan, rootDocumentId);

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            var parameterName = NormalizeParameterName(
                tableWritePlan.ColumnBindings[bindingIndex].ParameterName
            );

            if (substitutions.TryInline(parameterName, row.Values[bindingIndex], collectionItemIdBindings))
            {
                continue;
            }

            parameters.Add(
                new RelationalParameter(
                    parameterName,
                    ResolveParameterValue(
                        tableWritePlan,
                        row.Values[bindingIndex],
                        rootDocumentId,
                        collectionItemIdBindings
                    )
                )
            );
        }

        return new RelationalCommand(substitutions.Apply(sql), parameters);
    }

    public static RelationalCommand BuildBatchCommand(
        string sql,
        TableWritePlan tableWritePlan,
        IReadOnlyList<RelationalWriteMergedTableRow> rows,
        int rowOffset,
        int rowCount,
        RelationalWriteRootDocumentIdSource rootDocumentId,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings
    )
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(rows);

        List<RelationalParameter> parameters = new(rowCount * tableWritePlan.ColumnBindings.Length);
        InlinedValueSubstitutions substitutions = new(tableWritePlan, rootDocumentId);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = rows[rowOffset + rowIndex];

            for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
            {
                var parameterName = NormalizeParameterName(
                    WriteBatchSqlSupport.BuildBatchParameterName(
                        tableWritePlan.ColumnBindings[bindingIndex].ParameterName,
                        rowIndex
                    )
                );

                if (
                    substitutions.TryInline(parameterName, row.Values[bindingIndex], collectionItemIdBindings)
                )
                {
                    continue;
                }

                parameters.Add(
                    new RelationalParameter(
                        parameterName,
                        ResolveParameterValue(
                            tableWritePlan,
                            row.Values[bindingIndex],
                            rootDocumentId,
                            collectionItemIdBindings
                        )
                    )
                );
            }
        }

        return new RelationalCommand(substitutions.Apply(sql), parameters);
    }

    private static object? ResolveParameterValue(
        TableWritePlan tableWritePlan,
        FlattenedWriteValue value,
        RelationalWriteRootDocumentIdSource rootDocumentId,
        RelationalWriteCollectionItemIdBindings collectionItemIdBindings
    )
    {
        ArgumentNullException.ThrowIfNull(collectionItemIdBindings);

        return value switch
        {
            FlattenedWriteValue.Literal(var literalValue) => literalValue,
            FlattenedWriteValue.UnresolvedRootDocumentId
                when rootDocumentId is RelationalWriteRootDocumentIdSource.Bound bound => bound.DocumentId,
            FlattenedWriteValue.UnresolvedRootDocumentId => throw new InvalidOperationException(
                $"Table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' asked to bind the root "
                    + "DocumentId, but the statement derives it from an expression. Every unresolved root "
                    + "DocumentId must have been substituted before binding."
            ),
            FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId
                when collectionItemIdBindings.TryGetReservedValue(
                    unresolvedCollectionItemId,
                    out var reservedCollectionItemId
                ) => reservedCollectionItemId,
            FlattenedWriteValue.UnresolvedCollectionItemId => throw new InvalidOperationException(
                $"Table '{RelationalWriteFlattener.FormatTable(tableWritePlan)}' still contains an "
                    + "unresolved CollectionItemId. CollectionItemId reservation must complete before this "
                    + "row can be written."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };
    }

    private static string NormalizeParameterName(string parameterName) =>
        parameterName.StartsWith('@') ? parameterName : $"@{parameterName}";

    /// <summary>
    /// Collects one statement's parameter substitutions, so a value the database produces server-side is
    /// emitted as an expression where the bind marker stood instead of being bound: a collection key from
    /// the sequence, or the root document id derived from an earlier statement of the same command.
    /// </summary>
    private sealed class InlinedValueSubstitutions(
        TableWritePlan tableWritePlan,
        RelationalWriteRootDocumentIdSource rootDocumentId
    )
    {
        private readonly Dictionary<string, string> _replacementsByBareName = new(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>
        /// Only a table that preallocates its own collection key can have an inlinable collection key, so
        /// unless the root document id is derived too, every other statement — root rows, updates, deletes —
        /// skips recording altogether rather than building a replacement map its SQL is never rewritten with.
        /// </summary>
        private readonly bool _canInline =
            tableWritePlan.CollectionKeyPreallocationPlan is not null
            || rootDocumentId is RelationalWriteRootDocumentIdSource.Derived;

        private bool _hasInlinedParameter;

        /// <summary>
        /// Records how <paramref name="parameterName"/> is emitted and reports whether the caller should
        /// skip binding it. Every parameter is recorded, including the ones that stay bound, because the
        /// rewrite must be able to explain every token it meets.
        /// </summary>
        public bool TryInline(
            string parameterName,
            FlattenedWriteValue value,
            RelationalWriteCollectionItemIdBindings collectionItemIdBindings
        )
        {
            if (!_canInline)
            {
                return false;
            }

            var bareName = RelationalParameterTokenRewriter.BareName(parameterName);

            if (TryGetInlineExpression(value, collectionItemIdBindings) is { } expression)
            {
                _replacementsByBareName[bareName] = expression;
                _hasInlinedParameter = true;

                return true;
            }

            _replacementsByBareName[bareName] = parameterName;

            return false;
        }

        private string? TryGetInlineExpression(
            FlattenedWriteValue value,
            RelationalWriteCollectionItemIdBindings collectionItemIdBindings
        ) =>
            value switch
            {
                FlattenedWriteValue.UnresolvedRootDocumentId
                    when rootDocumentId is RelationalWriteRootDocumentIdSource.Derived derived => derived.Sql,
                FlattenedWriteValue.UnresolvedCollectionItemId unresolvedCollectionItemId
                    when collectionItemIdBindings.IsInlined(unresolvedCollectionItemId) =>
                    collectionItemIdBindings.SequenceExpression,
                _ => null,
            };

        /// <summary>
        /// Rewrites the statement only when something was inlined, so a statement that binds every one of
        /// its parameters keeps the plan-compiled SQL byte for byte.
        /// </summary>
        public string Apply(string sql) =>
            _hasInlinedParameter
                ? RelationalParameterTokenRewriter.Rewrite(sql, _replacementsByBareName)
                : sql;
    }
}
