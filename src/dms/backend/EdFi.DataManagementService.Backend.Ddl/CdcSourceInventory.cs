// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

internal static class CdcSourceInventoryBuilder
{
    internal static IReadOnlyList<CdcSourceTableInventory> BuildExpectedSourceInventory(ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        return
        [
            BuildTable(CdcSourceTableKind.Document, DmsCoreTableDefinitions.Document(dialect), dialect),
            BuildTable(
                CdcSourceTableKind.DocumentCache,
                DmsCoreTableDefinitions.DocumentCache(dialect),
                dialect
            ),
            BuildTable(
                CdcSourceTableKind.CdcHeartbeat,
                DmsCoreTableDefinitions.CdcHeartbeat(dialect),
                dialect
            ),
        ];
    }

    private static CdcSourceTableInventory BuildTable(
        CdcSourceTableKind tableKind,
        DmsCoreTableDefinition table,
        ISqlDialect dialect
    ) =>
        new(
            tableKind,
            table.TableName,
            dialect.QualifyTable(table.TableName),
            table
                .Columns.Select(
                    (column, index) =>
                        new CdcSourceColumnInventory(
                            column.ColumnName,
                            dialect.QuoteIdentifier(column.ColumnName.Value),
                            index + 1,
                            column.SqlType,
                            column.IsNullable
                        )
                )
                .ToArray()
        );
}

