// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Resolves unified-alias storage metadata and classifies unified-alias presence gates.
/// </summary>
internal static class UnifiedAliasStorageResolver
{
    /// <summary>
    /// Defines which presence-gated columns are rejected for a given storage-resolution usage.
    /// </summary>
    internal enum PresenceGateRejectionPolicy
    {
        /// <summary>
        /// Reject synthetic scalar presence flags (e.g., <c>..._Present</c>), but allow reference-site
        /// <c>..._DocumentId</c> presence gates.
        /// </summary>
        RejectSyntheticScalarPresence,

        /// <summary>
        /// Reject all presence-gate columns, including both reference-site <c>..._DocumentId</c> columns and
        /// synthetic scalar presence flags.
        /// </summary>
        RejectAllPresenceGates,
    }

    /// <summary>
    /// Options that control strict validation behavior while building unified-alias metadata.
    /// </summary>
    /// <param name="ThrowIfPresenceColumnMissing">
    /// When true, throws if a unified-alias column references a presence-gate column that does not exist on the table.
    /// </param>
    /// <param name="ThrowIfInvalidStrictSyntheticCandidate">
    /// When true, throws if a referenced presence-gate column is not a valid strict synthetic presence flag.
    /// </param>
    internal readonly record struct PresenceGateMetadataOptions(
        bool ThrowIfPresenceColumnMissing,
        bool ThrowIfInvalidStrictSyntheticCandidate
    );

    /// <summary>
    /// Cached per-table metadata used for storage-column resolution and presence-gate validation.
    /// </summary>
    /// <param name="Table">The owning table name.</param>
    /// <param name="ColumnsByName">All columns keyed by column name.</param>
    /// <param name="AllPresenceGateColumns">All presence-gate columns referenced by unified aliases.</param>
    /// <param name="SyntheticScalarPresenceColumns">Presence-gate columns that are strict synthetic scalar flags.</param>
    internal sealed record TableMetadata(
        DbTableName Table,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> ColumnsByName,
        IReadOnlySet<DbColumnName> AllPresenceGateColumns,
        IReadOnlySet<DbColumnName> SyntheticScalarPresenceColumns
    );

    /// <summary>
    /// Builds table metadata used by storage resolution and presence-gate validation.
    /// </summary>
    public static TableMetadata BuildTableMetadata(DbTableModel table, PresenceGateMetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(table);

        var columnsByName = table.Columns.ToDictionary(column => column.ColumnName, column => column);
        HashSet<DbColumnName> allPresenceGateColumns = [];
        HashSet<DbColumnName> syntheticScalarPresenceColumns = [];

        foreach (var column in table.Columns)
        {
            if (column.Storage is not ColumnStorage.UnifiedAlias { PresenceColumn: { } presenceColumn })
            {
                continue;
            }

            allPresenceGateColumns.Add(presenceColumn);

            if (!columnsByName.TryGetValue(presenceColumn, out var presenceColumnModel))
            {
                if (!options.ThrowIfPresenceColumnMissing)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Unified alias column '{column.ColumnName.Value}' on table '{table.Table}' references "
                        + $"unknown presence-gate column '{presenceColumn.Value}'."
                );
            }

            if (IsReferenceSitePresenceGate(presenceColumnModel))
            {
                if (presenceColumnModel.Storage is not ColumnStorage.Stored)
                {
                    throw new InvalidOperationException(
                        $"Unified alias column '{column.ColumnName.Value}' on table '{table.Table}' references "
                            + $"invalid reference presence-gate column '{presenceColumn.Value}'. Reference "
                            + "presence-gate columns must be stored."
                    );
                }

                continue;
            }

            if (IsStrictSyntheticPresenceFlag(presenceColumnModel))
            {
                syntheticScalarPresenceColumns.Add(presenceColumn);
                continue;
            }

            if (
                options.ThrowIfInvalidStrictSyntheticCandidate
                && TryGetStrictSyntheticPresenceViolation(presenceColumnModel, out var strictViolationReason)
            )
            {
                throw new InvalidOperationException(
                    $"Unified alias column '{column.ColumnName.Value}' on table '{table.Table}' references "
                        + $"invalid synthetic presence column '{presenceColumn.Value}'. Synthetic presence flags "
                        + $"must be nullable stored scalar booleans with null source path ({strictViolationReason})."
                );
            }
        }

