// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Resolves grouped logical reference fields for one reference site and validates that grouped members converge to
/// one projected value.
/// </summary>
internal static class ReferenceIdentityProjectionLogicalFieldResolver
{
    public static IReadOnlyList<ResolvedReferenceIdentityProjectionLogicalField> ResolveOrThrow(
        DbTableModel tableModel,
        DocumentReferenceBinding binding,
        Func<string, Exception> createException
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(createException);

        List<ResolvedReferenceIdentityProjectionLogicalField> logicalFields = [];

        if (binding.IdentityBindings.Count == 0)
        {
            throw createException(
                $"reference identity projection binding '{binding.ReferenceObjectPath.Canonical}' on table '{tableModel.Table}' does not contain any identity bindings"
            );
        }

        foreach (var logicalFieldGroup in binding.GetLogicalFieldGroups())
        {
            List<DbColumnName> memberColumnsInOrder = [];
            DbColumnName? representativeBindingColumn = null;
            DbColumnName? storageColumn = null;
            DbColumnName? presenceColumn = null;
            var containsUnifiedAlias = false;

            foreach (var memberColumnName in logicalFieldGroup.MemberColumns)
            {
                var memberColumn = ProjectionMetadataResolver.ResolveTableColumnOrThrow(
                    tableModel,
                    memberColumnName,
                    missingColumn =>
                        createException(
                            $"reference identity projection binding '{binding.ReferenceObjectPath.Canonical}' on table '{tableModel.Table}' grouped logical field '{logicalFieldGroup.ReferenceJsonPath.Canonical}' references missing member column '{missingColumn.Value}'"
                        )
                );

                ValidateGroupedReferenceIdentityBindingPathOrThrow(
                    tableModel.Table,
                    memberColumn,
                    binding.ReferenceObjectPath,
                    logicalFieldGroup.ReferenceJsonPath,
                    createException
                );

                var resolvedBindingColumn = ResolveBindingColumnMetadataOrThrow(
                    tableModel,
                    memberColumn,
                    binding.ReferenceObjectPath,
                    logicalFieldGroup.ReferenceJsonPath,
                    createException
                );

                representativeBindingColumn ??= memberColumnName;
                memberColumnsInOrder.Add(memberColumnName);

                if (storageColumn is null)
                {
                    storageColumn = resolvedBindingColumn.StorageColumn;
                    presenceColumn = resolvedBindingColumn.PresenceColumn;
                    containsUnifiedAlias = resolvedBindingColumn.UsesUnifiedAlias;
                    continue;
                }

                if (!storageColumn.Equals(resolvedBindingColumn.StorageColumn))
                {
                    var representativeBindingColumnName = representativeBindingColumn.GetValueOrDefault();
                    var storageColumnName = storageColumn.GetValueOrDefault();

                    throw createException(
                        $"reference identity projection binding '{binding.ReferenceObjectPath.Canonical}' on table '{tableModel.Table}' grouped logical field '{logicalFieldGroup.ReferenceJsonPath.Canonical}' resolves to multiple storage columns: "
                            + $"'{representativeBindingColumnName.Value}' -> '{storageColumnName.Value}', "
                            + $"'{memberColumnName.Value}' -> '{resolvedBindingColumn.StorageColumn.Value}'"
                    );
                }

                if (!Equals(presenceColumn, resolvedBindingColumn.PresenceColumn))
                {
                    var representativeBindingColumnName = representativeBindingColumn.GetValueOrDefault();

                    throw createException(
                        $"reference identity projection binding '{binding.ReferenceObjectPath.Canonical}' on table '{tableModel.Table}' grouped logical field '{logicalFieldGroup.ReferenceJsonPath.Canonical}' resolves to multiple presence columns: "
                            + $"'{representativeBindingColumnName.Value}' -> '{FormatPresenceColumn(presenceColumn)}', "
                            + $"'{memberColumnName.Value}' -> '{FormatPresenceColumn(resolvedBindingColumn.PresenceColumn)}'"
                    );
                }

                containsUnifiedAlias |= resolvedBindingColumn.UsesUnifiedAlias;
            }

            if (representativeBindingColumn is null)
            {
                // storageColumn is set in the same loop iteration as representativeBindingColumn,
                // so a null representativeBindingColumn implies no member columns were processed.
                throw createException(
                    $"reference identity projection binding '{binding.ReferenceObjectPath.Canonical}' on table '{tableModel.Table}' grouped logical field '{logicalFieldGroup.ReferenceJsonPath.Canonical}' does not contain any member columns"
                );
            }

            if (containsUnifiedAlias && !Equals(presenceColumn, binding.FkColumn))
            {
                throw createException(
                    $"reference identity projection binding '{binding.ReferenceObjectPath.Canonical}' on table '{tableModel.Table}' grouped logical field '{logicalFieldGroup.ReferenceJsonPath.Canonical}' resolves alias presence column '{FormatPresenceColumn(presenceColumn)}', "
                        + $"but owning reference FK column is '{binding.FkColumn.Value}'"
                );
            }

            var representativeBindingColumnValue = representativeBindingColumn.Value;
            var storageColumnValue = storageColumn!.Value;

            logicalFields.Add(
                new ResolvedReferenceIdentityProjectionLogicalField(
                    ReferenceJsonPath: logicalFieldGroup.ReferenceJsonPath,
                    RepresentativeBindingColumn: representativeBindingColumnValue,
                    MemberColumnsInOrder: memberColumnsInOrder,
                    StorageColumn: storageColumnValue
                )
            );
        }

        if (logicalFields.Count == 0)
        {
            throw createException(
                $"reference identity projection binding '{binding.ReferenceObjectPath.Canonical}' on table '{tableModel.Table}' resolves to zero logical fields after grouping identity bindings"
            );
        }

        return logicalFields;
    }