internal static class CdcSourceInventoryValidator
{
    internal static IReadOnlyList<CdcProviderDiagnostic> ValidateLiveSourceInventory(
        IReadOnlyList<CdcSourceTableInventory> expectedSourceInventory,
        IReadOnlyList<CdcSourceTableInventory> observedSourceInventory
    )
    {
        var expected = CdcSourceInventoryContract.ValidateRequiredSourceInventory(
            expectedSourceInventory,
            nameof(expectedSourceInventory)
        );
        ArgumentNullException.ThrowIfNull(observedSourceInventory);

        List<CdcProviderDiagnostic> diagnostics = [];
        var observedByKind = observedSourceInventory
            .GroupBy(table => table.TableKind)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var expectedTable in expected)
        {
            if (!observedByKind.TryGetValue(expectedTable.TableKind, out var observedTables))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        code: "CDC_SOURCE_TABLE_MISSING",
                        category: CdcProviderDiagnosticCategory.MissingRequiredSourceObject,
                        artifactKind: CdcProviderArtifactKind.SourceTable,
                        safeName: SafeName(expectedTable.TableName),
                        expectedValue: "present",
                        observedValue: "missing"
                    )
                );
                continue;
            }

            if (observedTables.Length > 1)
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        code: "CDC_SOURCE_TABLE_DUPLICATE",
                        category: CdcProviderDiagnosticCategory.ValidationMismatch,
                        artifactKind: CdcProviderArtifactKind.SourceTable,
                        safeName: SafeName(expectedTable.TableName),
                        expectedValue: "one",
                        observedValue: observedTables.Length.ToString()
                    )
                );
                continue;
            }

            ValidateTable(expectedTable, observedTables[0], diagnostics);
        }

        foreach (var observedTable in observedSourceInventory)
        {
            if (CdcSourceInventoryContract.RequiredSourceTableKinds.Contains(observedTable.TableKind))
            {
                continue;
            }

            var isWorkTable = observedTable.TableName.Equals(DmsTableNames.DocumentProjectionWork);
            diagnostics.Add(
                BuildDiagnostic(
                    code: isWorkTable
                        ? "CDC_WORK_TABLE_SOURCE_INVENTORY_FORBIDDEN"
                        : "CDC_SOURCE_TABLE_UNEXPECTED",
                    category: isWorkTable
                        ? CdcProviderDiagnosticCategory.WorkTableCaptureViolation
                        : CdcProviderDiagnosticCategory.ValidationMismatch,
                    artifactKind: CdcProviderArtifactKind.SourceTable,
                    safeName: SafeName(observedTable.TableName),
                    expectedValue: "absent",
                    observedValue: "present"
                )
            );
        }

        return diagnostics;
    }

    private static void ValidateTable(
        CdcSourceTableInventory expected,
        CdcSourceTableInventory observed,
        List<CdcProviderDiagnostic> diagnostics
    )
    {
        if (
            !expected.TableName.Equals(observed.TableName)
            || !string.Equals(
                expected.EmittedQuotedTableName,
                observed.EmittedQuotedTableName,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    code: "CDC_SOURCE_TABLE_NAME_MISMATCH",
                    category: CdcProviderDiagnosticCategory.MissingRequiredSourceObject,
                    artifactKind: CdcProviderArtifactKind.SourceTable,
                    safeName: SafeName(expected.TableName),
                    expectedValue: SafeValue(expected.EmittedQuotedTableName),
                    observedValue: SafeValue(observed.EmittedQuotedTableName)
                )
            );
        }

        var observedColumnsByName = observed.Columns.ToDictionary(column => column.ColumnName.Value);

        foreach (var expectedColumn in expected.Columns)
        {
            if (!observedColumnsByName.TryGetValue(expectedColumn.ColumnName.Value, out var observedColumn))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        code: "CDC_SOURCE_COLUMN_MISSING",
                        category: CdcProviderDiagnosticCategory.MissingRequiredSourceObject,
                        artifactKind: CdcProviderArtifactKind.SourceColumn,
                        safeName: SafeName(expected.TableName, expectedColumn.ColumnName),
                        expectedValue: "present",
                        observedValue: "missing"
                    )
                );
                continue;
            }

            if (observedColumn.Ordinal != expectedColumn.Ordinal)
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        code: "CDC_SOURCE_COLUMN_ORDER_MISMATCH",
                        category: CdcProviderDiagnosticCategory.ValidationMismatch,
                        artifactKind: CdcProviderArtifactKind.SourceColumn,
                        safeName: SafeName(expected.TableName, expectedColumn.ColumnName),
                        expectedValue: expectedColumn.Ordinal.ToString(),
                        observedValue: observedColumn.Ordinal.ToString()
                    )
                );
            }

            if (
                !string.Equals(
                    observedColumn.ProviderDataType,
                    expectedColumn.ProviderDataType,
                    StringComparison.Ordinal
                )
            )
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        code: "CDC_SOURCE_COLUMN_TYPE_MISMATCH",
                        category: CdcProviderDiagnosticCategory.ValidationMismatch,
                        artifactKind: CdcProviderArtifactKind.SourceColumn,
                        safeName: SafeName(expected.TableName, expectedColumn.ColumnName),
                        expectedValue: SafeValue(expectedColumn.ProviderDataType),
                        observedValue: SafeValue(observedColumn.ProviderDataType)
                    )
                );
            }

            if (observedColumn.IsNullable != expectedColumn.IsNullable)
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        code: "CDC_SOURCE_COLUMN_NULLABILITY_MISMATCH",
                        category: CdcProviderDiagnosticCategory.ValidationMismatch,
                        artifactKind: CdcProviderArtifactKind.SourceColumn,
                        safeName: SafeName(expected.TableName, expectedColumn.ColumnName),
                        expectedValue: expectedColumn.IsNullable ? "nullable" : "not-null",
                        observedValue: observedColumn.IsNullable ? "nullable" : "not-null"
                    )
                );
            }
        }

        var expectedColumnNames = expected.Columns.Select(column => column.ColumnName.Value).ToHashSet();
        foreach (
            var observedColumn in observed.Columns.Where(column =>
                !expectedColumnNames.Contains(column.ColumnName.Value)
            )
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    code: "CDC_SOURCE_COLUMN_UNEXPECTED",
                    category: CdcProviderDiagnosticCategory.ValidationMismatch,
                    artifactKind: CdcProviderArtifactKind.SourceColumn,
                    safeName: SafeName(observed.TableName, observedColumn.ColumnName),
                    expectedValue: "absent",
                    observedValue: "present"
                )
            );
        }
    }

    private static CdcProviderDiagnostic BuildDiagnostic(
        string code,
        CdcProviderDiagnosticCategory category,
        CdcProviderArtifactKind artifactKind,
        CdcSafeName safeName,
        string? expectedValue,
        string? observedValue
    ) =>
        new(
            Code: code,
            Category: category,
            Severity: CdcProviderDiagnosticSeverity.Error,
            PrincipalKind: CdcPrincipalKind.None,
            ArtifactKind: artifactKind,
            SafeName: safeName,
            ExpectedValue: expectedValue,
            ObservedValue: observedValue,
            ProviderErrorClass: null,
            Classification: CdcProviderRetryContinuityClassification.FailClosed
        );

    private static CdcSafeName SafeName(DbTableName table) =>
        new($"{SafeValue(table.Schema.Value)}.{SafeValue(table.Name)}");

    private static CdcSafeName SafeName(DbTableName table, DbColumnName column) =>
        new($"{SafeValue(table.Schema.Value)}.{SafeValue(table.Name)}.{SafeValue(column.Value)}");

    private static string SafeValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(
                char.IsLetterOrDigit(character)
                || character == '_'
                || character == '.'
                || character == '['
                || character == ']'
                || character == '"'
                    ? character
                    : '_'
            );
        }

        return builder.ToString();
    }
}