        return new TableMetadata(
            table.Table,
            columnsByName,
            allPresenceGateColumns,
            syntheticScalarPresenceColumns
        );
    }

    /// <summary>
    /// Resolves a column to its canonical stored column and validates presence-gate usage.
    /// </summary>
    public static DbColumnName ResolveStorageColumn(
        DbColumnName column,
        TableMetadata tableMetadata,
        PresenceGateRejectionPolicy presenceGateRejectionPolicy,
        string contextDescription,
        string columnRole,
        string usageDescription
    )
    {
        ArgumentNullException.ThrowIfNull(tableMetadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(usageDescription);

        if (!tableMetadata.ColumnsByName.TryGetValue(column, out var columnModel))
        {
            throw new InvalidOperationException(
                $"{contextDescription} resolved {columnRole} '{column.Value}' that does not exist on "
                    + $"table '{tableMetadata.Table}'."
            );
        }

        if (IsRejectedPresenceGate(columnModel.ColumnName, tableMetadata, presenceGateRejectionPolicy))
        {
            throw new InvalidOperationException(
                $"{contextDescription} resolved {columnRole} '{columnModel.ColumnName.Value}' on table "
                    + $"'{tableMetadata.Table}' to a {DescribePresenceGateRejection(presenceGateRejectionPolicy)}, "
                    + $"which is not valid for {usageDescription}."
            );
        }

        switch (columnModel.Storage)
        {
            case ColumnStorage.Stored:
                return columnModel.ColumnName;
            case ColumnStorage.UnifiedAlias unifiedAlias:
                if (
                    !tableMetadata.ColumnsByName.TryGetValue(
                        unifiedAlias.CanonicalColumn,
                        out var canonicalColumn
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"{contextDescription} resolved {columnRole} '{columnModel.ColumnName.Value}' on table "
                            + $"'{tableMetadata.Table}' to missing canonical storage column "
                            + $"'{unifiedAlias.CanonicalColumn.Value}'."
                    );
                }

                if (
                    IsRejectedPresenceGate(
                        unifiedAlias.CanonicalColumn,
                        tableMetadata,
                        presenceGateRejectionPolicy
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"{contextDescription} resolved {columnRole} '{columnModel.ColumnName.Value}' on table "
                            + $"'{tableMetadata.Table}' to "
                            + $"{DescribePresenceGateRejection(presenceGateRejectionPolicy)} "
                            + $"'{unifiedAlias.CanonicalColumn.Value}', which is not valid for "
                            + $"{usageDescription}."
                    );
                }

                if (canonicalColumn.Storage is not ColumnStorage.Stored)
                {
                    throw new InvalidOperationException(
                        $"{contextDescription} resolved {columnRole} '{columnModel.ColumnName.Value}' on table "
                            + $"'{tableMetadata.Table}' to canonical column "
                            + $"'{unifiedAlias.CanonicalColumn.Value}' that is not stored."
                    );
                }

                return canonicalColumn.ColumnName;
            default:
                throw new InvalidOperationException(
                    $"{contextDescription} resolved {columnRole} '{columnModel.ColumnName.Value}' on table "
                        + $"'{tableMetadata.Table}' to unsupported storage metadata "
                        + $"'{columnModel.Storage.GetType().Name}'."
                );
        }
    }

    /// <summary>
    /// Identifies reference-site presence gates (<c>..._DocumentId</c>) by naming convention.
    /// </summary>
    private static bool IsReferenceSitePresenceGate(DbColumnModel column)
    {
        return RelationalNameConventions.IsDocumentIdColumn(column.ColumnName);
    }

    /// <summary>
    /// Returns true when the column is a strict synthetic presence flag (nullable stored scalar boolean with null source path).
    /// </summary>
    private static bool IsStrictSyntheticPresenceFlag(DbColumnModel column)
    {
        return !TryGetStrictSyntheticPresenceViolation(column, out _);
    }

    /// <summary>
    /// Determines whether a column violates strict synthetic presence-flag requirements and returns a reason when it does.
    /// </summary>
    private static bool TryGetStrictSyntheticPresenceViolation(
        DbColumnModel column,
        [NotNullWhen(true)] out string? reason
    )
    {
        if (column.Kind != ColumnKind.Scalar)
        {
            reason = $"kind '{column.Kind}' is not scalar";
            return true;
        }

        if (column.ScalarType?.Kind != ScalarKind.Boolean)
        {
            reason = $"scalar type '{column.ScalarType?.Kind}' is not boolean";
            return true;
        }

        if (!column.IsNullable)
        {
            reason = "column is not nullable";
            return true;
        }

        if (column.Storage is not ColumnStorage.Stored)
        {
            reason = $"storage '{column.Storage.GetType().Name}' is not stored";
            return true;
        }

        if (column.SourceJsonPath is not null)
        {
            reason = $"source path '{column.SourceJsonPath.Value.Canonical}' is not null";
            return true;
        }

        reason = null;
        return false;
    }

    /// <summary>
    /// Returns true when a column should be rejected as a presence gate for the specified policy.
    /// </summary>
    private static bool IsRejectedPresenceGate(
        DbColumnName column,
        TableMetadata tableMetadata,
        PresenceGateRejectionPolicy presenceGateRejectionPolicy
    )
    {
        return presenceGateRejectionPolicy switch
        {
            PresenceGateRejectionPolicy.RejectSyntheticScalarPresence =>
                tableMetadata.SyntheticScalarPresenceColumns.Contains(column),
            PresenceGateRejectionPolicy.RejectAllPresenceGates =>
                tableMetadata.AllPresenceGateColumns.Contains(column),
            _ => throw new InvalidOperationException(
                $"Unsupported presence-gate rejection policy '{presenceGateRejectionPolicy}'."
            ),
        };
    }

    /// <summary>
    /// Builds a human-readable description of a presence-gate rejection policy for diagnostics.
    /// </summary>
    private static string DescribePresenceGateRejection(
        PresenceGateRejectionPolicy presenceGateRejectionPolicy
    )
    {
        return presenceGateRejectionPolicy switch
        {
            PresenceGateRejectionPolicy.RejectSyntheticScalarPresence => "synthetic presence column",
            PresenceGateRejectionPolicy.RejectAllPresenceGates => "presence-gate column",
            _ => throw new InvalidOperationException(
                $"Unsupported presence-gate rejection policy '{presenceGateRejectionPolicy}'."
            ),
        };
    }
}