    private static void ValidateGroupedReferenceIdentityBindingPathOrThrow(
        DbTableName table,
        DbColumnModel columnModel,
        JsonPathExpression referenceObjectPath,
        JsonPathExpression referenceJsonPath,
        Func<string, Exception> createException
    )
    {
        if (columnModel.SourceJsonPath?.Canonical == referenceJsonPath.Canonical)
        {
            return;
        }

        throw createException(
            $"reference identity projection binding '{referenceObjectPath.Canonical}' on table '{table}' grouped logical field '{referenceJsonPath.Canonical}' member column '{columnModel.ColumnName.Value}' has DbColumnModel.SourceJsonPath '{columnModel.SourceJsonPath?.Canonical ?? "<null>"}', "
                + $"which does not match grouped ReferenceJsonPath '{referenceJsonPath.Canonical}'"
        );
    }

    private static ResolvedReferenceIdentityProjectionBindingColumn ResolveBindingColumnMetadataOrThrow(
        DbTableModel tableModel,
        DbColumnModel bindingColumn,
        JsonPathExpression referenceObjectPath,
        JsonPathExpression referenceJsonPath,
        Func<string, Exception> createException
    )
    {
        var contextDescription =
            $"reference identity projection binding '{referenceObjectPath.Canonical}' on table '{tableModel.Table}' grouped logical field '{referenceJsonPath.Canonical}' member column '{bindingColumn.ColumnName.Value}'";

        return bindingColumn.Storage switch
        {
            ColumnStorage.Stored => new ResolvedReferenceIdentityProjectionBindingColumn(
                StorageColumn: bindingColumn.ColumnName,
                PresenceColumn: null,
                UsesUnifiedAlias: false
            ),
            ColumnStorage.UnifiedAlias unifiedAlias => new ResolvedReferenceIdentityProjectionBindingColumn(
                StorageColumn: ResolveStoredCanonicalColumnOrThrow(
                    tableModel,
                    bindingColumn.ColumnName,
                    unifiedAlias.CanonicalColumn,
                    contextDescription,
                    createException
                ),
                PresenceColumn: unifiedAlias.PresenceColumn,
                UsesUnifiedAlias: true
            ),
            _ => throw createException(
                $"{contextDescription} uses unsupported storage metadata '{bindingColumn.Storage.GetType().Name}'"
            ),
        };
    }

    private static DbColumnName ResolveStoredCanonicalColumnOrThrow(
        DbTableModel tableModel,
        DbColumnName aliasColumnName,
        DbColumnName canonicalColumnName,
        string contextDescription,
        Func<string, Exception> createException
    )
    {
        var canonicalColumn = ProjectionMetadataResolver.ResolveTableColumnOrThrow(
            tableModel,
            canonicalColumnName,
            missingColumn =>
                createException(
                    $"{contextDescription} resolves to missing canonical storage column '{missingColumn.Value}'"
                )
        );

        if (canonicalColumn.Storage is ColumnStorage.Stored)
        {
            return canonicalColumnName;
        }

        if (canonicalColumn.Storage is ColumnStorage.UnifiedAlias)
        {
            throw createException(
                $"{contextDescription} resolves to canonical alias column '{canonicalColumnName.Value}'. "
                    + $"Transitive {nameof(ColumnStorage.UnifiedAlias)} resolution is not supported for alias column '{aliasColumnName.Value}' -> '{canonicalColumnName.Value}'"
            );
        }

        throw createException(
            $"{contextDescription} resolves to canonical storage column '{canonicalColumnName.Value}', but that column is not stored"
        );
    }

    private static string FormatPresenceColumn(DbColumnName? presenceColumn) =>
        presenceColumn?.Value ?? "<none>";
}

internal sealed record ResolvedReferenceIdentityProjectionLogicalField(
    JsonPathExpression ReferenceJsonPath,
    DbColumnName RepresentativeBindingColumn,
    IReadOnlyList<DbColumnName> MemberColumnsInOrder,
    DbColumnName StorageColumn
);

internal sealed record ResolvedReferenceIdentityProjectionBindingColumn(
    DbColumnName StorageColumn,
    DbColumnName? PresenceColumn,
    bool UsesUnifiedAlias
);
