// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Cdc;

internal static class CdcProviderSetupPrerequisiteRules
{
    private const string RedactedValue = "[redacted]";

    internal static IReadOnlyList<CdcConnectorTemplateDiagnostic> Validate(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase
    )
    {
        ArgumentNullException.ThrowIfNull(providerSetupResult);

        List<CdcConnectorTemplateDiagnostic> diagnostics = [];

        AddSourceTableInventoryDiagnostics(
            providerSetupResult,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
        AddExpectedMessageKeyColumnsDiagnostics(
            providerSetupResult,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
        AddProviderArtifactDiagnostics(
            providerSetupResult,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );
        AddBlankHeartbeatActionQueryDiagnosticIfNeeded(
            providerSetupResult,
            safeArtifactOrObjectName,
            sourcePhase,
            diagnostics
        );

        return diagnostics;
    }

    private static void AddBlankHeartbeatActionQueryDiagnosticIfNeeded(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (
            providerSetupResult.HeartbeatActionQuery is null
            || !string.IsNullOrWhiteSpace(providerSetupResult.HeartbeatActionQuery.Sql)
        )
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired,
                CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation,
                "providerSetup.heartbeatActionQuery",
                "fresh provider heartbeat action query",
                RedactedValue,
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static void AddSourceTableInventoryDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory =
            providerSetupResult.SourceTableInventory;

        if (
            sourceTableInventory is not null
            && CdcConnectorTemplateSharedRules.HasRequiredSourceInventory(sourceTableInventory)
        )
        {
            AddSourceTableNameDiagnostics(
                providerSetupResult,
                sourceTableInventory,
                safeArtifactOrObjectName,
                sourcePhase,
                diagnostics
            );
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                "providerSetup.sourceTableInventory",
                "dms.DocumentCache, dms.Document, and dms.CdcHeartbeat",
                ObservedCountOrMissing(sourceTableInventory),
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static void AddSourceTableNameDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        IReadOnlyList<CdcSourceTableInventory> sourceTableInventory,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        foreach (
            CdcSourceTableKind tableKind in CdcConnectorTemplateSharedRules.OrderedRequiredSourceTableKinds
        )
        {
            CdcSourceTableInventory sourceTable = sourceTableInventory.Single(table =>
                table.TableKind == tableKind
            );
            var expectedTableName = CdcConnectorTemplateSharedRules.ExpectedSourceTableName(tableKind);
            if (sourceTable.TableName.Equals(expectedTableName))
            {
                continue;
            }

            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch,
                    CdcConnectorTemplateDiagnosticCategory.IncludeListViolation,
                    "table.include.list",
                    $"{expectedTableName.Schema.Value}.{expectedTableName.Name}",
                    SanitizePhysicalIdentifier(
                        $"{sourceTable.TableName.Schema.Value}.{sourceTable.TableName.Name}"
                    ),
                    providerSetupResult.Provider,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }
    }

    private static void AddExpectedMessageKeyColumnsDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns =
            providerSetupResult.ExpectedMessageKeyColumns;
        if (
            expectedMessageKeyColumns is null
            || !CdcConnectorTemplateSharedRules.HasExpectedMessageKeyColumns(expectedMessageKeyColumns)
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                    CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation,
                    "providerSetup.expectedMessageKeyColumns",
                    "DocumentUuid keys for document sources",
                    ObservedCountOrMissing(expectedMessageKeyColumns),
                    providerSetupResult.Provider,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
            return;
        }

        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory =
            providerSetupResult.SourceTableInventory;
        if (
            sourceTableInventory is null
            || !CdcConnectorTemplateSharedRules.HasRequiredSourceInventory(sourceTableInventory)
        )
        {
            return;
        }

        foreach (CdcExpectedMessageKeyColumns messageKeyColumns in expectedMessageKeyColumns)
        {
            CdcSourceTableInventory sourceTable = sourceTableInventory.Single(table =>
                table.TableKind == messageKeyColumns.TableKind
            );

            if (HasMalformedSourceColumns(sourceTable))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                        CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation,
                        "providerSetup.sourceTableInventory.columns",
                        $"non-null source column inventory for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                        "malformed",
                        providerSetupResult.Provider,
                        safeArtifactOrObjectName,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                    )
                );
                continue;
            }

            if (HasDuplicateColumnNames(sourceTable))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                        CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation,
                        "message.key.columns",
                        $"unique source column names for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                        "duplicate",
                        providerSetupResult.Provider,
                        safeArtifactOrObjectName,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                    )
                );
            }

            foreach (DbColumnName keyColumn in messageKeyColumns.KeyColumns)
            {
                if (CountSourceColumns(sourceTable, keyColumn) > 0)
                {
                    continue;
                }

                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                        CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation,
                        "message.key.columns",
                        $"source column {keyColumn.Value} for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(sourceTable.TableKind)}",
                        "missing",
                        providerSetupResult.Provider,
                        safeArtifactOrObjectName,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                    )
                );
            }
        }
    }

    private static void AddProviderArtifactDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (providerSetupResult.Provider == CdcProvider.Postgresql)
        {
            AddMissingArtifactDiagnosticIfNeeded(
                providerSetupResult,
                safeArtifactOrObjectName,
                sourcePhase,
                diagnostics,
                CdcProviderArtifactKind.PostgresqlPublication,
                CdcConnectorTemplateDiagnosticCodes.PostgresqlPublicationMetadataRequired,
                "publication.name"
            );
            AddMissingArtifactDiagnosticIfNeeded(
                providerSetupResult,
                safeArtifactOrObjectName,
                sourcePhase,
                diagnostics,
                CdcProviderArtifactKind.PostgresqlReplicationSlot,
                CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired,
                "slot.name"
            );
        }
        else if (providerSetupResult.Provider == CdcProvider.SqlServer)
        {
            AddSqlServerCaptureInstanceDiagnostics(
                providerSetupResult,
                safeArtifactOrObjectName,
                sourcePhase,
                diagnostics
            );
        }
    }

    private static void AddMissingArtifactDiagnosticIfNeeded(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics,
        CdcProviderArtifactKind artifactKind,
        string code,
        string propertyName
    )
    {
        CdcProviderArtifactObservation[] artifacts = MatchingUsableArtifacts(
            providerSetupResult,
            artifactKind
        );
        if (artifacts.Length == 1)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                code,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                propertyName,
                "one matched provider setup artifact",
                artifacts.Length == 0 ? "missing" : artifacts.Length.ToString(),
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.Safe
            )
        );
    }

    private static void AddSqlServerCaptureInstanceDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        CdcProviderArtifactObservation[] captureInstances = CdcConnectorTemplateSharedRules
            .ArtifactInventory(providerSetupResult)
            .Where(artifact => artifact.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance)
            .ToArray();

        foreach (
            CdcSourceTableKind tableKind in CdcConnectorTemplateSharedRules.OrderedRequiredSourceTableKinds
        )
        {
            CdcProviderArtifactObservation[] tableCaptureInstances = captureInstances
                .Where(artifact =>
                    CdcConnectorTemplateSharedRules.SqlServerCaptureInstanceSourceTableKind(artifact)
                    == tableKind
                )
                .ToArray();

            if (
                tableCaptureInstances.Length == 1
                && tableCaptureInstances[0].State
                    is CdcProviderArtifactState.Created
                        or CdcProviderArtifactState.Matched
            )
            {
                continue;
            }

            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.artifactInventory.sqlServerCaptureInstance",
                    $"one usable SQL Server capture-instance artifact for {CdcConnectorTemplateSharedRules.ExpectedSourceTableName(tableKind)}",
                    SqlServerCaptureInstanceObservedValue(tableCaptureInstances),
                    providerSetupResult.Provider,
                    safeArtifactOrObjectName,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        int extraCaptureInstanceCount = captureInstances.Count(artifact =>
            CdcConnectorTemplateSharedRules.SqlServerCaptureInstanceSourceTableKind(artifact)
                is not { } tableKind
            || !CdcConnectorTemplateSharedRules.OrderedRequiredSourceTableKinds.Contains(tableKind)
        );
        if (extraCaptureInstanceCount == 0)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                "providerSetup.artifactInventory.sqlServerCaptureInstance",
                "only SQL Server capture-instance artifacts for dms.DocumentCache, dms.Document, and dms.CdcHeartbeat",
                extraCaptureInstanceCount.ToString(),
                providerSetupResult.Provider,
                safeArtifactOrObjectName,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static CdcConnectorTemplateDiagnostic BuildDiagnostic(
        string code,
        CdcConnectorTemplateDiagnosticCategory category,
        string propertyName,
        string? expectedValue,
        string? observedValue,
        CdcProvider provider,
        CdcSafeName? safeArtifactOrObjectName,
        CdcConnectorTemplateSourcePhase sourcePhase,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        new(
            code,
            category,
            CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName,
            safeArtifactOrObjectName,
            expectedValue,
            observedValue,
            provider,
            sourcePhase,
            redactionClassification
        );

    private static CdcProviderArtifactObservation[] MatchingUsableArtifacts(
        CdcProviderSetupResult providerSetupResult,
        CdcProviderArtifactKind artifactKind
    ) =>
        CdcConnectorTemplateSharedRules
            .ArtifactInventory(providerSetupResult)
            .Where(artifact =>
                artifact.ArtifactKind == artifactKind
                && artifact.State is CdcProviderArtifactState.Created or CdcProviderArtifactState.Matched
            )
            .ToArray();

    private static bool HasMalformedSourceColumns(CdcSourceTableInventory sourceTable) =>
        sourceTable.Columns is null || sourceTable.Columns.Any(column => column is null);

    private static bool HasDuplicateColumnNames(CdcSourceTableInventory sourceTable) =>
        sourceTable
            .Columns.GroupBy(column => column.ColumnName.Value, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);

    private static int CountSourceColumns(CdcSourceTableInventory sourceTable, DbColumnName columnName) =>
        sourceTable.Columns.Count(column =>
            string.Equals(column.ColumnName.Value, columnName.Value, StringComparison.Ordinal)
        );

    private static string SqlServerCaptureInstanceObservedValue(
        IReadOnlyList<CdcProviderArtifactObservation> tableCaptureInstances
    )
    {
        if (tableCaptureInstances.Count == 0)
        {
            return "missing";
        }

        if (tableCaptureInstances.Count > 1)
        {
            return tableCaptureInstances.Count.ToString();
        }

        return tableCaptureInstances[0].State.ToString();
    }

    private static string ObservedCountOrMissing<T>(IReadOnlyList<T>? values) =>
        values is null || values.Count == 0 ? "missing" : values.Count.ToString();

    private static string SanitizePhysicalIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RedactedValue;
        }

        return new string(
            value
                .Select(character =>
                    char.IsLetterOrDigit(character) || character is '_' or '.' ? character : '_'
                )
                .ToArray()
        );
    }
}
