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

        foreach (var logicalFieldGroup in binding.GetLogicalFieldGroups())
        {
            List<DbColumnName> memberColumnsInOrder = [];
            DbColumnName? representativeBindingColumn = null;
            DbColumnName? storageColumn = null;

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

                var resolvedStorageColumn = ResolveStorageColumnOrThrow(
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
                    storageColumn = resolvedStorageColumn;
                    continue;
                }

                if (storageColumn == resolvedStorageColumn)
                {
                    continue;
                }

                throw createException(
                    $"reference identity projection binding '{binding.ReferenceObjectPath.Canonical}' on table '{tableModel.Table}' grouped logical field '{logicalFieldGroup.ReferenceJsonPath.Canonical}' resolves to multiple storage columns: "
                        + $"'{representativeBindingColumn.Value}' -> '{storageColumn.Value}', "
                        + $"'{memberColumnName.Value}' -> '{resolvedStorageColumn.Value}'"
                );
            }

            if (representativeBindingColumn is null || storageColumn is null)
            {
                throw createException(
                    $"reference identity projection binding '{binding.ReferenceObjectPath.Canonical}' on table '{tableModel.Table}' grouped logical field '{logicalFieldGroup.ReferenceJsonPath.Canonical}' does not contain any member columns"
                );
            }

            var representativeBindingColumnValue = representativeBindingColumn.Value;
            var storageColumnValue = storageColumn.Value;

            logicalFields.Add(
                new ResolvedReferenceIdentityProjectionLogicalField(
                    ReferenceJsonPath: logicalFieldGroup.ReferenceJsonPath,
                    RepresentativeBindingColumn: representativeBindingColumnValue,
                    MemberColumnsInOrder: memberColumnsInOrder,
                    StorageColumn: storageColumnValue
                )
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

    private static DbColumnName ResolveStorageColumnOrThrow(
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
            ColumnStorage.Stored => bindingColumn.ColumnName,
            ColumnStorage.UnifiedAlias unifiedAlias => ResolveStoredCanonicalColumnOrThrow(
                tableModel,
                unifiedAlias.CanonicalColumn,
                contextDescription,
                createException
            ),
            _ => throw createException(
                $"{contextDescription} uses unsupported storage metadata '{bindingColumn.Storage.GetType().Name}'"
            ),
        };
    }

    private static DbColumnName ResolveStoredCanonicalColumnOrThrow(
        DbTableModel tableModel,
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

        throw createException(
            $"{contextDescription} resolves to canonical storage column '{canonicalColumnName.Value}', but that column is not stored"
        );
    }
}

internal sealed record ResolvedReferenceIdentityProjectionLogicalField(
    JsonPathExpression ReferenceJsonPath,
    DbColumnName RepresentativeBindingColumn,
    IReadOnlyList<DbColumnName> MemberColumnsInOrder,
    DbColumnName StorageColumn
);
